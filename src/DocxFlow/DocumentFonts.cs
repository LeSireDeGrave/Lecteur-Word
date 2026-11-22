using System.Text.Json.Serialization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public sealed record FontRequest(string Value,bool Theme,SourceLocation Source,string Attribute);
public sealed record WordFontSlots(FontRequest? Ascii=null,FontRequest? HAnsi=null,FontRequest? EastAsia=null,FontRequest? Cs=null,string Hint="default")
{
    public static WordFontSlots Apply(WordFontSlots inherited,InventoryNode node)
        =>Apply(inherited,node.Source,key=>node.Attributes.GetValueOrDefault(SemanticIndex.QName(SemanticIndex.W,key)));
    internal static WordFontSlots Apply(WordFontSlots inherited,SourceLocation source,Func<string,string?> attribute)
    {
        FontRequest? Read(string key,string theme,FontRequest? previous)
        {
            string? themed=attribute(theme);
            if(key=="cs")themed??=attribute("csTheme");
            if(themed is not null)return new(themed,true,source,theme);
            return attribute(key) is {} value?new(value,false,source,key):previous;
        }
        return new(Read("ascii","asciiTheme",inherited.Ascii),Read("hAnsi","hAnsiTheme",inherited.HAnsi),Read("eastAsia","eastAsiaTheme",inherited.EastAsia),Read("cs","cstheme",inherited.Cs),attribute("hint")??inherited.Hint);
    }
}
public sealed record ThemeFont(string Key,string Family,string Script,SourceLocation Source);
public sealed record FontDeclaration(string Family,string Alternative,string Classification,string Charset,string Pitch,SourceLocation Source,bool Installed);
public sealed record EmbeddedFont(string Family,string Face,string? Part,SourceLocation Source,string Status,string Rights,int? FsType,string? Sha256,string Detail)
{
    public string Copyright {get;init;}="";
    public string LicenseNotice {get;init;}="";
    public string LicenseUrl {get;init;}="";
    [JsonIgnore] internal FontFaceData? Data {get;init;}
}
public sealed record FontResolution(string RequestedFamily,string UsedFamily,string Status,bool Installed,int Weight,string Slant,string Sha256,int FaceIndex,string Coverage,string Reason)
{
    [JsonIgnore] internal FontFaceData? Data {get;init;}
}
public sealed record FontUse(string RunId,SourceLocation Source,string Slot,FontRequest? Request,FontResolution? Resolution,string Status);
internal sealed record FontFaceData(byte[] Bytes,int Index,string Hash);
public sealed record DocumentFonts
{
    public bool ResolutionAttempted {get;init;}
    public IReadOnlyList<FontDeclaration> Declarations {get;init;}=[];
    public IReadOnlyList<ThemeFont> Theme {get;init;}=[];
    public IReadOnlyList<FontRequest> Requests {get;init;}=[];
    public IReadOnlyList<EmbeddedFont> Embedded {get;init;}=[];
    public IReadOnlyList<FontUse> Uses {get;init;}=[];
    public string Impact=>"Les métriques des faces chargées déterminent les lignes et pages. Une substitution peut modifier la pagination ; aucune équivalence Word n’est garantie.";
    public static DocumentFonts Read(InspectionResult input) => Read(input, collectRequests: true);

    internal static DocumentFonts Read(InspectionResult input, bool collectRequests)
    {
        const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main",A="http://schemas.openxmlformats.org/drawingml/2006/main",R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var m=input.Manifest;if(!m.InspectionComplete)return new();
        string? main=m.Relationships.FirstOrDefault(r=>r.SourcePart=="/"&&r.Type==R+"/officeDocument")?.ResolvedPart;
        var tableParts=m.Relationships.Where(r=>r.SourcePart==main&&r.Type==R+"/fontTable"&&r.Mode=="Internal").Select(r=>r.ResolvedPart).ToHashSet();
        var themeParts=m.Relationships.Where(r=>r.SourcePart==main&&r.Type==R+"/theme"&&r.Mode=="Internal").Select(r=>r.ResolvedPart).ToHashSet();
        var children=m.Nodes.Where(n=>n.ParentId is not null &&
            (tableParts.Contains(n.Source.PartUri)||themeParts.Contains(n.Source.PartUri))).ToLookup(n=>n.ParentId!);
        string Att(InventoryNode n,string a)=>n.Attributes.GetValueOrDefault("{"+W+"}"+a)??"";
        InventoryNode? Child(InventoryNode n,string local)=>children[n.Id].FirstOrDefault(x=>x.Name=="{"+W+"}"+local);
        string Val(InventoryNode n,string local)=>Child(n,local) is {} c?Att(c,"val"):"";
        var declarations=new List<FontDeclaration>();var embedded=new List<EmbeddedFont>();var themes=new List<ThemeFont>();
        foreach(var n in m.Nodes.Where(n=>tableParts.Contains(n.Source.PartUri)&&n.Name=="{"+W+"}font"))
        {
            string family=Att(n,"name");declarations.Add(new(family,Val(n,"altName"),Val(n,"family"),Val(n,"charset"),Val(n,"pitch"),n.Source,FontResolver.IsInstalled(family)));
            foreach(var e in children[n.Id].Where(c=>c.Name.StartsWith("{"+W+"}embed",StringComparison.Ordinal)))
                embedded.Add(embedded.Count<16?EmbeddedFonts.Read(input,family,e):new EmbeddedFont(family,e.Name[(e.Name.IndexOf('}')+1)..],null,e.Source,"refused","indisponibles",null,null,"Budget d’analyse de 16 faces incorporées dépassé ; déclaration conservée."));
        }
        foreach(var set in m.Nodes.Where(n=>themeParts.Contains(n.Source.PartUri)&&n.Name is "{"+A+"}majorFont" or "{"+A+"}minorFont"))
        {
            string prefix=set.Name.EndsWith("majorFont",StringComparison.Ordinal)?"major":"minor";
            foreach(var n in children[set.Id])
            {
                string family=n.Attributes.GetValueOrDefault("{}typeface")??"",script=n.Attributes.GetValueOrDefault("{}script")??"";
                string key=n.Name=="{"+A+"}latin"?prefix+"HAnsi":n.Name=="{"+A+"}ea"?prefix+"EastAsia":n.Name=="{"+A+"}cs"?prefix+"Bidi":prefix+":"+script;
                if(n.Name.StartsWith("{"+A+"}",StringComparison.Ordinal))themes.Add(new(key,family,script,n.Source));
            }
        }
        var requests=collectRequests
            ? m.Nodes.Where(n=>n.Name=="{"+W+"}rFonts").SelectMany(n=>n.Attributes.Where(a=>a.Key.StartsWith("{"+W+"}",StringComparison.Ordinal)&&a.Key!="{"+W+"}hint").Select(a=>new FontRequest(a.Value,a.Key.EndsWith("Theme",StringComparison.Ordinal)||a.Key.EndsWith("theme",StringComparison.Ordinal),n.Source,a.Key[(W.Length+2)..]))).ToArray()
            : [];
        return new(){Declarations=declarations,Theme=themes,Requests=requests,Embedded=embedded};
    }
    public string? ThemeFamily(string key)
    {
        key=key.Replace("Ascii","HAnsi",StringComparison.Ordinal);
        var matches=Theme.Where(t=>t.Key==key).ToArray();return matches.Length==1&&!string.IsNullOrWhiteSpace(matches[0].Family)?matches[0].Family:null;
    }
}
