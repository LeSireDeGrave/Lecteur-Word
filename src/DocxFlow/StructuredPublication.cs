using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed class StructuredPublication(PublicationSource source,ExportFormat format,CancellationToken token,bool useCover=false)
{
    internal const string HtmlNamespace="http://www.w3.org/1999/xhtml";
    internal static readonly XNamespace Html=HtmlNamespace, Epub="http://www.idpf.org/2007/ops",
        Office="urn:oasis:names:tc:opendocument:xmlns:office:1.0",
        Text="urn:oasis:names:tc:opendocument:xmlns:text:1.0",
        Style="urn:oasis:names:tc:opendocument:xmlns:style:1.0",
        Table="urn:oasis:names:tc:opendocument:xmlns:table:1.0",
        Draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0",
        SvgOdt="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0",
        Fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0",
        Xlink="http://www.w3.org/1999/xlink", Svg="http://www.w3.org/2000/svg";
    internal sealed record Asset(string Path,string Mime,byte[] Data);
    internal sealed record Inline(SemanticSource Source,string? Value,TextStyle? Style,FontResolution? Font,
        string? Anchor=null,SemanticReference? Link=null,SemanticReference? Note=null,InlineImage? Image=null,string? Field=null);

    internal readonly List<FlowIssue> Issues=[];
    internal readonly List<PublicationFeature> Features=[];
    readonly HashSet<(string,string?)> seen=[];
    internal readonly List<Asset> Assets=[];
    internal Asset? Cover=>useCover?Assets.FirstOrDefault(a=>a.Mime is "image/png" or "image/jpeg"):null;
    readonly Dictionary<string,Asset> assetsById=new(StringComparer.Ordinal);
    internal readonly XElement Automatic=new(Office+"automatic-styles");
    readonly Dictionary<(string,XElement),string> styles=new(new StyleEquality());
    readonly SemanticDocument model=source.Semantic!;
    readonly PageCompositionSnapshot snapshot=source.Pagination!;
    readonly Dictionary<string,LayoutParagraph> paragraphs=(source.Pagination?.RulerModel?.Paragraphs??[])
        .ToDictionary(p=>p.Id,StringComparer.Ordinal);
    readonly Dictionary<string,LayoutTable> tables=(source.Pagination?.RulerModel?.Tables??[])
        .ToDictionary(t=>t.Id,StringComparer.Ordinal);
    readonly ILookup<string,TextToken> tokens=(source.Pagination?.Typography?.Tokens??[])
        .Where(t=>t.ParagraphId is not null).ToLookup(t=>t.ParagraphId!,StringComparer.Ordinal);
    readonly ILookup<string,SemanticField> fieldsByParagraph=source.Semantic!.Fields
        .Select(f=>(Field:f,Paragraph:ParagraphOf(source.Semantic!,f.Id))).Where(p=>p.Paragraph is not null)
        .ToLookup(p=>p.Paragraph!,p=>p.Field,StringComparer.Ordinal);
    readonly Dictionary<string,TextParagraph> shaped=(source.Pagination?.Typography?.Paragraphs??[])
        .ToDictionary(p=>p.Id,StringComparer.Ordinal);
    readonly ILookup<string,SemanticReference> references=source.Semantic!.References.ToLookup(r=>r.SourceId,StringComparer.Ordinal);
    readonly ILookup<string,SemanticElement> marks=source.Semantic!.Elements.Where(e=>e.Kind==SemanticKind.BookmarkStart&&
        e.Coverage==SemanticCoverage.Represented).Select(e=>(element:e,paragraph:ParagraphOf(source.Semantic!,e.Id)))
        .Where(x=>x.paragraph is not null).ToLookup(x=>x.paragraph!,x=>x.element,StringComparer.Ordinal);
    readonly HashSet<string> writtenNotes=new(StringComparer.Ordinal);
    readonly List<(string Id,bool End,string Label,XElement[] Blocks)> notes=[];
    internal IReadOnlyList<(string Id,bool End,string Label,XElement[] Blocks)> Notes=>notes;
    internal readonly List<(string Id,int Level,string Title)> Headings=[];
    readonly Dictionary<string,InlineImage> placedImages=(source.Pagination?.Composition.Pages??[])
        .SelectMany(p=>p.DisplayLines.SelectMany(l=>l.Images)).GroupBy(i=>i.RunId,StringComparer.Ordinal)
        .ToDictionary(g=>g.Key,g=>g.First().Image,StringComparer.Ordinal);
    bool Odt=>format==ExportFormat.Odt;
    internal PublicationMetadata Metadata=>source.Metadata;
    internal SemanticDocument Model=>model;

    internal static PublicationPlan Prepare(PublicationSource source,ExportFormat format,CancellationToken token,bool useCover=false)
    {
        if(source.Pagination is not {Typography:{},RulerModel:{}}||source.Semantic is null)
            return Publications.Blocked("EXPORT_SEMANTIC_UNAVAILABLE","Les styles et la structure vérifiés du document sont indisponibles.");
        var book=new StructuredPublication(source,format,token,useCover);
        _=book.BuildBody();
        return new(format switch{ExportFormat.Odt=>"odt-semantic-v1",ExportFormat.Epub3=>"epub-semantic-v1",_=>"opusnova-v27"},
            [..book.Features],[..book.Issues]);
    }

    internal static void Write(string path,PublicationSource source,ExportFormat format,ExportBudget budget,
        IProgress<PublicationProgress>? progress,CancellationToken token,bool useCover=false)
    {
        var book=new StructuredPublication(source,format,token,useCover);
        var body=book.BuildBody();
        if(book.Issues.Any(i=>i.BlocksExport))throw new PublicationRefusal(book.Issues);
        if(format==ExportFormat.Nova)
        {
            NovaPublication.Write(path,book,budget,token);
            progress?.Report(new("write",1,1));
            return;
        }
        PublicationPackages.Write(path,source,book,body,format,budget,progress,token);
    }

    internal static void Verify(string path,PublicationSource source,ExportFormat format,CancellationToken token,bool useCover=false)
    {
        var book=new StructuredPublication(source,format,token,useCover);
        var body=book.BuildBody();
        if(format==ExportFormat.Nova)NovaPublication.Verify(path,book,token);
        else PublicationPackages.Verify(path,source,book,body,format,token);
    }

    XElement[] BuildBody()
    {
        token.ThrowIfCancellationRequested();
        if(model.Stories.SingleOrDefault(s=>s.Kind=="body") is not {} body)return [];
        Feature("metadata",PublicationRetention.Native,null,"Propriétés du document et langue.");
        Feature("document",PublicationRetention.Structured,model.Source(body.RootId).Location,"Ordre de lecture et paragraphes.");
        if(Odt&&snapshot.Sections.Length!=1)
            Issue("EXPORT_ODT_SECTIONS",model.Source(body.RootId),"Le profil ODT conserve une disposition de page unique.");
        if(!Odt&&snapshot.Sections.Length>0)
            Issue("EXPORT_REFLOW_PAPER",model.Source(body.RootId),"La publication s’adapte au support de lecture. Les marges et dimensions du papier restent dans le DOCX.",false);
        foreach(var region in model.Sections.SelectMany(s=>s.RunningStories).Where(s=>s.Reference.TargetId is not null).DistinctBy(s=>s.Reference.TargetId))
        {
            string root=region.Reference.TargetId!;
            if(model.BlocksOf(root).Any(e=>e.Kind!=SemanticKind.Paragraph||tokens[e.Id].Any(t=>Visible(t)&&
                (t.Control=="Drawing"||!string.IsNullOrWhiteSpace(t.Original)))))
                Issue("EXPORT_RUNNING_STORY",model.Source(root),"Un en-tête ou pied de page contient des éléments hors du profil de publication.");
        }
        foreach(var issue in snapshot.Typography!.Diagnostics.Where(i=>i.Code is "TEXT_PROPERTY_PARTIAL" or "TEXT_VISIBILITY_UNRESOLVED" or
            "TEXT_HIDDEN_CONTROL_UNQUALIFIED" or "TEXT_STORY_UNQUALIFIED"))
            AddIssue(issue.Code,issue.Source,issue.Message,true);
        foreach(var element in model.Elements.Where(e=>e.Coverage!=SemanticCoverage.InactiveAlternative))
        {
            token.ThrowIfCancellationRequested();
            if(element.Kind is SemanticKind.Revision or SemanticKind.CommentReference)
                Issue("EXPORT_EDITORIAL_HISTORY",model.Source(element.Id),"L’historique éditorial de ce document dépasse le profil du format.");
            if(element.Kind==SemanticKind.Opaque&&element.StoryId is not null&&element.Coverage==SemanticCoverage.InventoryOnly&&
                !IsCoveredDescendant(element.Id))
                Issue("EXPORT_UNKNOWN_CONTENT",model.Source(element.Id),"Contenu de l’histoire conservé dans l’inventaire. Conversion indéterminée.");
        }
        if(format==ExportFormat.Nova)
        {
            NovaPublication.Assess(this,model.BodyBlocks);
            return [];
        }
        var blocks=Blocks(model.BodyBlocks);
        if(useCover&&Cover is null)Issue("EXPORT_EPUB_COVER",model.Source(body.RootId),"Aucune illustration PNG ou JPEG ne peut servir de couverture.");
        return blocks;
    }

    bool IsCoveredDescendant(string id)
    {
        for(string? parent=model.ParentId(id);parent is not null;parent=model.ParentId(parent))
            if(model.Element(parent).Kind is SemanticKind.Property or SemanticKind.Section||
                model.Element(parent).Kind==SemanticKind.Drawing&&placedImages.ContainsKey(parent))return true;
        return false;
    }

    internal Inline[] ReadInlines(string paragraph)
    {
        token.ThrowIfCancellationRequested();
        var fontMap=new Dictionary<string,FontResolution>(StringComparer.Ordinal);
        if(shaped.TryGetValue(paragraph,out var typography))
            foreach(var fragment in typography.Fragments)
                if(fragment.Face is {} face)
                    foreach(var slice in fragment.Sources)fontMap.TryAdd(slice.Source.NodeId,face);
        var parts=new List<(int Index,Inline Item)>();
        foreach(var mark in marks[paragraph])
            if(model.Attribute(mark.Id,"name")!="_GoBack")
                parts.Add((mark.Order,new(model.Source(mark.Id),null,null,null,Anchor:Id(mark.Id))));

        var all=tokens[paragraph].ToArray();
        var fields=fieldsByParagraph[paragraph];
        var replaced=new HashSet<string>(StringComparer.Ordinal);
        foreach(var field in fields)
        {
            if(Odt&&field.StructureVerified&&!field.Locked&&field.Command is ("PAGE" or "NUMPAGES")&&
                PageFieldSyntax.Read(model,field,allowCachedPageStyle:false) is {Format:null,DecimalPlaces:0})
            {
                var node=model.Source(field.Id);
                parts.Add((model.Element(field.Id).Order,new(node,model.StoredText(field),null,null,Field:field.Command)));
                replaced.UnionWith(field.StoredResultIds);
                Feature("fields",PublicationRetention.Native,node.Location,"Champ de numéro ou total de pages ODT.");
            }
            else if(field is not {StructureVerified:true,Locked:true,StoredResultIds.IsEmpty:false})
                Issue("EXPORT_FIELD_UNSUPPORTED",model.Source(field.Id),"Champ actif hors profil de conversion.");
            else Feature("fields",PublicationRetention.Structured,model.Source(field.Id).Location,"Résultat stocké du champ verrouillé.");
        }
        foreach(var item in all)
        {
            token.ThrowIfCancellationRequested();
            if(!Visible(item)||replaced.Contains(item.Source.NodeId))continue;
            string? value=item.Original;
            var link=LinkFor(item.Source.NodeId);
            var font=fontMap.GetValueOrDefault(item.Source.NodeId);
            if(item.Role==TextRole.Control)
            {
                switch(item.Control)
                {
                    case "Tab":value="\t";break;
                    case "SoftHyphen":value="\u00ad";break;
                    case "NoBreakHyphen":value="\u2011";break;
                    case "Break":
                        if(model.Attribute(item.Source.NodeId,"type") is null or "textWrapping")value="\n";
                        else if(Odt&&model.Attribute(item.Source.NodeId,"type")=="page")
                            parts.Add((model.Element(item.Source.NodeId).Order,new(item.Source,null,null,null,Field:"BREAK")));
                        else Issue("EXPORT_BREAK_UNSUPPORTED",item.Source,"Saut de page ou de colonne hors profil.");
                        break;
                    case "Drawing":
                        if(placedImages.GetValueOrDefault(item.Source.NodeId) is {} image&&
                            format!=ExportFormat.Nova&&AssetFor(image) is not null)
                            parts.Add((model.Element(item.Source.NodeId).Order,new(item.Source,null,null,null,Link:link,Image:image)));
                        else Issue("EXPORT_DRAWING_UNSUPPORTED",item.Source,"Dessin, graphique ou équation hors du profil structuré.");
                        continue;
                    case "NoteMark":continue;
                    case "NoteCall":
                        var note=references[item.Source.NodeId].SingleOrDefault();
                        if(format!=ExportFormat.Nova&&note is {Status:ReferenceStatus.Resolved,TargetId:not null})
                            parts.Add((model.Element(item.Source.NodeId).Order,new(item.Source,null,null,null,Note:note)));
                        else Issue("EXPORT_NOTE_UNSUPPORTED",item.Source,"Note hors du profil structuré.");
                        continue;
                    default:Issue("EXPORT_INLINE_UNSUPPORTED",item.Source,"Élément en ligne hors profil.");continue;
                }
            }
            if(string.IsNullOrEmpty(value))continue;
            if(value.Contains('\r')||value.Any(c=>!XmlConvert.IsXmlChar(c)&&!char.IsSurrogate(c)))
                Issue("EXPORT_TEXT_UNSUPPORTED",item.Source,"Caractère de contrôle sans représentation XML qualifiée.");
            if(item.Style is {} style&&(style.HorizontalScale!=100||style.Shadow is not null||
                style.Underline is not ("none" or "single")||style.Caps||style.SmallCaps||style.BaselineShift!=0))
                Issue("EXPORT_STYLE_UNSUPPORTED",item.Source,"Effet typographique hors profil structuré.");
            if(font is {Status:"substituted"}||font is not null&&font.RequestedFamily!=font.UsedFamily)
                Feature("font-substitution",PublicationRetention.Structured,item.Source.Location,"Nom de la face résolue, sans copie de police dans les formats éditables.");
            parts.Add((model.Element(item.Source.NodeId).Order,new(item.Source,value,item.Style,font,Link:link)));
        }
        return parts.OrderBy(p=>p.Index).Select(p=>p.Item).ToArray();
    }

    internal LayoutParagraph? Paragraph(string id)=>paragraphs.GetValueOrDefault(id);
    internal static bool Visible(TextToken item)=>item.Role is TextRole.Source or TextRole.StoredField or TextRole.Control&&
        item.Style?.SuppressText!=true;

    static string? ParagraphOf(SemanticDocument semantic,string id)
    {
        for(string? parent=id;parent is not null;parent=semantic.ParentId(parent))
            if(semantic.Element(parent).Kind==SemanticKind.Paragraph)return parent;
        return null;
    }

    SemanticReference? LinkFor(string id)
    {
        for(string? p=id;p is not null&&model.Element(p).Kind!=SemanticKind.Paragraph;p=model.ParentId(p))
            if(references[p].FirstOrDefault(r=>r.Kind is "hyperlink" or "bookmark") is {} link)return link;
        return null;
    }

    internal XElement[] Blocks(IEnumerable<string> ids)
    {
        var result=new List<XElement>();
        XElement? list=null;
        string? listId=null;
        foreach(string id in ids)
        {
            token.ThrowIfCancellationRequested();
            var element=model.Element(id);
            if(element.Kind==SemanticKind.Table)
            {
                list=null;listId=null;
                if(tables.TryGetValue(id,out var table))result.Add(BuildTable(table));
                else Issue("EXPORT_TABLE_UNQUALIFIED",model.Source(id),"Géométrie ou fusion de tableau hors profil structuré.");
                continue;
            }
            if(element.Kind!=SemanticKind.Paragraph)
            {
                Issue("EXPORT_BLOCK_UNSUPPORTED",model.Source(id),"Bloc de l’histoire hors du profil de publication.");
                continue;
            }
            var paragraph=Paragraph(id);
            var markup=BuildParagraph(id,paragraph);
            if(paragraph?.List is not {} label)
            {
                list=null;listId=null;result.Add(markup);continue;
            }
            if(label.Level!=0||!TryListLabel(label.Text,out bool number,out int index))
            {
                Issue("EXPORT_LIST_UNSUPPORTED",model.Source(id),"Numérotation ou niveau de liste hors profil.");
                result.Add(markup);
                continue;
            }
            if(listId!=label.NumId)
            {
                listId=label.NumId;
                list=Odt?new(Text+"list"):new(number?Html+"ol":Html+"ul");
                if(Odt)list.SetAttributeValue(Text+"style-name",ListStyle(number));
                else if(number)list.SetAttributeValue("start",index);
                result.Add(list);
            }
            var entry=new XElement(Odt?Text+"list-item":Html+"li",markup);
            if(number)entry.SetAttributeValue(Odt?Text+"start-value":"value",index);
            list!.Add(entry);
            Feature("lists",PublicationRetention.Native,label.Source,"Liste ordonnée ou à puce, niveau simple.");
        }
        return result.ToArray();
    }

    static bool TryListLabel(string label,out bool numbered,out int number)
    {
        number=0;
        numbered=label.EndsWith('.')&&int.TryParse(label.AsSpan(0,label.Length-1),NumberStyles.None,
            CultureInfo.InvariantCulture,out number)&&number>=1;
        return numbered||label=="•";
    }

    string ListStyle(bool number)
    {
        string name=number?"DecimalList":"BulletList";
        if(!Automatic.Elements(Text+"list-style").Any(e=>(string?)e.Attribute(Style+"name")==name))
        {
            var level=new XElement(Text+(number?"list-level-style-number":"list-level-style-bullet"),new XAttribute(Text+"level",1));
            if(number){level.SetAttributeValue(Style+"num-format","1");level.SetAttributeValue(Style+"num-suffix",".");}
            else level.SetAttributeValue(Text+"bullet-char","•");
            Automatic.Add(new XElement(Text+"list-style",new XAttribute(Style+"name",name),level));
        }
        return name;
    }

    XElement BuildParagraph(string id,LayoutParagraph? paragraph)
    {
        int level=Math.Clamp(paragraph?.Properties.Heading??0,0,9);
        string tag=level>0?"h"+Math.Min(level,6).ToString(CultureInfo.InvariantCulture):"p";
        var block=new XElement(Odt?Text+(level>0?"h":"p"):Html+tag);
        if(Odt&&level>0)block.SetAttributeValue(Text+"outline-level",level);
        if(!Odt)
        {
            block.SetAttributeValue("id",Id(id));
            if(level>6)block.SetAttributeValue("aria-level",level);
        }
        var parts=ReadInlines(id);
        if(!Odt&&parts.Any(p=>p.Value is {} value&&(value.Contains('\t')||value.Contains('\n')||value.All(char.IsWhiteSpace))))
            block.SetAttributeValue(XNamespace.Xml+"space","preserve");
        string? plainTitle=level>0?string.Concat(parts.Select(x=>x.Value)).Trim():null;
        if(!string.IsNullOrEmpty(plainTitle))Headings.Add((Id(id),level,plainTitle));
        if(paragraph is {} layout)ApplyParagraph(block,layout);
        foreach(var item in parts)
        {
            if(item.Field=="BREAK")
            {
                // A mid-paragraph page break cannot be mapped to an ODF paragraph property.
                Issue("EXPORT_ODT_INLINE_PAGE_BREAK",item.Source,"Saut de page à l’intérieur d’un paragraphe hors profil ODT.");
                continue;
            }
            var element=WriteInline(item);
            if(element is not null)block.Add(element);
        }
        return block;
    }

    void ApplyParagraph(XElement node,LayoutParagraph paragraph)
    {
        var p=paragraph.Properties;
        if(!Odt)
        {
            string css=$"text-align:{Alignment(p.Alignment)};margin-inline-start:{Pt(p.Left)};margin-inline-end:{Pt(p.Right)};text-indent:{Pt(p.FirstLine)}";
            node.SetAttributeValue("style",css);
            if(shaped.GetValueOrDefault(paragraph.Id)?.RightToLeft==true)node.SetAttributeValue("dir","rtl");
            if(p.Tabs.Count>0)Issue("EXPORT_EPUB_TAB_STOPS",model.Source(paragraph.Id),
                "Les espaces de tabulation sont conservés. Les positions fixes sont adaptées à la liseuse.",false);
            return;
        }
        var props=new XElement(Style+"paragraph-properties",new XAttribute(Fo+"text-align",Alignment(p.Alignment)),
            new XAttribute(Fo+"margin-left",Pt(p.Left)),new XAttribute(Fo+"margin-right",Pt(p.Right)),
            new XAttribute(Fo+"text-indent",Pt(p.FirstLine)),new XAttribute(Fo+"margin-top",Pt(p.Before)),
            new XAttribute(Fo+"margin-bottom",Pt(p.After)));
        if(p.PageBreakBefore)props.SetAttributeValue(Fo+"break-before","page");
        if(p.KeepNext)props.SetAttributeValue(Fo+"keep-with-next","always");
        if(p.KeepLines)props.SetAttributeValue(Fo+"keep-together","always");
        if(p.LineRule=="auto")props.SetAttributeValue(Fo+"line-height",Num(p.Line/240*100)+"%");
        if(p.LineRule=="exact")props.SetAttributeValue(Fo+"line-height",Pt(p.Line/20));
        if(p.LineRule=="atLeast")props.SetAttributeValue(Style+"line-height-at-least",Pt(p.Line/20));
        if(p.Tabs.Count>0)props.Add(new XElement(Style+"tab-stops",p.Tabs.Select(tab=>
            new XElement(Style+"tab-stop",new XAttribute(Style+"position",Pt(tab.Position)),
                new XAttribute(Style+"type",tab.Alignment switch{"center"=>"center","right"=>"right","decimal"=>"char",_=>"left"})))));
        if(shaped.GetValueOrDefault(paragraph.Id)?.RightToLeft==true)props.SetAttributeValue(Style+"writing-mode","rl-tb");
        if(p.Shading is {} shade)props.SetAttributeValue(Fo+"background-color","#"+shade.Color);
        node.SetAttributeValue(Text+"style-name",Register("paragraph",props));
    }

    XNode? WriteInline(Inline item)
    {
        if(item.Anchor is {} anchor)return Odt?new XElement(Text+"bookmark",new XAttribute(Text+"name",anchor)):
            new XElement(Html+"span",new XAttribute("id",anchor));
        if(item.Note is {} note)return WriteNote(item,note);
        XNode content;
        if(item.Field is {} field)
            content=new XElement(Text+(field=="PAGE"?"page-number":"page-count"),item.Value??"");
        else if(item.Image is {} image&&AssetFor(image) is {} asset)
        {
            var alt=AlternativeText(item.Source.NodeId);
            if(Odt)
                content=new XElement(Draw+"frame",new XAttribute(Text+"anchor-type","as-char"),
                    new XAttribute(SvgOdt+"width",Pt(image.Width)), new XAttribute(SvgOdt+"height",Pt(image.Height)),
                    new XElement(Draw+"image",new XAttribute(Xlink+"href",asset.Path),new XAttribute(Xlink+"type","simple"),
                    new XAttribute(Xlink+"show","embed"),new XAttribute(Xlink+"actuate","onLoad")),
                    alt is null?null:new XElement(SvgOdt+"desc",alt));
            else
            {
                if(alt is null&&image.Decorative!=true)
                    Issue("EXPORT_IMAGE_ALT",item.Source,"Cette image ne déclare pas de description. Elle reste identifiée comme image dans l’EPUB.",false);
                content=new XElement(Html+"img",new XAttribute("src",asset.Path),
                    new XAttribute("alt",alt??(image.Decorative==true?"":"Illustration")),
                    new XAttribute("style","max-width:100%;height:auto"));
            }
        }
        else if(item.Value is {} text)
        {
            if(Odt)
            {
                var span=new XElement(Text+"span",OdtText(text));
                if(item.Style is {} style)span.SetAttributeValue(Text+"style-name",Register("text",OdtStyle(style,Family(item))));
                content=span;
            }
            else
            {
                var span=new XElement(Html+"span",text);
                if(item.Style is {} style)
                {
                    span.SetAttributeValue("style",CssStyle(style,Family(item)));
                    if(style.Language!="und")span.SetAttributeValue(XNamespace.Xml+"lang",style.Language);
                    if(style.RightToLeft)span.SetAttributeValue("dir","rtl");
                }
                content=span;
            }
        }
        else return null;
        if(item.Link is not {} link)return content;
        string? href=link is {Status:ReferenceStatus.Resolved,TargetId:{} target}?"#"+Id(target):Publications.SafeUrl(link.Relation?.Target);
        if(href is null)
        {
            Issue("EXPORT_LINK_UNSUPPORTED",item.Source,"Cible du lien indisponible ou hors profil.");
            return content;
        }
        Feature("hyperlinks",PublicationRetention.Native,item.Source.Location,"Lien interne ou externe vérifié.");
        return new XElement(Odt?Text+"a":Html+"a",new XAttribute(Odt?Xlink+"href":"href",href),
            Odt?new XAttribute(Xlink+"type","simple"):null,content);
    }

    XElement WriteNote(Inline item,SemanticReference note)
    {
        bool end=note.Kind=="endnote";
        string id=Id(note.TargetId!),reference=Id(item.Source.NodeId);
        if(!writtenNotes.Add(id))
        {
            Issue("EXPORT_NOTE_REPEATED",item.Source,"Référence de note partagée hors du profil de numérotation.");
            return new XElement(Odt?Text+"span":Html+"span");
        }
        string label=(notes.Count(n=>n.End==end)+1).ToString(CultureInfo.InvariantCulture);
        var blocks=Blocks(model.BlocksOf(note.TargetId!).Select(e=>e.Id));
        notes.Add((id,end,label,blocks));
        Feature("notes",PublicationRetention.Native,item.Source.Location,"Appels et corps de notes, avec retour dans l’EPUB.");
        return Odt?new XElement(Text+"note",new XAttribute(Text+"id",id),new XAttribute(Text+"note-class",end?"endnote":"footnote"),
                new XElement(Text+"note-citation",label),new XElement(Text+"note-body",blocks)):
            new XElement(Html+"a",new XAttribute("href","#"+id),new XAttribute("id",reference),
                new XAttribute(Epub+"type","noteref"),new XAttribute("role","doc-noteref"),label);
    }

    XElement BuildTable(LayoutTable table)
    {
        var grid=new XElement(Odt?Table+"table":Html+"table");
        if(Odt)
        {
            grid.SetAttributeValue(Table+"name",Id(table.Id));
            grid.Add(table.Columns.Select(width=>new XElement(Table+"table-column",new XAttribute(Table+"style-name",
                Register("table-column",new XElement(Style+"table-column-properties",new XAttribute(Style+"column-width",Pt(width))))))));
        }
        var body=Odt?grid:new XElement(Html+"tbody");
        if(!Odt)grid.Add(body);
        var cells=table.Cells.ToLookup(c=>c.Row);
        foreach(var row in table.Rows)
        {
            token.ThrowIfCancellationRequested();
            var tr=new XElement(Odt?Table+"table-row":Html+"tr");
            var starts=cells[row.Index].Where(c=>c.MergeOrigin is null).ToDictionary(c=>c.Column);
            for(int column=0;column<table.Columns.Count;column++)
            {
                if(starts.TryGetValue(column,out var cell))
                {
                    string element=Odt?"table-cell":row.Header?"th":"td";
                    var td=new XElement(Odt?Table+element:Html+element,Blocks(model.BlocksOf(cell.Id).Select(n=>n.Id)));
                    if(cell.ColumnSpan>1)td.SetAttributeValue(Odt?Table+"number-columns-spanned":"colspan",cell.ColumnSpan);
                    if(cell.RowSpan>1)td.SetAttributeValue(Odt?Table+"number-rows-spanned":"rowspan",cell.RowSpan);
                    if(Odt)
                    {
                        var properties=new XElement(Style+"table-cell-properties",new XAttribute(Fo+"padding-top",Pt(cell.Margins.Top)),
                            new XAttribute(Fo+"padding-bottom",Pt(cell.Margins.Bottom)),new XAttribute(Fo+"padding-left",Pt(cell.Margins.Left)),
                            new XAttribute(Fo+"padding-right",Pt(cell.Margins.Right)));
                        if(cell.Fill is {} color)properties.SetAttributeValue(Fo+"background-color","#"+color);
                        foreach(var (edge,border) in Borders(cell.Borders))
                            if(border is not null)properties.SetAttributeValue(Fo+("border-"+edge),Border(border));
                        td.SetAttributeValue(Table+"style-name",Register("table-cell",properties));
                    }
                    else
                    {
                        var css=Borders(cell.Borders).Where(b=>b.Border is not null).Select(b=>$"border-{b.Edge}:{Border(b.Border!)}").ToList();
                        if(cell.Fill is {} color)css.Add("background-color:#"+color);
                        if(css.Count>0)td.SetAttributeValue("style",string.Join(';',css));
                    }
                    tr.Add(td);
                }
                else if(Odt)tr.Add(new XElement(Table+"covered-table-cell"));
            }
            body.Add(tr);
        }
        Feature("tables",PublicationRetention.Native,table.Source,"Tableau éditable, fusions et propriétés de cellules.");
        return grid;
    }

    Asset? AssetFor(InlineImage image)
    {
        string key=image.Sha256+":"+image.ResourceId;
        if(assetsById.TryGetValue(key,out var known))return known;
        byte[] bytes;
        string extension,mime;
        if(image.VmlVector is not null||image.Diagram is not null)
        {
            bytes=Encoding.UTF8.GetBytes(PublicationSvg.Encode(image));
            extension="svg";mime="image/svg+xml";
            Feature("vectors",PublicationRetention.Vector,image.Source,"Chemins et texte transmis en SVG.");
        }
        else if(image.RenderBytes.Length>0&&image.Crop==default&&image.Effects==default&&!image.FlipX&&!image.FlipY&&image.Orientation==1)
        {
            bytes=image.RenderBytes;
            (extension,mime)=bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10})?("png","image/png"):
                bytes.AsSpan().StartsWith(new byte[]{255,216,255})?("jpg","image/jpeg"):("","");
            if(extension.Length==0)return null;
            Feature("images",PublicationRetention.Native,image.Source,"Image intégrée réutilisée.");
        }
        else return null;
        var asset=new Asset((Odt?"Pictures/":"images/")+Assets.Count.ToString(CultureInfo.InvariantCulture)+"."+extension,mime,bytes);
        Assets.Add(asset);
        assetsById.Add(key,asset);
        return asset;
    }

    string? AlternativeText(string id)
    {
        var candidates=new Stack<string>();
        candidates.Push(id);
        while(candidates.TryPop(out string? candidate))
        {
            token.ThrowIfCancellationRequested();
            string? label=model.Attribute(candidate,"descr",ns:"")??model.Attribute(candidate,"title",ns:"");
            if(!string.IsNullOrWhiteSpace(label))return label;
            foreach(var child in model.Children(candidate).Reverse())candidates.Push(child.Id);
        }
        return null;
    }

    internal string Id(string node)=>"n"+model.Element(node).Order.ToString(CultureInfo.InvariantCulture);
    internal void Issue(string code,SemanticSource location,string message,bool blocks=true)=>AddIssue(code,location.Location,message,blocks);
    void AddIssue(string code,SourceLocation? location,string message,bool blocks)
    {
        if(Issues.Count>=4096)throw new InvalidDataException("EXPORT_DIAGNOSTIC_LIMIT");
        if(seen.Add((code,location?.PartUri+location?.Path)))Issues.Add(new(code,location,message,false,blocks));
    }
    internal void Feature(string kind,PublicationRetention retention,SourceLocation? location,string detail)
    {
        if(seen.Add((kind,null)))Features.Add(new(kind,retention,location,detail));
    }

    internal string Register(string family,XElement properties)
    {
        if(styles.TryGetValue((family,properties),out var id))return id;
        string name="Lw"+styles.Count.ToString(CultureInfo.InvariantCulture);
        styles.Add((family,properties),name);
        Automatic.Add(new XElement(Style+"style",new XAttribute(Style+"family",family),new XAttribute(Style+"name",name),properties));
        return name;
    }

    sealed class StyleEquality:IEqualityComparer<(string,XElement)>
    {
        readonly XNodeEqualityComparer nodes=new();
        public bool Equals((string,XElement) x,(string,XElement) y)=>x.Item1==y.Item1&&nodes.Equals(x.Item2,y.Item2);
        public int GetHashCode((string,XElement) value)=>HashCode.Combine(value.Item1,nodes.GetHashCode(value.Item2));
    }

    internal static string? Family(Inline item)=>item.Font?.UsedFamily??(item.Style?.Slots.Ascii is {Theme:false} family?family.Value:null);
    internal static string Language(PublicationMetadata metadata)
    {
        string? language=metadata.Language;
        return language is { Length:>0 and <=64 }&&language.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')?language:"und";
    }
    internal static string Xml(XElement value)=>"<?xml version=\"1.0\" encoding=\"UTF-8\"?>"+value.ToString(SaveOptions.DisableFormatting);
    internal static string Num(float n)=>n.ToString("0.#####",CultureInfo.InvariantCulture);
    internal static string Pt(float n)=>Num(n)+"pt";
    internal static string Alignment(string a)=>a switch{"center"=>"center","right"=>"right","both" or "justify"=>"justify",_=>"left"};
    static string Border(LayoutBorder border)=>Pt(border.Width)+" "+(border.Style=="double"?"double":"solid")+" #"+border.Color;
    static IEnumerable<(string Edge,LayoutBorder? Border)> Borders(CellBorders b)=>
        [("top",b.Top),("right",b.Right),("bottom",b.Bottom),("left",b.Left)];

    internal static XElement OdtStyle(TextStyle style,string? family)
    {
        var result=new XElement(Style+"text-properties",new XAttribute(Fo+"font-size",Pt(style.Size)),
            new XAttribute(Fo+"font-weight",style.Bold?"bold":"normal"),new XAttribute(Fo+"font-style",style.Italic?"italic":"normal"));
        if(style.Color!="auto")result.SetAttributeValue(Fo+"color","#"+style.Color);
        if(style.Background is {} background)result.SetAttributeValue(Fo+"background-color",
            background.StartsWith('#')?background:"#"+background);
        if(family is not null)result.SetAttributeValue(Fo+"font-family",family);
        if(style.Underline=="single")result.SetAttributeValue(Style+"text-underline-style","solid");
        if(style.Tracking!=0)result.SetAttributeValue(Fo+"letter-spacing",Pt(style.Tracking));
        if(style.Language!="und")result.SetAttributeValue(XNamespace.Xml+"lang",style.Language);
        return result;
    }

    internal static string CssStyle(TextStyle style,string? font)
    {
        var values=new List<string>
        {
            "font-size:"+Pt(style.Size),"font-weight:"+(style.Bold?"bold":"normal"),"font-style:"+(style.Italic?"italic":"normal")
        };
        if(font is not null)values.Add("font-family:\""+string.Concat(font.Select(c=>
            char.IsLetterOrDigit(c)||c is ' ' or '-'?c.ToString():$"\\{(int)c:X} "))+"\"");
        if(style.Color!="auto")values.Add("color:#"+style.Color);
        if(style.Background is {} background)values.Add("background-color:"+(background.StartsWith('#')?background:"#"+background));
        if(style.Underline=="single")values.Add("text-decoration:underline");
        if(style.Tracking!=0)values.Add("letter-spacing:"+Pt(style.Tracking));
        return string.Join(';',values);
    }

    internal static IEnumerable<XNode> OdtText(string text)
    {
        for(int i=0;i<text.Length;)
        {
            int start=i;
            char c=text[i++];
            if(c==' ')
            {
                while(i<text.Length&&text[i]==' ')i++;
                yield return new XElement(Text+"s",new XAttribute(Text+"c",i-start));
            }
            else if(c=='\n')yield return new XElement(Text+"line-break");
            else if(c=='\t')yield return new XElement(Text+"tab");
            else
            {
                while(i<text.Length&&text[i] is not (' ' or '\t' or '\n'))i++;
                yield return new XText(text[start..i]);
            }
        }
    }
}
