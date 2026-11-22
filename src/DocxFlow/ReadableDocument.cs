using System.Diagnostics;
using System.Globalization;
using System.Text;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

/// <summary>Continuous, explicitly approximate reading view. Never a pagination or export qualification.</summary>
public sealed record ReadableNavigation(string Label, string BlockId, SourceLocation Source, int Level = 0);
public sealed record ReadableBlock(string Id, SourceLocation Source, string Kind, string Text, IReadOnlyList<ReadableBlock> Children)
{
    public string? Label { get; init; }
    public int HeadingLevel { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Hidden { get; init; }
    public double? FontSize { get; init; }
    public string? FontFamily { get; init; }
    public string? Alignment { get; init; }
    public string? ListLabel { get; init; }
    public int ListLevel { get; init; }
    public string? ResourceId { get; init; }
    public string? LinkTarget { get; init; }
    public string? NoteId { get; init; }
    public string? NoteKind { get; init; }
    public int ColumnSpan { get; init; } = 1;
    public string? VerticalMerge { get; init; }
    public string Revision { get; init; } = "unchanged";
    public string FieldRole { get; init; } = "none";
    public bool IsApproximate { get; init; } = true;
    // Summary fields on paragraphs/runs are not repeated when walking their children.
    public string PlainText => Children.Count == 0 ? Text : Kind is "paragraph" or "heading" or "run" or "link"
        ? string.Concat(Children.Select(c => c.PlainText))
        : string.Join(Kind is "table" or "row" or "cell" or "section" ? "\n" : "", new[] { Text }.Where(t => t.Length > 0).Concat(Children.Select(c => c.PlainText)));
}
public sealed record ReadableDocument(string SourceHash, bool Available, IReadOnlyList<ReadableBlock> Blocks,
    IReadOnlyList<FlowIssue> Issues, IReadOnlyList<ReadableNavigation> Outline, IReadOnlyList<ReadableNavigation> Bookmarks)
{
    public string PlainText => string.Join("\n", Blocks.Select(b => b.PlainText));
    public IReadOnlyDictionary<string, byte[]> Resources { get; init; } = new Dictionary<string, byte[]>();
    public ReadablePresentation Presentation { get; init; } = new();
    public bool IsApproximate => true;
    public bool PaginationAvailable => false;
}

public static class ReadableReader
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static ReadableDocument Read(InspectionResult result, CancellationToken cancellationToken = default)
    {
        if (!result.Manifest.InspectionComplete || result.Manifest.Status == Coverage.Refused)
            return new(result.Manifest.SourceSha256 ?? "", false, [], [new("READ_INSPECTION_REFUSED", null, "Lecture indisponible : inspection refusée ou incomplète.", true)], [], []);
        try { return new Projection(result, cancellationToken).Build(); }
        catch (ReadLimitException)
        { return new(result.Manifest.SourceSha256 ?? "", false, [], [new("READ_RESOURCE_LIMIT", null, "Limite de lecture simplifiée atteinte ; inventaire complet conservé.", true)], [], []); }
    }
    sealed class ReadLimitException : Exception;
    sealed record TextStyle(bool? Bold = null, bool? Italic = null, bool? Underline = null, bool? Hidden = null,
        double? FontSize = null, string? FontFamily = null, string? Alignment = null, int? HeadingLevel = null)
    {
        public TextStyle Over(TextStyle child) => new(child.Bold ?? Bold, child.Italic ?? Italic, child.Underline ?? Underline,
            child.Hidden ?? Hidden, child.FontSize ?? FontSize, child.FontFamily ?? FontFamily, child.Alignment ?? Alignment, child.HeadingLevel ?? HeadingLevel);
    }
    sealed class Projection
    {
        readonly InspectionResult input;
        readonly CancellationToken token;
        readonly Stopwatch watch = Stopwatch.StartNew();
        readonly IReadOnlyDictionary<string, InventoryNode> nodes;
        readonly SemanticIndex index;
        readonly Dictionary<string, InventoryNode> styles;
        readonly Dictionary<string, TextStyle> styleCache = new(StringComparer.Ordinal);
        readonly List<FlowIssue> issues = [];
        readonly List<ReadableNavigation> outline = [], bookmarks = [];
        readonly HashSet<string> issueKeys = new(StringComparer.Ordinal);
        readonly HashSet<string> noteStack = new(StringComparer.Ordinal);
        readonly Dictionary<(string Num, int Level), int> listCounters = [];
        readonly Dictionary<string, byte[]> checkedImages = new(StringComparer.Ordinal);
        int count, noteExpansions;
        long characters, decodedImagePixels;
        string main = "";
        TextStyle defaults = new();
        public Projection(InspectionResult input, CancellationToken token)
        {
            this.input = input; this.token = token;
            if (input.Manifest.Nodes.Count > 150_000) throw new ReadLimitException();
            index = new(input.Manifest, Check);
            nodes = index.Nodes;
            styles = input.Manifest.Nodes.Where(n => n.Kind == "style").GroupBy(n => Attr(n, "styleId"), StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds > 10_000 || count > 30_000 || characters > 2_000_000 || noteExpansions > 512) throw new ReadLimitException();
        }
        static bool Is(InventoryNode n, string local) => n.Name == "{" + W + "}" + local;
        static string Attr(InventoryNode n, string local) => n.Attributes.GetValueOrDefault("{" + W + "}" + local) ?? "";
        InventoryNode? Child(InventoryNode n, string local) => index.Children(n).FirstOrDefault(c => Is(c, local));
        IEnumerable<InventoryNode> Descendants(InventoryNode n, int depth = 0)
        {
            Check(); if (depth > 64) throw new ReadLimitException();
            foreach (var child in index.Children(n)) { yield return child; foreach (var deeper in Descendants(child, depth + 1)) yield return deeper; }
        }
        bool ImportantProperties(InventoryNode n) => Descendants(n).Any(c => c.Kind == "revision" || c.Revision != "unchanged" || c.Name.EndsWith("Change", StringComparison.Ordinal) || c.Kind == "textValue" && !string.IsNullOrWhiteSpace(c.Text));
        void Issue(string code, InventoryNode? n, string message)
        {
            if (issues.Count >= 10_000) throw new ReadLimitException();
            string key = code + ":" + n?.Id;
            if (issueKeys.Add(key)) issues.Add(new(code, n?.Source, message, false));
        }
        public ReadableDocument Build()
        {
            Check();
            main = index.Main;
            var body = index.Body;
            if (body is null) return new(input.Manifest.SourceSha256 ?? "", false, [], [new("READ_MAIN_MISSING", null, "Corps documentaire absent.", true)], [], []);
            var docDefaults = input.Manifest.Nodes.FirstOrDefault(n => Is(n, "docDefaults"));
            if (docDefaults is not null) defaults = ParseProperties(docDefaults, true);
            var blocks = index.Children(body).Select(n => Convert(n, defaults, n.Id, 0)).Where(b => b.Kind != "metadata").ToList();
            // Running stories remain visibly separated from the body; this does not place them on pages.
            foreach (var relation in input.Manifest.Relationships.Where(r => r.SourcePart == main && r.Mode == "Internal" &&
                (r.Type == R + "/header" || r.Type == R + "/footer")).DistinctBy(r => r.ResolvedPart))
            {
                var story = input.Manifest.Nodes.FirstOrDefault(n => n.Source.PartUri == relation.ResolvedPart && n.ParentId is null);
                if (story is null) continue;
                var context = ConvertChildren(story, defaults, story.Id, 0);
                blocks.Add(Make(story, "section", "", context, defaults) with { Label = relation.Type.EndsWith("/header", StringComparison.Ordinal) ? "En-tête source — placement non paginé" : "Pied de page source — placement non paginé" });
                Issue("READ_RUNNING_STORY", story, "En-tête/pied présenté à part ; son emplacement Word n’est pas reproduit.");
            }
            Issue("READ_APPROXIMATE", body, "Lecture simplifiée continue : aucune pagination, fidélité Word ou qualification d’export déduite.");
            Check();var document=new ReadableDocument(input.Manifest.SourceSha256 ?? "", true, blocks, issues, outline, bookmarks) { Resources = checkedImages };
            var presentation=ReadablePresentation.Create(input,document,index,token);
            var headings=ReadablePresentation.Walk(presentation.BodyBlocks).Where(b=>b.Kind=="heading")
                .DistinctBy(b=>b.Source).ToDictionary(b=>b.Source,b=>presentation.TextFor(b).Trim());
            return document with {Presentation=presentation,Outline=[..outline
                .Where(o=>headings.TryGetValue(o.Source,out var title)&&title.Length>0)
                .DistinctBy(o=>o.Source).Select(o=>o with {Label=headings[o.Source]})]};
        }
        ReadableBlock Make(InventoryNode n, string kind, string text, IReadOnlyList<ReadableBlock> nested, TextStyle style)
        {
            count++; characters += text.Length; Check();
            return new(n.Id, n.Source, kind, text, nested)
            {
                Bold = style.Bold ?? false, Italic = style.Italic ?? false, Underline = style.Underline ?? false,
                Hidden = style.Hidden ?? false, FontSize = style.FontSize, FontFamily = style.FontFamily, Alignment = style.Alignment,
                HeadingLevel = style.HeadingLevel ?? 0, Revision = n.Revision, FieldRole = n.FieldRole
            };
        }
        List<ReadableBlock> ConvertChildren(InventoryNode n, TextStyle style, string context, int depth) =>
            index.Children(n).Select(c => Convert(c, style, context, depth + 1)).Where(b => b.Kind != "metadata").ToList();
        ReadableBlock Convert(InventoryNode n, TextStyle inherited, string context, int depth)
        {
            Check(); if (depth > 64) throw new ReadLimitException();
            if (n.Name.StartsWith('#'))
            {
                if (n.Kind != "textValue" || string.IsNullOrWhiteSpace(n.Text) && (n.ParentId is null || !nodes.TryGetValue(n.ParentId, out var parent) || !Is(parent, "t") && !Is(parent, "delText") && !Is(parent, "instrText")))
                    return Make(n, "metadata", "", [], inherited);
                return Make(n, "text", n.Text ?? "", [], inherited);
            }
            if (index.UnsupportedMustUnderstand(n))
                Issue("READ_MUST_UNDERSTAND", n, "Espace de noms MustUnderstand hors profil. Présentation approximative et source conservée.");
            if (SemanticIndex.IsAlternative(n))
            {
                var branch = index.SelectedBranch(n);
                if (branch is not null && index.UnsupportedMustUnderstand(branch))
                    Issue("READ_MUST_UNDERSTAND", branch, "Branche avec espace de noms MustUnderstand hors profil.");
                Issue(branch is null ? "READ_ALTERNATIVE_UNRESOLVED" : "READ_ALTERNATIVE_SELECTED", n,
                    branch is null ? "Aucune branche compatible. Alternatives conservées dans l’inventaire."
                        : "Une branche de compatibilité est présentée. Les autres restent dans l’inventaire.");
                return Make(n, "placeholder", "", branch is null ? [] : ConvertChildren(branch, inherited, context, depth + 1), inherited)
                    with { Label = branch is null ? "Alternative non représentée" : "Alternative de compatibilité" };
            }
            if (Is(n, "p"))
            {
                var properties = Child(n, "pPr");
                string id = properties is null ? "" : Child(properties, "pStyle") is {} pStyle ? Attr(pStyle, "val") : "";
                var style = inherited.Over(ResolveStyle(id, new HashSet<string>(StringComparer.Ordinal)));
                if (properties is not null) style = style.Over(ParseProperties(properties));
                var contentList = index.Children(n).Where(c => !Is(c, "pPr")).Select(c => Convert(c, style, context, depth + 1)).Where(b => b.Kind != "metadata").ToList();
                if (properties is not null)
                {
                    Issue("READ_FORMATTING_APPROXIMATE", properties, "Propriétés de paragraphe interprétées partiellement ; mise en forme Word non garantie.");
                    if (Child(properties,"sectPr") is {} section) contentList.Add(Convert(section,style,context,depth+1));
                    if (ImportantProperties(properties))
                        contentList.Add(Make(properties,"placeholder","",ConvertChildren(properties,style,context,depth+1),style) with { Label="Propriétés contenant une révision ou du texte — inventaire conservé" });
                }
                var content=contentList.ToArray();
                int heading = style.HeadingLevel ?? 0;
                if (heading == 0) heading = HeadingFromName(id);
                var block = Make(n, heading > 0 ? "heading" : "paragraph", "", content, style) with { HeadingLevel = heading };
                if (properties is not null && Child(properties, "numPr") is {} numbering)
                {
                    var (label, level) = ListMarker(numbering);
                    block = block with { ListLabel = label, ListLevel = level };
                }
                if (heading > 0) outline.Add(new(block.PlainText, context, n.Source, heading));
                return block with { Text = block.PlainText };
            }
            if (Is(n, "r"))
            {
                var properties = Child(n, "rPr"); string id = properties is null ? "" : Child(properties, "rStyle") is {} rs ? Attr(rs, "val") : "";
                var style = inherited.Over(ResolveStyle(id, new HashSet<string>(StringComparer.Ordinal)));
                if (properties is not null) { style = style.Over(ParseProperties(properties));Issue("READ_FORMATTING_APPROXIMATE",properties,"Propriétés de caractères interprétées partiellement ; styles Word non garantis."); }
                var runContent = index.Children(n).Where(c => !Is(c, "rPr")).Select(c => Convert(c, style, context, depth + 1)).Where(b => b.Kind != "metadata").ToList();
                if(properties is not null && ImportantProperties(properties))runContent.Add(Make(properties,"placeholder","",ConvertChildren(properties,style,context,depth+1),style) with {Label="Propriétés de caractères avec révision ou contenu"});
                var nested=runContent.ToArray();
                if (style.Hidden == true) Issue("READ_HIDDEN_TEXT", n, "Texte masqué présenté avec son état ; il n’est pas supprimé.");
                var block = Make(n, "run", "", nested, style); return block with { Text = block.PlainText };
            }
            if (Is(n, "t") || Is(n, "delText"))
                return Make(n, "text", string.Concat(index.Children(n).Where(c => c.Kind == "textValue").Select(c => c.Text)), [], inherited);
            if (Is(n, "tab")) return Make(n, "text", "\t", [], inherited);
            if (Is(n, "br") || Is(n, "cr"))
            {
                string type=Attr(n,"type");
                if(type=="page") {Issue("READ_PAGE_BREAK",n,"Saut de page source repéré, sans pagination calculée.");return Make(n,"break","[Saut de page source]",[],inherited);}
                if(type=="column") {Issue("READ_COLUMN_BREAK",n,"Saut de colonne source repéré ; colonnes non reproduites.");return Make(n,"break","[Saut de colonne source — non reproduit]",[],inherited);}
                if(type.Length>0&&type!="textWrapping") {Issue("READ_BREAK_UNQUALIFIED",n,"Type de saut non interprété ; repère conservé.");return Make(n,"break","[Saut source non représenté]",[],inherited);}
                return Make(n,"break","\n",[],inherited);
            }
            if (Is(n, "tbl"))
            {
                Issue("READ_TABLE_APPROXIMATE", n, "Tableau en lecture simplifiée ; largeurs, bordures et pagination Word non garanties.");
                return Make(n, "table", "", ConvertChildren(n,inherited,context,depth), inherited);
            }
            if (Is(n, "tr")) return Make(n, "row", "", ConvertChildren(n,inherited,context,depth), inherited);
            if (Is(n, "tc"))
            {
                var properties = Child(n, "tcPr"); var span = properties is null ? null : Child(properties, "gridSpan"); var merge = properties is null ? null : Child(properties, "vMerge");
                int columns = span is not null && int.TryParse(Attr(span, "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? Math.Clamp(value, 1, 64) : 1;
                if (columns > 1 || merge is not null) Issue("READ_TABLE_MERGE", n, "Fusion source repérée ; géométrie de fusion simplifiée et explicitée.");
                return Make(n, "cell", "", index.Children(n).Where(c => !Is(c, "tcPr") || ImportantProperties(c)).Select(c => Convert(c, inherited, context, depth + 1)).ToArray(), inherited) with
                { ColumnSpan = columns, VerticalMerge = merge is null ? null : Attr(merge, "val") is { Length: > 0 } kind ? kind : "continue" };
            }
            if (Is(n, "hyperlink"))
            {
                string id = n.Attributes.GetValueOrDefault("{" + R + "}id") ?? "";
                string? target = Attr(n, "anchor") is { Length: > 0 } anchor ? "#" + anchor : index.Relation(n.Source.PartUri, id)?.Target;
                Issue("READ_LINK_INERT", n, "Lien conservé pour lecture, sans ouverture ni accès réseau automatique.");
                var block = Make(n, "link", "", ConvertChildren(n, inherited, context, depth), inherited) with { LinkTarget = target };
                return block with { Text = block.PlainText };
            }
            if (Is(n, "bookmarkStart"))
            {
                string name = Attr(n, "name"); bookmarks.Add(new(name, context, n.Source));
                return Make(n, "bookmark", "", [], inherited) with { Label = "Signet : " + name };
            }
            if (Is(n, "bookmarkEnd")) return Make(n, "metadata", "", [], inherited);
            if (Is(n, "footnoteReference") || Is(n, "endnoteReference")) return Note(n, inherited, context, depth);
            if (Is(n, "fldChar") || Is(n, "fldSimple") || Is(n, "instrText"))
            {
                Issue("READ_FIELD_PRESERVED", n, "Champ conservé : code et résultat restent distingués, sans recalcul.");
                string code = Is(n, "fldSimple") ? Attr(n, "instr") : Is(n, "fldChar") ? Attr(n, "fldCharType") : "";
                return Make(n, "field", code.Length == 0 ? "" : "[Champ : " + code + "]", ConvertChildren(n, inherited, context, depth), inherited) with
                { Label = n.FieldRole == "instruction" || Is(n, "instrText") ? "Code de champ — non exécuté" : "Champ — non recalculé" };
            }
            if (Is(n, "ins") || Is(n, "del") || Is(n, "moveFrom") || Is(n, "moveTo"))
            {
                Issue("READ_REVISION_PRESERVED", n, "Révision présentée séparément ; aucune acceptation ou suppression implicite.");
                return Make(n, "revision", "", ConvertChildren(n, inherited, context, depth), inherited) with { Label = n.Revision == "deleted" ? "Texte supprimé — révision conservée" : "Texte inséré — révision conservée" };
            }
            if (Is(n, "drawing") || Is(n, "pict")) return Image(n, inherited,context,depth);
            if (Is(n, "sectPr"))
            {
                Issue("READ_SECTION_PROPERTIES", n, "Section source repérée ; marges, colonnes et disposition non paginées.");
                return Make(n, "section", "", [], inherited) with { Label = "Section source — disposition Word non reproduite" };
            }
            if (Is(n, "sdt") || Is(n, "sdtContent"))
            {
                Issue("READ_CONTENT_CONTROL", n, "Contrôle de contenu affiché en lecture seule, sans liaison active.");
                return Make(n, "placeholder", "", ConvertChildren(n, inherited, context, depth), inherited) with { Label = "Contrôle de contenu — lecture seule" };
            }
            if (Is(n, "pPr") || Is(n, "rPr") || Is(n, "sdtPr") || Is(n,"tcPr") || Is(n, "tblPr") || Is(n, "tblGrid") || Is(n, "trPr"))
            {
                Issue("READ_PROPERTIES_APPROXIMATE",n,"Propriétés conservées dans l’inventaire ; représentation partielle.");
                if(ImportantProperties(n))
                    return Make(n,"placeholder","",ConvertChildren(n,inherited,context,depth),inherited) with { Label="Propriétés avec contenu non représenté" };
                return Make(n,"metadata","",[],inherited);
            }
            Issue("READ_UNREPRESENTED_ELEMENT", n, "Élément non représenté fidèlement ; son texte et sa provenance restent consultables : " + n.Name);
            return Make(n, "placeholder", "", ConvertChildren(n, inherited, context, depth), inherited) with { Label = "Élément non représenté : " + n.Name };
        }
        ReadableBlock Note(InventoryNode n, TextStyle style, string context, int depth)
        {
            string id = Attr(n, "id"), kind = Is(n, "footnoteReference") ? "footnote" : "endnote";
            Issue("READ_NOTE_INLINE", n, "Note conservée près de son appel ; placement et numérotation Word non reproduits.");
            string? part = index.Related(n.Source.PartUri, kind + "s")?.ResolvedPart;
            var definition = index.Definition(part, kind, id);
            var nested = new List<ReadableBlock>();string key = part + ":" + id;
            if (definition is not null && noteStack.Add(key))
            { noteExpansions++; nested.AddRange(ConvertChildren(definition, style, context, depth)); noteStack.Remove(key); }
            else Issue("READ_NOTE_UNRESOLVED", n, "Corps de note absent ou référence cyclique ; appel conservé.");
            return Make(n, "note", "", nested, style) with { NoteId = id, NoteKind = kind, Label = (kind == "footnote" ? "Note de bas de page #" : "Note de fin #") + id };
        }
        ReadableBlock Image(InventoryNode n, TextStyle style, string context, int depth)
        {
            var all=Descendants(n).ToArray();var content=new List<ReadableBlock>();
            foreach(var reference in all.Where(d=>d.Name.EndsWith("}blip",StringComparison.Ordinal)||d.Name.EndsWith("}imagedata",StringComparison.Ordinal)||d.Name.EndsWith("}svgBlip",StringComparison.Ordinal)))
            {
                string? id=reference.Attributes.GetValueOrDefault("{"+R+"}embed")??reference.Attributes.GetValueOrDefault("{"+R+"}link")??reference.Attributes.GetValueOrDefault("{"+R+"}id");
                var relation=id is null ? null : index.Relation(n.Source.PartUri, id);
                var media=input.Manifest.Media.FirstOrDefault(m=>m.PartUri==relation?.ResolvedPart);string? resource=null;
                if(relation?.Mode=="External")Issue("READ_EXTERNAL_IMAGE",reference,"Image liée externe non chargée.");
                else if(media is not null&&input.SafeMedia.TryGetValue(media.Id,out var bytes))
                {
                    if(checkedImages.ContainsKey(media.Id))resource=media.Id;
                    else if(checkedImages.Count<64 && decodedImagePixels+(long)(media.Width??8192)*(media.Height??8192)<=24_000_000 && SafePng.TryGetRenderableBytes(bytes,out var renderable,out string reason,token))
                    {
                        checkedImages.Add(media.Id,renderable);resource=media.Id;decodedImagePixels+=(long)(media.Width??0)*(media.Height??0);
                        if(reason.Length>0)Issue("READ_PNG_METADATA_OMITTED",reference,reason);
                    }
                    else Issue("READ_IMAGE_UNQUALIFIED",reference,"Image hors profil de décodage borné ; repère conservé.");
                }
                else Issue("READ_IMAGE_UNQUALIFIED",reference,"Format d’image non qualifié ; repère conservé sans décodage.");
                content.Add(Make(reference,"image","",[],style) with {ResourceId=resource,Label=resource is null?"[Image non représentée]":"Image — placement simplifié"});
            }
            var paragraphRoots=all.Where(d=>Is(d,"p")&&!AncestorsUntil(d,n).Any(a=>Is(a,"p"))).ToArray();
            foreach(var paragraph in paragraphRoots)content.Add(Convert(paragraph,style,context,depth+1));
            foreach(var text in all.Where(d=>d.Kind=="textValue"&&!string.IsNullOrWhiteSpace(d.Text)&&!AncestorsUntil(d,n).Any(a=>Is(a,"p"))))
            {
                if(text.ParentId is {} parent&&nodes.TryGetValue(parent,out var owner))
                {
                    if(owner.Name is "{http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing}posOffset" or
                        "{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}pctWidth" or
                        "{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}pctHeight")
                        Issue("READ_DRAWING_GEOMETRY_METADATA",owner,"Paramètre de géométrie DrawingML conservé dans l’inventaire ; il ne constitue pas du texte du document.");
                    else content.Add(Make(text,"placeholder",text.Text??"",[],style) with {Label="Texte du dessin — représentation simplifiée"});
                }
            }
            if(content.Count==0)content.Add(Make(n,"image","",[],style) with {Id=n.Id+"-marker",Label="[Dessin ou image non représenté]"});
            Issue("READ_IMAGE_APPROXIMATE",n,"Dessin présenté dans le flux ; ancrage, habillage et géométrie Word non reproduits.");
            return Make(n,"placeholder","",content,style) with {Label="Dessin — lecture simplifiée"};
        }
        IEnumerable<InventoryNode> AncestorsUntil(InventoryNode node,InventoryNode stop)
        {
            for(int i=0;node.ParentId is {} parent&&nodes.TryGetValue(parent,out var owner)&&owner.Id!=stop.Id;i++)
            {if(i>64)throw new ReadLimitException();yield return owner;node=owner;}
        }
        TextStyle ResolveStyle(string id, HashSet<string> seen)
        {
            if (id.Length == 0) return new();
            if (styleCache.TryGetValue(id, out var cached)) return cached;
            if (seen.Count > 32 || !seen.Add(id) || !styles.TryGetValue(id, out var definition)) return new();
            string parent = Child(definition, "basedOn") is {} based ? Attr(based, "val") : "";
            var result = ResolveStyle(parent, seen).Over(ParseProperties(definition, true));
            int heading = HeadingFromName(id);
            if (heading == 0 && Child(definition, "name") is {} name) heading = HeadingFromName(Attr(name, "val"));
            if (heading > 0) result = result with { HeadingLevel = heading };
            styleCache[id] = result; return result;
        }
        TextStyle ParseProperties(InventoryNode n, bool recursive = false)
        {
            TextStyle style = new();
            foreach (var p in recursive ? Descendants(n) : index.Children(n))
            {
                string value = Attr(p, "val"); bool toggle = value is not ("0" or "false" or "off");
                if (Is(p, "b")) style = style with { Bold = toggle };
                else if (Is(p, "i")) style = style with { Italic = toggle };
                else if (Is(p, "u")) style = style with { Underline = value is not ("none" or "0" or "false") };
                else if (Is(p, "vanish") || Is(p, "webHidden")) style = style with { Hidden = toggle };
                else if (Is(p, "sz") && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size is >= 2 and <= 160) style = style with { FontSize = size / 2 };
                else if (Is(p, "rFonts"))
                {
                    string family = Attr(p, "ascii");
                    if (family.Length is > 0 and <= 128 && family.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')) style = style with { FontFamily = family };
                }
                else if (Is(p, "jc") && value is "left" or "center" or "right" or "both" or "start" or "end") style = style with { Alignment = value };
                else if (Is(p, "outlineLvl") && int.TryParse(value, out int level) && level is >= 0 and <= 8) style = style with { HeadingLevel = level + 1 };
            }
            return style;
        }
        static int HeadingFromName(string name)
        {
            string compact = name.Replace(" ", "", StringComparison.Ordinal);
            foreach (string prefix in new[] { "Heading", "Titre" })
                if (compact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(compact[prefix.Length..], out int level) && level is >= 1 and <= 9) return level;
            return name.Equals("Title", StringComparison.OrdinalIgnoreCase) || name.Equals("Titre", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        (string Label, int Level) ListMarker(InventoryNode numPr)
        {
            string num = Child(numPr, "numId") is {} n ? Attr(n, "val") : "";
            int level = Child(numPr, "ilvl") is {} l && int.TryParse(Attr(l, "val"), out int parsed) ? Math.Clamp(parsed, 0, 8) : 0;
            var instance = input.Manifest.Nodes.FirstOrDefault(n => Is(n, "num") && Attr(n, "numId") == num);
            string abstractId = instance is not null && Child(instance, "abstractNumId") is {} a ? Attr(a, "val") : "";
            var definition = input.Manifest.Nodes.FirstOrDefault(n => Is(n, "abstractNum") && Attr(n, "abstractNumId") == abstractId);
            var lvl = definition is null ? null : index.Children(definition).FirstOrDefault(n => Is(n, "lvl") && Attr(n, "ilvl") == level.ToString(CultureInfo.InvariantCulture));
            string format = lvl is not null && Child(lvl, "numFmt") is {} fmt ? Attr(fmt, "val") : "bullet";
            int start = lvl is not null && Child(lvl, "start") is {} st && int.TryParse(Attr(st, "val"), out int initial) ? Math.Clamp(initial, 0, 1_000_000) : 1;
            if (instance is not null)
            {
                var over = index.Children(instance).FirstOrDefault(n => Is(n, "lvlOverride") && Attr(n, "ilvl") == level.ToString(CultureInfo.InvariantCulture));
                if (over is not null && Child(over, "startOverride") is {} restart && int.TryParse(Attr(restart, "val"), out int first)) start = Math.Clamp(first, 0, 1_000_000);
            }
            int current = listCounters.TryGetValue((num, level), out int previous) ? Math.Min(previous + 1, 1_000_000) : start;
            listCounters[(num, level)] = current;
            foreach (var deeper in listCounters.Keys.Where(k => k.Num == num && k.Level > level).ToArray()) listCounters.Remove(deeper);
            string pattern = lvl is not null && Child(lvl, "lvlText") is {} lt ? Attr(lt, "val") : format == "bullet" ? "•" : "%" + (level + 1).ToString(CultureInfo.InvariantCulture) + ".";
            for (int i = 1; i <= 9; i++) pattern = pattern.Replace("%" + i, listCounters.GetValueOrDefault((num, i - 1), 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (format is not ("bullet" or "decimal")) pattern = current.ToString(CultureInfo.InvariantCulture) + ".";
            Issue("READ_LIST_APPROXIMATE", numPr, "Liste structurée conservée ; libellés et redémarrages sont une approximation explicite.");
            return (pattern.Length > 128 ? "•" : pattern, level);
        }
    }
}
