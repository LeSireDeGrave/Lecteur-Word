using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public sealed record PageGeometry(float Width=Composition.Width,float Height=Composition.Height,float Left=Composition.Margin,float Top=Composition.Margin,float Right=Composition.Margin,float Bottom=Composition.Margin)
{
    public float BodyHeight=>Height-Top-Bottom;
}
public sealed record LayoutStyle(float Size=12,bool Bold=false,bool Italic=false,bool Underline=false,string Color="000000",string RequestedFont="DejaVu Serif")
{
    internal string? UnderlineColor {get;init;}
    public WordFontSlots FontSlots {get;init;}=new();
    public FontResolution? Font {get;init;}
    public float? ComplexSize {get;init;}
    public bool? ComplexBold {get;init;}
    public bool? ComplexItalic {get;init;}
    public string ResolvedFont=>Font?.UsedFamily??"indisponible";
    public string ResolvedFace=>Font is null?"indisponible":$"{Font.Weight}/{Font.Slant}";
}
public sealed record LayoutRun(string Id,SourceLocation Source,string Text,LayoutStyle Style,string Kind="text")
{
    public int Offset {get;init;}
    public InlineImage? Image {get;init;}
    internal FloatingPicture? FloatingPicture {get;init;}
    internal FloatingTextFrame? FloatingTextFrame {get;init;}
}
public sealed record ParagraphLayout(string Alignment="left",float Left=0,float Right=0,float FirstLine=0,float Before=0,float After=6,float Line=240,string LineRule="auto",bool KeepNext=false,bool KeepLines=false,bool Widow=true,bool PageBreakBefore=false,int Heading=0)
{
    public string? NumId {get;init;}
    public int ListLevel {get;init;}
    public IReadOnlyList<TabStop> Tabs {get;init;}=[];
    public float DefaultTab {get;init;}=36;
    public bool ContextualSpacing {get;init;}
    internal ParagraphBorderStroke? BottomBorder {get;init;}
    internal ParagraphBorderStroke? TopBorder {get;init;}
    internal ParagraphBorderStroke? LeftBorder {get;init;}
    internal ParagraphBorderStroke? RightBorder {get;init;}
    internal ParagraphBorderStroke? BetweenBorder {get;init;}
    internal bool ContinuesSideBorders {get;init;}
    internal bool HasBorders=>TopBorder is not null||BottomBorder is not null||LeftBorder is not null||
        RightBorder is not null||BetweenBorder is not null;
    internal SourceLocation? BeforeAutoSpacing {get;init;}
    internal SourceLocation? AfterAutoSpacing {get;init;}
    internal ParagraphShading? Shading {get;init;}
}
internal sealed record ParagraphShading(SourceLocation Source,string Color);
internal sealed record ParagraphBorderStroke(SourceLocation Source,float Width,float Space,string Color)
{
    internal float Height=>Space+Width;
}
public sealed record LayoutParagraph(string Id,SourceLocation Source,IReadOnlyList<LayoutRun> Runs,ParagraphLayout Properties,int Section,PageGeometry Geometry,string SectionStart)
{
    internal bool SuppressBeforeAfterStandaloneBreak {get;init;}
    internal FlowColumns? FlowColumns {get;init;}
    internal FlowExclusion? FlowExclusion {get;init;}
    internal SideExclusion? SideExclusion {get;init;}
    public string? CellOwner {get;init;}
    public string? TableId {get;init;}
    public ListLabel? List {get;init;}
    public string StyleId {get;init;}="";
    public string Text=>string.Concat(Runs.Where(r=>r.Kind!="listMarker").Select(r=>r.Text));
}
public sealed record PaginatedDocument(IReadOnlyList<LayoutParagraph> Paragraphs,IReadOnlyList<FlowIssue> Issues)
{
    public DocumentFonts Fonts {get;init;}=new();



    public IReadOnlyList<LayoutTable> Tables {get;init;}=[];
    internal IReadOnlyList<SmartArtPresentation> SmartArt {get;init;}=[];
    internal IReadOnlyList<PageGeometry> SectionFormats {get;init;}=[];
    public const string Profile="docx-flow-layout-v2";
    public bool Available=>!Issues.Any(i=>i.BlocksRendering);
}

/// <summary>Bounded paragraph/section profile, independent of simplified reading and export qualification.</summary>
public static partial class LayoutReader
{
    const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static PaginatedDocument Read(InspectionResult input,CancellationToken token=default)=>new Reader(input,token).Read();
    internal static PaginatedDocument Read(InspectionResult input,SemanticDocument semantic,CancellationToken token,TableStyleCascade tableStyles) =>
        new Reader(input,token,semantic,tableStyles).Read();
    internal sealed partial class Reader
    {
        readonly InspectionResult input;readonly CancellationToken token;
        readonly List<FlowIssue> issues=[];readonly HashSet<string> issueKeys=[];
        readonly Dictionary<string,InventoryNode> nodes;readonly ILookup<string,InventoryNode> children;
        readonly Dictionary<string,InventoryNode> styles;
        readonly Dictionary<string,List<InventoryNode>> chains=[];
        readonly SemanticDocument? semantic;
        TextStyleResolver? characterEffects;
        readonly Dictionary<string,SemanticNumbering>? semanticNumbering;
        readonly TableStyleCascade? tableCascade;
        LayoutStyle defaults=new();ParagraphLayout paragraphDefaults=new();
        InventoryNode? defaultParagraphMark;
        bool readingStory,runningStory,textBoxStory;
        bool? fixedAutoSpacing;
        readonly bool nativeComposition;
        readonly bool styleCompositionComparison;
        readonly bool hasLineNumbering;
        readonly IReadOnlySet<string>? calculatedBodyFields;
        readonly IReadOnlySet<string>? placedFootnoteCalls;
        readonly bool endnotesPlanned;
        readonly IReadOnlyDictionary<string,GeneratedTocRow[]>? generatedTocs;
        readonly IReadOnlyDictionary<string,string>? tocContinuations;
        public Reader(InspectionResult input,CancellationToken token,SemanticDocument? semantic=null,TableStyleCascade? tableCascade=null,
            bool nativeComposition=false,IReadOnlySet<string>? calculatedBodyFields=null,IReadOnlySet<string>? placedFootnoteCalls=null,
            IReadOnlyDictionary<string,GeneratedTocRow[]>? generatedTocs=null,IReadOnlyDictionary<string,string>? tocContinuations=null,
            bool endnotesPlanned=false,bool styleCompositionComparison=false)
        {
            this.input=input;this.token=token;nodes=input.Manifest.Nodes.ToDictionary(n=>n.Id);children=input.Manifest.Nodes.Where(n=>n.ParentId is not null).ToLookup(n=>n.ParentId!);
            this.semantic=semantic;
            this.tableCascade=tableCascade;
            this.nativeComposition=nativeComposition;
            this.styleCompositionComparison=styleCompositionComparison;
            hasLineNumbering=nativeComposition&&input.Manifest.Nodes.Any(n=>Is(n,"lnNumType")&&n.Source.PartUri==semantic?.MainPart);
            this.calculatedBodyFields=calculatedBodyFields;
            this.placedFootnoteCalls=placedFootnoteCalls;
            this.endnotesPlanned=endnotesPlanned;
            this.generatedTocs=generatedTocs;
            this.tocContinuations=tocContinuations;
            if(semantic is not null)
            {
                if(input.Manifest.SourceSha256!=semantic.SnapshotId)throw new InvalidDataException("PAGE_LAYOUT_SNAPSHOT");
                paragraphDefaults=paragraphDefaults with{After=0};
                semanticNumbering=semantic.Numbering.ToDictionary(n=>n.ParagraphId,StringComparer.Ordinal);
            }
            var definitions=semantic is null?input.Manifest.Nodes.Where(n=>Is(n,"style")):
                semantic.Styles.Select(d=>nodes[d.Id]);
            styles=definitions
                .GroupBy(n=>A(n,"styleId")).Where(g=>g.Key.Length>0&&(semantic is null||g.Count()==1)).ToDictionary(g=>g.Key,g=>g.First());
        }
        internal PaginatedDocument ReadStory(SemanticStory story,PageGeometry geometry)
        {
            token.ThrowIfCancellationRequested();
            issues.Clear();issueKeys.Clear();tables.Clear();counters.Clear();smartArt.Clear();
            var output=new List<LayoutParagraph>();
            readingStory=true;
            runningStory=story.Kind is "hdr" or "ftr";
            textBoxStory=story.Kind=="txbxContent";
            try
            {
                foreach(var element in semantic!.BlocksOf(story.RootId))
                {
                    token.ThrowIfCancellationRequested();
                    var node=nodes[element.Id];
                    if(node.Kind=="textValue"&&string.IsNullOrWhiteSpace(node.Text))continue;
                    if(Is(node,"tbl"))ReadTable(node,1,geometry,"nextPage",output);
                    else if(Is(node,"p"))output.Add(ReadParagraph(node,1,geometry,"nextPage"));
                    else Issue("PAGE_STORY_BLOCK_UNQUALIFIED",node,"Bloc auxiliaire hors profil de composition.");
                    if(output.Count>2_048)
                    {
                        Issue("PAGE_STORY_PARAGRAPH_LIMIT",nodes[story.RootId],"Budget de paragraphes de l’histoire dépassé.");
                        break;
                    }
                }
                return new(output,issues.ToArray()){Tables=tables.ToArray(),SmartArt=smartArt.ToArray()};
            }
            finally{readingStory=runningStory=textBoxStory=false;}
        }
        static bool Is(InventoryNode n,string local)=>n.Name=="{"+W+"}"+local;
        static string A(InventoryNode n,string key)=>n.Attributes.GetValueOrDefault("{"+W+"}"+key)??"";
        InventoryNode? Child(InventoryNode n,string local)=>PropertyChildren(n).FirstOrDefault(c=>Is(c,local));
        IEnumerable<InventoryNode> PropertyChildren(InventoryNode n)=>nativeComposition&&semantic is not null?
            semantic.StylePropertyChildren(n.Id):children[n.Id];
        void Issue(string code,InventoryNode? n,string message,bool blocks=true)
        {
            if(blocks&&checkingListDefinition)invalidListDefinition=true;
            if(issueKeys.Add(code+":"+n?.Id))issues.Add(new(code,n?.Source,message,blocks));
        }
        bool Toggle(InventoryNode n)=>A(n,"val") is not ("0" or "false" or "off");
        float Number(InventoryNode n,string key,float fallback,float min,float max,float divisor=1)
        {
            string value=A(n,key);if(value.Length==0)return fallback;
            if(!float.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out float result)||result/divisor<min||result/divisor>max)
            {Issue("LAYOUT_PROPERTY_RANGE",n,"Propriété numérique hors profil : "+key);return fallback;}
            return result/divisor;
        }
        void Attributes(InventoryNode n,params string[] allowed)
        {
            foreach(var key in n.Attributes.Keys)
                if(!key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal)&&key is not ("{http://schemas.microsoft.com/office/word/2010/wordml}paraId" or "{http://schemas.microsoft.com/office/word/2010/wordml}textId")&&!allowed.Contains(key,StringComparer.Ordinal))
                {
                    if(key=="{http://schemas.microsoft.com/office/word/2016/wordml/cid}durableId"||
                        key=="{http://schemas.microsoft.com/office/word/2012/wordml}restartNumberingAfterBreak"&&n.Attributes[key] is "0" or "false"||
                        nativeComposition&&key=="{"+W+"}rsidDel"&&(Is(n,"p")||Is(n,"r")||Is(n,"tr"))&&
                        n.Attributes[key] is {Length:8} revision&&revision.All(Uri.IsHexDigit))
                        Issue("LAYOUT_INFORMATIONAL_ATTRIBUTE",n,"Métadonnée sans effet sur la composition, conservée : "+key,false);
                    else Issue("LAYOUT_ATTRIBUTE_UNQUALIFIED",n,"Propriété hors profil paginé : "+key);
                }
        }
        void WAttributes(InventoryNode n,params string[] allowed)=>Attributes(n,allowed.Select(k=>"{"+W+"}"+k).ToArray());
        IReadOnlyList<InventoryNode> Chain(string id,InventoryNode? reference=null)
        {
            if(nativeComposition&&semantic is not null)
            {
                if(reference is null&&id.Length==0)return [];
                var useId=reference?.Id??styles.GetValueOrDefault(id)?.Id;
                var use=useId is null?null:semantic.StyleUse(useId);
                if(use?.Resolved!=true)
                    Issue("LAYOUT_STYLE_CHAIN_UNRESOLVED",reference??(useId is null?null:nodes[useId]),
                        "Style absent, incompatible ou chaîne d’héritage ambiguë : "+id+".");
                return use is null?[]:use.BaseToDerivedIds.Select(source=>nodes[source]).ToArray();
            }
            if(chains.TryGetValue(id,out var cached))return cached;
            var result=new List<InventoryNode>();var seen=new HashSet<string>();string current=id;
            while(current.Length>0)
            {
                token.ThrowIfCancellationRequested();
                if(result.Count>=32||!seen.Add(current)){Issue("LAYOUT_STYLE_CYCLE",null,"Chaîne de styles cyclique ou trop profonde.");break;}
                if(!styles.TryGetValue(current,out var style)){Issue("LAYOUT_STYLE_MISSING",null,"Style référencé absent : "+current);break;}
                result.Add(style);current=Child(style,"basedOn") is {} parent?A(parent,"val"):"";
            }
            result.Reverse();chains[id]=result;return result;
        }
        LayoutStyle Character(InventoryNode? property,LayoutStyle inherited,bool inStyle=false)
        {
            if(property is null)return inherited;WAttributes(property);var style=inherited;
            HashSet<string>? visibility=null;
            foreach(var n in PropertyChildren(property))
            {
                if(n.Name.StartsWith('#'))continue;
                if(Is(n,"b")||Is(n,"i"))
                {
                    WAttributes(n,"val");bool value=Toggle(n);
                    if(Is(n,"b"))style=style with{Bold=inStyle?(value?!style.Bold:style.Bold):value};
                    else style=style with{Italic=inStyle?(value?!style.Italic:style.Italic):value};
                }
                else if(Is(n,"u"))
                {
                    WAttributes(n,"val","color","themeColor","themeTint","themeShade");
                    if(n.Attributes.TryGetValue("{"+W+"}val",out string? kind))
                    {
                        if(kind is "single" or "none"||nativeComposition&&kind=="double")
                            style=style with{Underline=kind!="none"};
                        else Issue("LAYOUT_UNDERLINE_UNQUALIFIED",n,"Type de soulignement hors profil.");
                    }
                    if(n.Attributes.ContainsKey("{"+W+"}color")||A(n,"themeColor") is not ("" or "none"))
                    {
                        string color=ReadColor(n,"color",automatic:"auto");
                        if(color=="auto")style=style with{UnderlineColor=null};
                        else if(color.Length==6&&color.All(Uri.IsHexDigit))style=style with{UnderlineColor=color};
                        else Issue("LAYOUT_COLOR_UNQUALIFIED",n,"Couleur de soulignement hors profil.");
                    }
                }
                else if(Is(n,"sz")){WAttributes(n,"val");style=style with{Size=Number(n,"val",style.Size,6,72,2)};}
                else if(nativeComposition&&Is(n,"vertAlign")&&A(n,"val") is "baseline" or "superscript" or "subscript")
                    WAttributes(n,"val");
                else if(nativeComposition&&(Is(n,"caps")||Is(n,"smallCaps")))
                {
                    WAttributes(n,"val");
                    if(TextStyleResolver.OnOff(n.Attributes.GetValueOrDefault("{"+W+"}val")) is null||
                        Elements(n).Any()||Child(property,Is(n,"caps")?"smallCaps":"caps") is not null)
                        Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Déclaration des capitales invalide ou contradictoire.");
                }
                else if(nativeComposition&&n.Name=="{"+TextStyleResolver.W14+"}ligatures")
                {
                    string val="{"+TextStyleResolver.W14+"}val";
                    Attributes(n,val);
                    if(Elements(n).Any()||!TextStyleResolver.IsLigatureValue(n.Attributes.GetValueOrDefault(val)))
                        Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Valeur de ligatures hors profil.");
                }
                else if(nativeComposition&&semantic is not null&&TextStyleResolver.IsGlyphEffect(n.Name))
                {
                    characterEffects??=new(semantic,(_,_,_)=>{});
                    if(!characterEffects.SupportsGlyphEffect(n.Id,n.Name))
                        Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Effet de glyphe hors profil de dessin natif.");
                }
                else if(nativeComposition&&Is(n,"bdr")&&A(n,"val") is "none" or "nil")
                {
                    WAttributes(n,"val","sz","space","color","themeColor","themeTint","themeShade","frame","shadow");
                    if(Elements(n).Any())Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Bordure de caractères sans trait invalide.");
                }
                else if(Is(n,"rFonts"))
                {
                    WAttributes(n,"ascii","hAnsi","cs","eastAsia","asciiTheme","hAnsiTheme","cstheme","csTheme","eastAsiaTheme","hint");
                    style=style with{FontSlots=WordFontSlots.Apply(style.FontSlots,n)};
                    string family=A(n,"ascii");if(family.Length==0)family=A(n,"hAnsi");if(family.Length==0)family=A(n,"asciiTheme");
                    if(family.Length>128){Issue("LAYOUT_FONT_NAME_LIMIT",n,"Nom de police hors limites.");family="";}
                    if(family.Length>0)style=style with{RequestedFont=family};
                }
                else if(Is(n,"color")){WAttributes(n,"val","themeColor","themeTint","themeShade");string color=ReadColor(n);if(color.Length!=6||!color.All(Uri.IsHexDigit))Issue("LAYOUT_COLOR_UNQUALIFIED",n,"Couleur hors profil.");else style=style with{Color=color};}
                else if(Is(n,"lang")){WAttributes(n,"val","eastAsia","bidi");}
                else if(Is(n,"rStyle")){WAttributes(n,"val");}
                else if(nativeComposition&&TextStyleResolver.IsVisibility(n.Name))
                {
                    WAttributes(n,"val");
                    visibility??=new(StringComparer.Ordinal);
                    if(!visibility.Add(n.Name)||Elements(n).Any()||TextStyleResolver.OnOff(n.Attributes.GetValueOrDefault("{"+W+"}val")) is null)
                        Issue("TEXT_VISIBILITY_UNRESOLVED",n,"Déclaration de visibilité invalide ou ambiguë.");
                }
                else if(Is(n,"noProof")){WAttributes(n,"val");Issue("LAYOUT_PROOFING_METADATA",n,"Option de correction orthographique conservée ; aucun effet sur la composition.",false);}
                else if(Is(n,"spacing")&&(A(n,"val")=="0"||nativeComposition&&
                    int.TryParse(A(n,"val"),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int spacing)&&
                    spacing is >=-20 and <=20))
                {
                    WAttributes(n,"val");
                    if(Elements(n).Any())Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Espacement de caractères imbriqué hors profil.");
                }
                else if(nativeComposition&&(Is(n,"kern")||Is(n,"position")))
                {
                    WAttributes(n,"val");
                    bool kerning=Is(n,"kern");
                    if(Elements(n).Any()||!int.TryParse(A(n,"val"),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int count)||
                        (kerning?count is <0 or >3276:count is <-3168 or >3168))
                        Issue("LAYOUT_CHARACTER_PROPERTY_RANGE",n,kerning?"Seuil de crénage invalide.":"Position de ligne de base invalide.");
                }
                else if(nativeComposition&&Is(n,"w"))
                {
                    WAttributes(n,"val");
                    if(Elements(n).Any()||!int.TryParse(A(n,"val"),NumberStyles.None,CultureInfo.InvariantCulture,out int scale)||scale is <1 or >600)
                        Issue("LAYOUT_CHARACTER_PROPERTY_RANGE",n,"Échelle horizontale invalide.");
                }
                else if(semantic is not null&&Is(n,"strike")&&A(n,"val") is "0" or "false" or "off")
                    WAttributes(n,"val");
                else if(nativeComposition&&(Is(n,"dstrike")&&A(n,"val") is ("0" or "false" or "off")||Is(n,"em")&&A(n,"val")=="none"))
                {
                    WAttributes(n,"val");
                    if(Elements(n).Any())Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Déclaration de caractère hors profil.");
                }
                else if(nativeComposition&&Is(n,"shd")&&A(n,"val") is "clear" or "nil")
                {
                    WAttributes(n,"val","fill","color","themeFill","themeFillTint","themeFillShade","themeColor","themeTint","themeShade");
                    string fill=A(n,"val")=="nil"?"":ReadColor(n,"fill","themeFill","themeFillTint","themeFillShade",automatic:"");
                    if(Elements(n).Any()||fill.Length>0&&(fill.Length!=6||!fill.All(Uri.IsHexDigit)))
                        Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Trame de caractères hors profil.");
                }
                else if(semantic is not null&&Is(n,"shd")&&A(n,"val")=="clear"&&A(n,"fill")=="auto"&&A(n,"themeFill").Length==0)
                    WAttributes(n,"val","fill","color");
                else if(Is(n,"bCs")||Is(n,"iCs")||Is(n,"szCs"))
                {
                    WAttributes(n,"val");style=Is(n,"szCs")?style with{ComplexSize=Number(n,"val",style.Size,6,72,2)}:Is(n,"bCs")?style with{ComplexBold=Toggle(n)}:style with{ComplexItalic=Toggle(n)};
                    Issue("LAYOUT_LATIN_ONLY",n,"Propriété de script complexe conservée ; profil latin seulement.",false);
                }
                else Issue("LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED",n,"Propriété de caractère non qualifiée : "+n.Name);
            }
            return style;
        }
        ParagraphLayout Paragraph(InventoryNode? property,ParagraphLayout inherited)
        {
            if(property is null)return inherited;WAttributes(property);var p=inherited;
            foreach(var n in PropertyChildren(property))
            {
                if(n.Name.StartsWith('#'))continue;
                if(Is(n,"jc")){WAttributes(n,"val");string v=A(n,"val");if(v is not ("left" or "center" or "right" or "both"))Issue("LAYOUT_ALIGNMENT_UNQUALIFIED",n,"Alignement non qualifié : "+v);else p=p with{Alignment=v};}
                else if(Is(n,"tabs")){p=ReadTabs(n,p);}
                else if(Is(n,"spacing"))
                {
                    if(nativeComposition)WAttributes(n,"before","after","line","lineRule","beforeAutospacing","afterAutospacing");
                    else WAttributes(n,"before","after","line","lineRule");
                    string rule=A(n,"lineRule");if(rule.Length==0)rule=p.LineRule;
                    if(rule is not ("auto" or "exact" or "atLeast"))Issue("LAYOUT_LINE_RULE_UNQUALIFIED",n,"Interligne hors profil.");
                    p=p with{Before=Number(n,"before",p.Before,0,288,20),After=Number(n,"after",p.After,0,288,20),Line=Number(n,"line",p.Line,1,rule=="auto"?1440:4000),LineRule=rule};
                    if(nativeComposition)p=p with
                    {
                        BeforeAutoSpacing=Auto("beforeAutospacing",p.BeforeAutoSpacing),
                        AfterAutoSpacing=Auto("afterAutospacing",p.AfterAutoSpacing)
                    };

                    SourceLocation? Auto(string key,SourceLocation? previous)
                    {
                        if(!n.Attributes.TryGetValue("{"+W+"}"+key,out var raw))return previous;
                        if(TextStyleResolver.OnOff(raw) is {} enabled)return enabled?n.Source:null;
                        Issue("LAYOUT_AUTOMATIC_SPACING_UNQUALIFIED",n,"Valeur d’espacement automatique invalide.");
                        return previous;
                    }
                }
                else if(Is(n,"ind")){WAttributes(n,"left","right","start","end","firstLine","hanging");if(A(n,"firstLine").Length>0&&A(n,"hanging").Length>0)Issue("LAYOUT_INDENT_CONFLICT",n,"Retraits contradictoires.");p=p with{Left=Number(n,A(n,"start").Length>0?"start":"left",p.Left,-720,720,20),Right=Number(n,A(n,"end").Length>0?"end":"right",p.Right,-720,720,20),FirstLine=A(n,"hanging").Length>0?-Number(n,"hanging",0,0,720,20):Number(n,"firstLine",p.FirstLine,-720,720,20)};}
                else if(Is(n,"contextualSpacing")){WAttributes(n,"val");p=p with{ContextualSpacing=Toggle(n)};}
                else if(nativeComposition&&Is(n,"pBdr"))
                {
                    WAttributes(n);
                    var sides=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var edge in PropertyChildren(n))
                    {
                        if(edge.Name.StartsWith('#'))continue;
                        if(!sides.Add(edge.Name)||!(Is(edge,"bottom")||Is(edge,"top")||Is(edge,"left")||Is(edge,"right")||
                            textBoxStory&&Is(edge,"between")))
                        {Issue("LAYOUT_PARAGRAPH_BORDER_UNQUALIFIED",edge,"Côté de bordure répété ou hors profil.");continue;}
                        WAttributes(edge,"val","sz","space","color","themeColor","themeTint","themeShade");
                        if(Elements(edge).Any())
                        {Issue("LAYOUT_PARAGRAPH_BORDER_UNQUALIFIED",edge,"Contenu de bordure inattendu.");continue;}
                        string kind=A(edge,"val");
                        if(kind is "nil" or "none"){p=WithBorder(null);continue;}
                        if(kind!="single"||A(edge,"sz").Length==0)
                        {Issue("LAYOUT_PARAGRAPH_BORDER_UNQUALIFIED",edge,"Style ou épaisseur de bordure hors profil.");continue;}
                        string color=ReadColor(edge,"color");
                        if(color.Length!=6||!color.All(Uri.IsHexDigit))
                        {Issue("LAYOUT_BORDER_COLOR_UNQUALIFIED",edge,"Couleur de bordure non résolue.");continue;}
                        p=WithBorder(new(edge.Source,Number(edge,"sz",.5f,.25f,12,8),Number(edge,"space",0,0,31),color));

                        ParagraphLayout WithBorder(ParagraphBorderStroke? stroke)=>Is(edge,"bottom")?p with{BottomBorder=stroke}:
                            Is(edge,"top")?p with{TopBorder=stroke}:Is(edge,"left")?p with{LeftBorder=stroke}:
                            Is(edge,"right")?p with{RightBorder=stroke}:p with{BetweenBorder=stroke};
                    }
                }
                else if(nativeComposition&&Is(n,"shd")&&A(n,"val") is "clear" or "nil")
                {
                    WAttributes(n,"val","fill","color","themeFill","themeFillTint","themeFillShade","themeColor","themeTint","themeShade");
                    string color=A(n,"val")=="nil"?"":ReadColor(n,"fill","themeFill","themeFillTint","themeFillShade",automatic:"");
                    if(Elements(n).Any()||color.Length>0&&(color.Length!=6||!color.All(Uri.IsHexDigit)))
                        Issue("LAYOUT_PARAGRAPH_PROPERTY_UNQUALIFIED",n,"Trame de paragraphe hors profil.");
                    else p=p with{Shading=color.Length==0?null:new(n.Source,color)};
                }
                else if(nativeComposition&&Is(n,"suppressLineNumbers")&&!hasLineNumbering&&
                    !Elements(n).Any()&&TextStyleResolver.OnOff(n.Attributes.GetValueOrDefault("{"+W+"}val")) is not null)
                {
                    WAttributes(n,"val");
                    Issue("LAYOUT_LINE_NUMBERING_INACTIVE",n,"Option conservée. Le document ne déclare aucune numérotation de lignes.",false);
                }
                else if(Is(n,"rPr")){WAttributes(n); /* Read separately as paragraph-mark formatting, never as text formatting. */}
                else if((Is(n,"autoSpaceDE")||Is(n,"autoSpaceDN")||Is(n,"adjustRightInd")||Is(n,"snapToGrid"))&&!Toggle(n)){WAttributes(n,"val");Issue("LAYOUT_DISABLED_PARAGRAPH_OPTION",n,"Option explicitement désactivée, conservée : "+n.Name,false);}
                else if(Is(n,"keepNext")){WAttributes(n,"val");p=p with{KeepNext=Toggle(n)};}
                else if(Is(n,"keepLines")){WAttributes(n,"val");p=p with{KeepLines=Toggle(n)};}
                else if(Is(n,"widowControl")){WAttributes(n,"val");p=p with{Widow=Toggle(n)};}
                else if(semantic is not null&&Is(n,"suppressAutoHyphens"))WAttributes(n,"val");
                else if(Is(n,"pageBreakBefore")){WAttributes(n,"val");p=p with{PageBreakBefore=Toggle(n)};}
                else if(Is(n,"outlineLvl")){WAttributes(n,"val");int level=(int)Number(n,"val",9,0,9);p=p with{Heading=level==9?0:level+1};}
                else if(Is(n,"pStyle")){WAttributes(n,"val");}
                else if(Is(n,"numPr"))p=ReadNumPr(n,p);
                else if(semantic is not null&&Is(n,"bidi"))WAttributes(n,"val");
                else if(nativeComposition&&Is(n,"textAlignment")&&A(n,"val")=="baseline"&&!Elements(n).Any())
                    WAttributes(n,"val");
                else if(nativeComposition&&Is(n,"overflowPunct")&&
                    TextStyleResolver.OnOff(n.Attributes.GetValueOrDefault("{"+W+"}val"))==false&&!Elements(n).Any())
                    WAttributes(n,"val");
                else if(tableCascade is not null&&Is(n,"cnfStyle")){}
                else if(!Is(n,"sectPr"))Issue("LAYOUT_PARAGRAPH_PROPERTY_UNQUALIFIED",n,"Propriété de paragraphe non qualifiée : "+n.Name);
            }
            return p;
        }
        (PageGeometry Geometry,string Start) Geometry(InventoryNode? section,PageGeometry? inherited=null)
        {
            var g=inherited??new PageGeometry();string start="nextPage";
            if(section is null){Issue("LAYOUT_DEFAULT_PAGE",null,"Format absent : profil de lecture A4, marges de 54 points.",false);return(g,start);}
            if(inherited is null&&(Child(section,"pgSz") is null||Child(section,"pgMar") is null))Issue("LAYOUT_DEFAULT_PAGE",section,"Dimensions ou marges absentes : valeurs du profil de lecture pour les propriétés manquantes.",false);
            WAttributes(section,"rsidR","rsidRPr","rsidSect","rsidDel");
            foreach(var n in children[section.Id])
            {
                if(n.Name.StartsWith('#'))continue;
                if(Is(n,"pgSz")){WAttributes(n,"w","h","orient","code");g=g with{Width=Number(n,"w",g.Width,144,1440,20),Height=Number(n,"h",g.Height,144,1440,20)};if(A(n,"orient") is not ("" or "portrait" or "landscape"))Issue("LAYOUT_ORIENTATION_UNQUALIFIED",n,"Orientation hors profil.");}
                else if(Is(n,"pgMar")){WAttributes(n,"left","top","right","bottom","header","footer","gutter");g=g with{Left=Number(n,"left",g.Left,0,720,20),Top=Number(n,"top",g.Top,0,720,20),Right=Number(n,"right",g.Right,0,720,20),Bottom=Number(n,"bottom",g.Bottom,0,720,20)};if(!nativeComposition&&A(n,"gutter") is not ("" or "0"))Issue("LAYOUT_GUTTER_UNQUALIFIED",n,"Gouttière hors profil.");}
                else if(Is(n,"type"))
                {
                    WAttributes(n,"val");start=A(n,"val");
                    if(start is not ("nextPage" or "continuous")&&!(nativeComposition&&start is "oddPage" or "evenPage" or "nextColumn"))
                        Issue("LAYOUT_SECTION_START_UNQUALIFIED",n,"Type de section non qualifié : "+start);
                }
                else if(Is(n,"cols"))
                {
                    if(nativeComposition)
                    {
                        WAttributes(n,"num","space","equalWidth","sep");
                        foreach(var c in Elements(n))
                            if(Is(c,"col"))WAttributes(c,"w","space");
                            else Issue("LAYOUT_COLUMNS_UNQUALIFIED",c,"Déclaration de colonne inconnue.");
                    }
                    else
                    {
                        WAttributes(n,"num","space","equalWidth");
                        if(A(n,"num") is not ("" or "1")||Elements(n).Any())
                            Issue("LAYOUT_COLUMNS_UNQUALIFIED",n,"Plusieurs colonnes hors profil.");
                    }
                }
                else if(Is(n,"docGrid")){WAttributes(n,"type","linePitch","charSpace");if(A(n,"type") is not ("" or "default"))Issue("LAYOUT_DOCUMENT_GRID_UNQUALIFIED",n,"Grille de composition active hors profil.");else Issue("LAYOUT_INACTIVE_DOCUMENT_GRID",n,"Grille inactive ; valeurs conservées sans effet sur la composition.",false);}
                else if(semantic is not null&&(Is(n,"headerReference")||Is(n,"footerReference")))
                    Attributes(n,"{"+R+"}id","{"+W+"}type");
                else if(semantic is not null&&Is(n,"titlePg"))WAttributes(n,"val");
                else if(semantic is not null&&Is(n,"pgNumType"))WAttributes(n,"start","fmt");
                else if(nativeComposition&&Is(n,"rtlGutter"))WAttributes(n,"val");
                else if(nativeComposition&&endnotesPlanned&&Is(n,"endnotePr")&&ReadSectionEndnoteProperties(n)){}
                else if(nativeComposition&&(Is(n,"footnotePr")||Is(n,"endnotePr"))&&ReadEquivalentSectionNoteProperties(n,Is(n,"endnotePr"))){}
                else if(nativeComposition&&Is(n,"noEndnote")&&
                    !semantic!.References.Any(reference=>reference.Kind=="endnote")&&
                    !Elements(n).Any()&&TextStyleResolver.OnOff(n.Attributes.GetValueOrDefault("{"+W+"}val")) is not null)
                {
                    WAttributes(n,"val");
                    Issue("PAGE_ENDNOTE_SUPPRESSION_INACTIVE",n,"Option conservée. Aucun appel de note de fin actif.",false);
                }
                else Issue("LAYOUT_SECTION_PROPERTY_UNQUALIFIED",n,"Propriété de section non qualifiée : "+n.Name);
            }
            if(g.Width-g.Left-g.Right<36||g.BodyHeight<36)Issue("LAYOUT_PAGE_BODY_INVALID",section,"Zone de texte trop petite pour composer.");
            return(g,start);
        }
        public PaginatedDocument Read()
        {
            token.ThrowIfCancellationRequested();var m=input.Manifest;
            if(!m.InspectionComplete)return new([],[new("LAYOUT_INSPECTION_REFUSED",null,"Inspection refusée : aucune pagination.",true)]);
            if(m.Nodes.Count>50000||m.Nodes.Sum(n=>(long)(n.Text?.Length??0))>500000)return new([],[new("LAYOUT_RESOURCE_LIMIT",null,"Limite du profil paginé ; lecture simplifiée conservée.",true)]);
            string main=m.Relationships.Single(r=>r.SourcePart=="/"&&r.Type==R+"/officeDocument").ResolvedPart!;
            var body=m.Nodes.FirstOrDefault(n=>n.Source.PartUri==main&&Is(n,"body"));if(body is null)return new([],[new("LAYOUT_BODY_MISSING",null,"Corps documentaire absent.",true)]);
            foreach(var relation in m.Relationships.Where(r=>r.SourcePart==main))
            {
                if(nativeComposition&&relation.Type==OleRelation)continue;
                if(placedFootnoteCalls is not null&&relation.Type==R+"/footnotes"&&
                    semantic!.References.Any(r=>r.Kind=="footnote"&&placedFootnoteCalls.Contains(r.SourceId)))continue;
                if(nativeComposition&&endnotesPlanned&&relation.Type==R+"/endnotes")continue;
                if(semantic is not null&&(relation.Type==StylesEffectsRelation||
                    relation.Type is R+"/header" or R+"/footer" or SemanticReader.CustomXmlRelation))continue;
                if(relation.Type is not (R+"/styles" or R+"/settings" or R+"/theme" or R+"/fontTable" or R+"/numbering" or R+"/webSettings" or R+"/image" or R+"/hyperlink")&&!EmptyNoteDependency(relation))Issue("LAYOUT_DEPENDENCY_UNQUALIFIED",body,"Dépendance non qualifiée en pagination : "+relation.Type);
            }
            if(semantic is not null)
            {
                foreach(var dependency in semantic.CustomXml)
                {
                    token.ThrowIfCancellationRequested();
                    var (code,blocks)=dependency.Impact switch
                    {
                        DependencyImpact.NoLayoutEffect=>("PAGE_CUSTOM_XML_METADATA_ONLY",false),
                        DependencyImpact.AffectsLayout=>("PAGE_CUSTOM_XML_CONSUMED",true),
                        _=>("PAGE_CUSTOM_XML_UNDETERMINED",true)
                    };
                    var relation=dependency.Relation;
                    Issue(code,dependency.RootId is {} root&&semantic.Element(root).StoryId is null?nodes[root]:body,
                        relation.SourcePart+" "+relation.Id+" → "+relation.Target+". "+dependency.Reason,blocks);
                }
                InspectStyles(body);
            }
            var dd=semantic is null?m.Nodes.FirstOrDefault(n=>Is(n,"docDefaults")):
                semantic.EffectiveStyles.DefaultsId is {} defaultsId?nodes[defaultsId]:null;
            if(dd is not null){var rd=Child(dd,"rPrDefault");defaults=Character(rd is null?null:Child(rd,"rPr"),defaults);var pd=Child(dd,"pPrDefault");var ppd=pd is null?null:Child(pd,"pPr");paragraphDefaults=Paragraph(ppd,paragraphDefaults);defaultParagraphMark=ppd is null?null:Child(ppd,"rPr");}
            var sections=new List<(List<InventoryNode> Paragraphs,InventoryNode? End)>();var pending=new List<InventoryNode>();
            foreach(var n in children[body.Id])
            {
                token.ThrowIfCancellationRequested();
                if(Is(n,"tbl")){pending.Add(n);}
                else if(Is(n,"p")){pending.Add(n);if(Child(n,"pPr") is {} pp&&Child(pp,"sectPr") is {} end){sections.Add((pending,end));pending=[];}}
                else if(Is(n,"sectPr")){sections.Add((pending,n));pending=[];}
                else if(nativeComposition&&(SemanticIndex.IsAlternative(n)||semantic!.ProcessesContent(n.Id)))
                {
                    // Body structure is read from the active semantic projection below.
                }
                else if(nativeComposition&&semantic!.IsStandaloneBookmark(n.Id))
                    Issue("LAYOUT_INFORMATIONAL_MARKER",n,"Marque de signet conservée en dehors du flux de blocs.",false);
                else if(n.Kind=="textValue"&&string.IsNullOrWhiteSpace(n.Text)){}
                else Issue("LAYOUT_BLOCK_UNQUALIFIED",n,"Structure hors profil paginé : "+n.Name);
            }
            if(pending.Count>0||sections.Count==0)sections.Add((pending,null));
            if(semantic is not null)
                sections=semantic.Sections.Select(s=>(semantic.BodyBlocks[s.StartBlock..s.EndBlockExclusive].Select(id=>nodes[id]).ToList(),
                    s.PropertiesId is {} id?nodes[id]:null)).ToList();
            var formats=new (PageGeometry Geometry,string Start)[sections.Count];
            for(int i=sections.Count-1;semantic is not null&&i>=0;i--)
            {
                token.ThrowIfCancellationRequested();
                var end=sections[i].End;
                bool share=semantic is not null&&i+1<formats.Length&&end is not null&&Child(end,"type") is {} type&&A(type,"val")=="continuous";
                formats[i]=Geometry(end,share?formats[i+1].Geometry:null);
            }
            ReadSettings();var result=new List<LayoutParagraph>();PageGeometry? previous=null;int sectionNumber=0;
            foreach(var section in sections)
            {
                var (geometry,start)=semantic is null?Geometry(section.End):formats[sectionNumber];
                sectionNumber++;
                if(start=="continuous"&&previous is not null&&geometry!=previous)Issue("LAYOUT_CONTINUOUS_GEOMETRY_UNQUALIFIED",section.End,"Changement de format dans une section continue hors profil.");previous=geometry;
                foreach(var n in section.Paragraphs)
                {
                    if(Is(n,"tbl"))ReadTable(n,sectionNumber,geometry,start,result);
                    else if(Is(n,"p"))
                    {
                        var paragraph=ReadParagraph(n,sectionNumber,geometry,start);
                        if(generatedTocs?.TryGetValue(n.Id,out var rows)==true)
                            foreach(var row in rows)
                            {
                                var style=paragraphDefaults;
                                if(row.StyleId is {} id)
                                    foreach(string layer in semantic!.StyleUse(id)!.BaseToDerivedIds)
                                        style=Paragraph(Child(nodes[layer],"pPr"),style);
                                result.Add(paragraph with {Id=row.Id,Properties=style,Runs=[],List=null,StyleId=row.StyleId??""});
                            }
                        else if(tocContinuations?.ContainsKey(n.Id)!=true)result.Add(paragraph);
                    }
                    else Issue("LAYOUT_BLOCK_UNQUALIFIED",n,"Structure hors profil paginé : "+n.Name);
                }
            }
            if(nativeComposition)
            {
                var unrendered=m.Relationships.Where(r=>r.SourcePart==main&&r.Type==OleRelation&&
                    !renderedOlePreviews.Contains((r.SourcePart,r.Id))).Select(r=>r.Id).ToArray();
                if(unrendered.Length>0)
                    Issue("PAGE_OLE_PREVIEW_UNRESOLVED",body,"Aperçus passifs non qualifiés pour "+unrendered.Length+
                        " relation(s) OLE. Identifiants inventoriés dans "+main+".");
            }
            if(result.Count>10000)Issue("LAYOUT_PARAGRAPH_LIMIT",body,"Trop de paragraphes pour le profil paginé.");
            var document=new PaginatedDocument(result,issues.ToArray()){Fonts=DocumentFonts.Read(input),Tables=tables.ToArray(),
                SmartArt=smartArt.ToArray()};
            return semantic is null?FontResolver.Resolve(document,token):
                document with{SectionFormats=formats.Select(f=>f.Geometry).ToArray()};
        }
        LayoutParagraph ReadParagraph(InventoryNode n,int sectionNumber,PageGeometry geometry,string start,ParagraphLayout? tableParagraph=null,LayoutStyle? tableCharacter=null,bool inCell=false)
        {
                    WAttributes(n,"rsidR","rsidRDefault","rsidP","rsidRPr");var pp=Child(n,"pPr");var p=tableParagraph??paragraphDefaults;var character=tableCharacter??defaults;
                    LayoutStyle mark=Character(defaultParagraphMark,character);bool hasMark=defaultParagraphMark is not null;SourceLocation markSource=defaultParagraphMark?.Source??n.Source;
                    var selection=pp is null?null:Child(pp,"pStyle");
                    string styleId=selection is null?defaultParagraphStyle:A(selection,"val");
                    var paragraphStyles=Chain(styleId,selection);
                    foreach(var style in paragraphStyles)
                    {
                        var sp=Child(style,"pPr");p=Paragraph(sp,p);character=Character(Child(style,"rPr"),character,true);mark=Character(Child(style,"rPr"),mark,true);
                        if(sp is not null&&Child(sp,"rPr") is {} sr){mark=Character(sr,mark,true);hasMark=true;markSource=sr.Source;}
                    }
                    p=Paragraph(pp,p);var listLabel=PrepareList(n,pp,ref p,character,paragraphStyles,tableParagraph);var runs=new List<LayoutRun>();
                    if(p.BeforeAutoSpacing is not null||p.AfterAutoSpacing is not null)
                    {
                        if(fixedAutoSpacing is null)
                        {
                            string? settings=semantic?.RelatedRoot("settings","settings");
                            var compatibility=settings is null?null:Child(nodes[settings],"compat");
                            fixedAutoSpacing=compatibility is not null&&
                                Child(compatibility,"doNotUseHTMLParagraphAutoSpacing") is {} setting&&Toggle(setting);
                        }
                        // ISO/IEC 29500-1 §17.15.3.24 supplies the fixed pair.
                        if(fixedAutoSpacing.Value)
                            p=p with{Before=p.BeforeAutoSpacing is null?p.Before:5,After=p.AfterAutoSpacing is null?p.After:10};
                        else
                            Issue("LAYOUT_AUTOMATIC_SPACING_UNQUALIFIED",n,"Espacement automatique HTML effectif hors profil.");
                    }
                    if(p.HasBorders&&(inCell||readingStory&&!runningStory&&!textBoxStory))
                        Issue("LAYOUT_PARAGRAPH_BORDER_REGION_UNQUALIFIED",n,"Bordure de paragraphe en cellule ou histoire auxiliaire hors profil.");
                    if(p.LeftBorder is not null||p.RightBorder is not null)
                    {
                        var settings=semantic?.RelatedRoot("settings","settings");
                        var compatibility=settings is null?null:Child(nodes[settings],"compat");
                        if(compatibility is not null&&Child(compatibility,"swapBordersFacingPages") is {} swap&&Toggle(swap))
                            Issue("LAYOUT_PARAGRAPH_BORDER_REGION_UNQUALIFIED",swap,"Inversion des bordures latérales hors profil.");
                    }
                    if(p.LineRule=="exact"&&suppressBottomSpacing)
                        Issue("PAGE_EXACT_BOTTOM_SPACING_UNQUALIFIED",n,"La règle de compatibilité modifie la dernière ligne exacte de la page.");
                    foreach(var run in InlineRuns(n))
                    {
                        if(run.Kind=="textValue"&&string.IsNullOrWhiteSpace(run.Text))continue;
                        if(!Is(run,"r")){Issue("LAYOUT_INLINE_UNQUALIFIED",run,"Élément non qualifié en pagination : "+run.Name);continue;}
                        WAttributes(run,"rsidR","rsidRPr");var rp=Child(run,"rPr");var effective=character;
                        if(rp is not null&&Child(rp,"rStyle") is {} rs)foreach(var style in Chain(A(rs,"val"),rs))effective=Character(Child(style,"rPr"),effective,true);
                        effective=Character(rp,effective);
                        foreach(var leaf in RunContents(run,(!readingStory||runningStory)&&!inCell))
                        {
                            if(Is(leaf,"t")){Attributes(leaf,"{http://www.w3.org/XML/1998/namespace}space");if(children[leaf.Id].Any(c=>c.Kind!="textValue"))Issue("LAYOUT_TEXT_CONTENT_UNQUALIFIED",leaf,"Contenu imbriqué dans un texte hors profil.");runs.Add(new(leaf.Id,leaf.Source,string.Concat(children[leaf.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)),effective));}
                            else if(Is(leaf,"tab")){WAttributes(leaf);runs.Add(new(leaf.Id,leaf.Source,"\t",effective,"tab"));}
                            else if(Is(leaf,"drawing"))
                            {
                                if(nativeComposition&&ReadSmartArt(leaf))continue;
                                if(nativeComposition&&!readingStory&&!inCell&&
                                    Elements(leaf).Take(2).ToArray() is [var anchor]&&anchor.Name=="{"+WP+"}anchor")
                                {
                                    var picture=ReadFloatingPicture(leaf,anchor);
                                    if(picture is not null)runs.Add(new(leaf.Id,leaf.Source,"",effective,"floatingPicture"){FloatingPicture=picture});
                                }
                                else if(ReadInlineImage(leaf) is {} image)
                                    runs.Add(new(leaf.Id,leaf.Source,"",effective,"image"){Image=image});
                            }
                            else if(nativeComposition&&(!readingStory||runningStory)&&!inCell&&IsFloatingTextAlternative(leaf))
                            {
                                if(ReadFloatingTextFrame(leaf) is {} frame)
                                    runs.Add(new(frame.Id,leaf.Source,"",effective,"floatingTextFrame"){FloatingTextFrame=frame});
                                else Issue("LAYOUT_FLOATING_TEXTBOX_UNQUALIFIED",leaf,
                                    "Branche de dessin ou zone de texte hors profil natif.");
                            }
                            else if(nativeComposition&&runningStory&&!inCell&&Is(leaf,"pict")&&HasVmlTextFrame(leaf))
                            {
                                if(ReadVmlTextFrame(leaf) is {} frame)
                                    runs.Add(new(frame.Id,leaf.Source,"",effective,"floatingTextFrame"){FloatingTextFrame=frame});
                                else Issue("LAYOUT_VML_TEXT_FRAME_UNQUALIFIED",leaf,"Zone de texte VML ou ancrage dans l’histoire hors profil.");
                            }
                            else if(nativeComposition&&(Is(leaf,"object")||Is(leaf,"pict")))
                            {
                                if(ReadLegacyPicture(leaf) is {} preview)
                                    runs.Add(new(leaf.Id,leaf.Source,"",effective,"image"){Image=preview});
                            }
                            else if(Is(leaf,"br")||Is(leaf,"cr"))
                            {
                                WAttributes(leaf,"type","clear");string type=A(leaf,"type");
                                if(A(leaf,"clear").Length>0||type is not ("" or "textWrapping" or "page")&&
                                    !(nativeComposition&&!readingStory&&!inCell&&type=="column"))
                                    Issue("LAYOUT_BREAK_UNQUALIFIED",leaf,"Saut de colonne ou habillage hors profil.");
                                string kind=type switch{"page"=>"pageBreak","column"=>"columnBreak",_=>"lineBreak"};
                                runs.Add(new(leaf.Id,leaf.Source,kind=="lineBreak"?"\n":"",effective,kind));
                            }
                            else if(Is(leaf,"lastRenderedPageBreak"))Issue("LAYOUT_CACHED_BREAK_IGNORED",leaf,"Saut de pagination mis en cache par Word conservé dans l’inventaire, non utilisé pour composer.",false);
                            else if(semantic is not null&&(Is(leaf,"softHyphen")||Is(leaf,"noBreakHyphen")))WAttributes(leaf);
                            else if((Is(leaf,"footnoteReference")||Is(leaf,"endnoteReference"))&&placedFootnoteCalls?.Contains(leaf.Id)==true)
                                WAttributes(leaf,"id","customMarkFollows");
                            else if(nativeComposition&&Is(leaf,"endnoteReference"))
                            {
                                WAttributes(leaf,"id","customMarkFollows");
                                Issue("PAGE_ENDNOTE_PLACEMENT_UNQUALIFIED",leaf,"Appel conservé. Placement de la note de fin hors profil.");
                            }
                            else if((Is(leaf,"footnoteRef")||Is(leaf,"endnoteRef")||Is(leaf,"separator")||Is(leaf,"continuationSeparator"))&&
                                readingStory&&placedFootnoteCalls?.Contains(leaf.Id)==true)
                                WAttributes(leaf);
                            else if(semantic is not null&&Is(leaf,"sym"))
                            {
                                WAttributes(leaf,"font","char");
                                if(Elements(leaf).Any())Issue("LAYOUT_SYMBOL_CONTENT_UNQUALIFIED",leaf,"Contenu imbriqué dans un symbole hors profil.");
                            }
                            else if(Is(leaf,"fldChar")&&(readingStory||IsNativeFieldToken(leaf)))
                            {
                                WAttributes(leaf,"fldCharType","fldLock","dirty");
                                if(Elements(leaf).Any())Issue("PAGE_STORY_FIELD_CONTENT_UNQUALIFIED",leaf,"Contenu auxiliaire du champ hors profil.");
                            }
                            else if(Is(leaf,"instrText")&&(readingStory||IsNativeFieldToken(leaf)))Attributes(leaf,"{http://www.w3.org/XML/1998/namespace}space");
                            else if(leaf.Kind!="textValue"||!string.IsNullOrWhiteSpace(leaf.Text))Issue("LAYOUT_RUN_CONTENT_UNQUALIFIED",leaf,"Contenu de caractère hors profil : "+leaf.Name);
                        }
                    }
                    if(listLabel is not null&&semantic is null)runs.Insert(0,new(n.Id+":marker",listLabel.Source,listLabel.Text,listLabel.Style,"listMarker"));
                    if(pp is not null&&Child(pp,"rPr") is {} mr){mark=Character(mr,mark);hasMark=true;markSource=mr.Source;}
                    if(hasMark)runs.Add(new(n.Id+":mark",markSource,"",mark,"paragraphMark"));
                    if(runs.Count==0)runs.Add(new(n.Id,n.Source,"",character));
                    return new(n.Id,n.Source,runs,p,sectionNumber,geometry,start){List=listLabel,StyleId=styleId,
                        SuppressBeforeAfterStandaloneBreak=suppressBeforeAfterStandaloneBreak};
        }

    }
}
