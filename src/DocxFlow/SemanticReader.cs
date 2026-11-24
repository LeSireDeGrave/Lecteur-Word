using System.Collections.Immutable;
using System.Diagnostics;
using LecteurWord.DocxCore;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal static partial class SemanticReader
{
    internal static SemanticResult Build(InspectionResult input, CancellationToken token = default,
        StyleCollectionKind styles = StyleCollectionKind.Ordinary)
    {
        token.ThrowIfCancellationRequested();
        var manifest = input.Manifest;
        if (!manifest.InspectionComplete || manifest.Status == Coverage.Refused || string.IsNullOrEmpty(manifest.SourceSha256))
            return Refused("SEMANTIC_INSPECTION_REQUIRED", "Inspection complète acceptée requise.");
        if (manifest.Nodes.Count > 150_000)
            return Refused("SEMANTIC_NODE_LIMIT", "Inventaire hors budget du modèle sémantique.");
        try
        {
            return new Builder(manifest, token).Build(styles);
        }
        catch (SemanticLimitException)
        {
            return Refused("SEMANTIC_RESOURCE_LIMIT", "Budget de construction atteint. Inventaire DocxCore disponible.");
        }
    }

    static SemanticResult Refused(string code, string message) => new(null, [new(code, null, message, true)]);
    sealed class SemanticLimitException : Exception;

    sealed partial class Builder
    {
        readonly InspectionManifest manifest;
        readonly SemanticIndex index;
        readonly CancellationToken token;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, SemanticElement> elements = new(StringComparer.Ordinal);
        readonly List<SemanticStory> stories = [];
        readonly List<SemanticAlternative> alternatives = [];
        readonly List<FlowIssue> issues = [];
        IEnumerable<SemanticElement> OrderedElements => index.Ordered.Select(n => elements[n.Id]);
        int work;
        bool diagnosticsLimited;

        internal Builder(InspectionManifest manifest, CancellationToken token)
        {
            this.manifest = manifest;
            this.token = token;
            index = new(manifest, Check);
        }

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (++work > 5_000_000 || clock.ElapsedMilliseconds >= 10_000) throw new SemanticLimitException();
        }

        void Issue(string code, InventoryNode node, string message)
        {
            if (issues.Count < 4096) issues.Add(new(code, node.Source, message, false));
            else diagnosticsLimited = true;
        }

        internal SemanticResult Build(StyleCollectionKind styles)
        {
            if (index.Body is not {} body) return Refused("SEMANTIC_BODY_MISSING", "Corps principal non rattaché à une relation interne unique.");
            int order = 0;
            foreach (var node in index.Ordered)
            {
                Check();
                var parent = node.ParentId is {} parentId ? elements[parentId] : default;
                var kind = Kind(node);
                string? story = parent.StoryId;
                bool inactive = parent.Coverage == SemanticCoverage.InactiveAlternative ||
                    parent.Kind == SemanticKind.Alternative && index.SelectedBranch(index.Nodes[parent.Id])?.Id != node.Id;
                if (kind == SemanticKind.Story)
                {
                    story = node.Id;
                    stories.Add(new(node.Id, Local(node), DeclaredId(node), node.ParentId));
                }

                var coverage = inactive ? SemanticCoverage.InactiveAlternative : kind switch
                {
                    SemanticKind.Metadata => SemanticCoverage.Technical,
                    SemanticKind.Property or SemanticKind.Opaque => SemanticCoverage.InventoryOnly,
                    _ => SemanticCoverage.Represented
                };
                if (index.UnsupportedMustUnderstand(node) && !inactive)
                {
                    coverage = SemanticCoverage.InventoryOnly;
                    Issue("SEMANTIC_MUST_UNDERSTAND", node, "Espace de noms MustUnderstand hors profil. Source conservée.");
                }
                if (kind == SemanticKind.Opaque && !inactive && story is not null && node.Kind != "textValue")
                    Issue("SEMANTIC_OPAQUE", node, "Objet ou propriété hors profil sémantique. QName et source conservés.");
                if (kind == SemanticKind.Alternative)
                {
                    var chosen = index.SelectedBranch(node);
                    alternatives.Add(new(node.Id, chosen?.Id, [..index.RawChildren(node)
                        .Where(n => n.Name is "{" + Mc + "}Choice" or "{" + Mc + "}Fallback").Select(n => n.Id)]));
                    if (!inactive && chosen is null)
                    {
                        if (index.IsEmptyStyleAlternative(node))
                            Issue("SEMANTIC_STYLE_ALTERNATIVE_EMPTY", node,
                                "Alternative de style sans branche comprise ni repli, vide dans la projection MCE. Sources conservées.");
                        else
                            Issue("SEMANTIC_ALTERNATIVE_UNRESOLVED", node, "Aucune branche compatible. Toutes les branches restent consultables.");
                    }
                }
                var element = new SemanticElement(node.Id, order++, story, kind, coverage);
                elements.Add(node.Id, element);
            }

            var blocks = index.LogicalChildren(body).Where(n => !Word(n, "sectPr")&&!index.IsStandaloneBookmark(n)).ToArray();
            var sections = BuildSections(body, blocks);
            var tables = BuildTables();
            long fieldStart = Stopwatch.GetTimestamp();
            var fields = BuildFields();
            double fieldMs = Stopwatch.GetElapsedTime(fieldStart).TotalMilliseconds;
            long tocStart=Stopwatch.GetTimestamp();
            var tocEntries=BuildTocEntries(fields);
            double tocMs=Stopwatch.GetElapsedTime(tocStart).TotalMilliseconds;
            var references = BuildReferences();
            long dependencyStart = Stopwatch.GetTimestamp();
            var customXml = BuildCustomXml(fields);
            double dependencyMs = Stopwatch.GetElapsedTime(dependencyStart).TotalMilliseconds;
            long styleStart = Stopwatch.GetTimestamp();
            var ordinary = ReadStyleCollection(R + "/styles", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
            var effects = ReadStyleCollection(StylesEffectsRelation, "application/vnd.ms-word.stylesWithEffects+xml");
            var compatibility = ReadStyleCompatibility();
            var effective = styles == StyleCollectionKind.Effects ? effects : ordinary;
            var (uses, numbering) = BuildStyles(ordinary, effects, effective);
            double styleMs = Stopwatch.GetElapsedTime(styleStart).TotalMilliseconds;
            if (diagnosticsLimited)
                issues.Add(new("SEMANTIC_DIAGNOSTIC_LIMIT", body.Source, "Détails limités. Le statut de couverture et l’inventaire restent consultables.", false));
            return new(new(manifest, index, [..OrderedElements], elements.AsReadOnly(), [..stories], [..blocks.Select(n => n.Id)], sections,
                references, [..alternatives], tables, fields,tocEntries, ordinary, effects, effective, compatibility, styleMs, uses, numbering, customXml)
                { CustomXmlResolutionMs=dependencyMs, FieldAnalysisMs=fieldMs,TocEntryAnalysisMs=tocMs }, [..issues]);
        }

        static string Local(InventoryNode n) => n.Name[(n.Name.IndexOf('}') + 1)..];
        static string? DeclaredId(InventoryNode n) => Get(n, "id") is { Length: > 0 } id ? id : null;
        bool Active(InventoryNode n) => elements[n.Id].Coverage != SemanticCoverage.InactiveAlternative;

        SemanticKind Kind(InventoryNode node)
        {
            if (node.Kind == "textValue")
            {
                var owner = index.Parent(node);
                return owner?.Name switch
                {
                    "{" + W + "}t" or "{" + Drawing + "}t" or
                        "{http://schemas.openxmlformats.org/officeDocument/2006/math}t" => SemanticKind.Text,
                    "{" + W + "}delText" => SemanticKind.DeletedText,
                    "{" + W + "}instrText" or "{" + W + "}delInstrText" => SemanticKind.FieldInstruction,
                    _ when string.IsNullOrWhiteSpace(node.Text) || owner?.Name is "{" + Wp + "}posOffset" or
                        "{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}pctWidth" or
                        "{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}pctHeight" => SemanticKind.Metadata,
                    _ => SemanticKind.Opaque
                };
            }
            if (node.Name.StartsWith('#')) return SemanticKind.Metadata;
            return node.Name switch
            {
                "{" + W + "}body" or "{" + W + "}hdr" or "{" + W + "}ftr" or "{" + W + "}footnote"
                    or "{" + W + "}endnote" or "{" + W + "}comment" or "{" + W + "}txbxContent" => SemanticKind.Story,
                "{" + W + "}sectPr" => SemanticKind.Section,
                "{" + W + "}p" => SemanticKind.Paragraph,
                "{" + W + "}r" => SemanticKind.Run,
                "{" + W + "}t" or "{" + W + "}delText" or "{" + W + "}instrText" or "{" + W + "}delInstrText" => SemanticKind.Metadata,
                "{" + W + "}fldChar" => SemanticKind.FieldMarker,
                "{" + W + "}fldSimple" => SemanticKind.SimpleField,
                "{" + W + "}tab" or "{" + W + "}ptab" when index.Parent(node) is {} run && Word(run, "r") => SemanticKind.Tab,
                "{" + W + "}br" or "{" + W + "}cr" => SemanticKind.Break,
                "{" + W + "}softHyphen" => SemanticKind.SoftHyphen,
                "{" + W + "}noBreakHyphen" => SemanticKind.NoBreakHyphen,
                "{" + W + "}sym" => SemanticKind.Symbol,
                "{" + W + "}tbl" => SemanticKind.Table,
                "{" + W + "}tr" => SemanticKind.Row,
                "{" + W + "}tc" => SemanticKind.Cell,
                "{" + W + "}style" or "{" + W + "}pStyle" or "{" + W + "}rStyle" or "{" + W + "}tblStyle" => SemanticKind.Style,
                "{" + W + "}numPr" or "{" + W + "}num" or "{" + W + "}abstractNum" or "{" + W + "}lvl"
                    or "{" + W + "}lvlOverride" => SemanticKind.Numbering,
                "{" + W + "}drawing" or "{" + W + "}pict" or "{" + W + "}object" => SemanticKind.Drawing,
                "{" + Wp + "}inline" => SemanticKind.InlineDrawing,
                "{" + Wp + "}anchor" => SemanticKind.FloatingDrawing,
                "{" + Drawing + "}blip" or "{" + Vml + "}imagedata"
                    or "{http://schemas.microsoft.com/office/drawing/2016/SVG/main}svgBlip" => SemanticKind.ResourceReference,
                "{" + W + "}footnoteReference" or "{" + W + "}endnoteReference" => SemanticKind.NoteCall,
                "{" + W + "}footnoteRef" or "{" + W + "}endnoteRef" => SemanticKind.NoteMark,
                "{" + W + "}separator" => SemanticKind.NoteSeparatorRule,
                "{" + W + "}continuationSeparator" => SemanticKind.NoteContinuationRule,
                "{" + W + "}headerReference" or "{" + W + "}footerReference" => SemanticKind.StoryReference,
                "{" + W + "}bookmarkStart" => SemanticKind.BookmarkStart,
                "{" + W + "}bookmarkEnd" => SemanticKind.BookmarkEnd,
                "{" + W + "}hyperlink" => SemanticKind.Link,
                "{" + W + "}ins" or "{" + W + "}del" or "{" + W + "}moveFrom" or "{" + W + "}moveTo"
                    or "{" + W + "}pPrChange" or "{" + W + "}rPrChange" or "{" + W + "}sectPrChange"
                    or "{" + W + "}tblPrChange" or "{" + W + "}trPrChange" or "{" + W + "}tcPrChange"
                    or "{" + W + "}tblGridChange" or "{" + W + "}cellIns" or "{" + W + "}cellDel" or "{" + W + "}cellMerge"
                    or "{" + W + "}moveFromRangeStart" or "{" + W + "}moveFromRangeEnd"
                    or "{" + W + "}moveToRangeStart" or "{" + W + "}moveToRangeEnd" => SemanticKind.Revision,
                "{" + W + "}commentReference" or "{" + W + "}commentRangeStart" or "{" + W + "}commentRangeEnd" => SemanticKind.CommentReference,
                "{" + W + "}sdt" or "{" + W + "}sdtContent" => SemanticKind.ContentControl,
                "{" + Mc + "}AlternateContent" => SemanticKind.Alternative,
                "{" + Mc + "}Choice" or "{" + Mc + "}Fallback" => SemanticKind.Branch,
                "{" + W + "}pPr" or "{" + W + "}rPr" or "{" + W + "}tcPr" or "{" + W + "}trPr" or "{" + W + "}tblPr"
                    or "{" + W + "}tblGrid" or "{" + W + "}docDefaults" or "{" + W + "}sdtPr" => SemanticKind.Property,
                _ => SemanticKind.Opaque
            };
        }
    }
}
