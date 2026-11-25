using System.Collections.Immutable;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum SemanticKind
{
    Metadata, Story, Section, Paragraph, Run, Text, DeletedText, FieldInstruction, FieldMarker, SimpleField,
    Tab, Break, SoftHyphen, NoBreakHyphen, Symbol, Table, Row, Cell, Style, Numbering,
    Drawing, InlineDrawing, FloatingDrawing, ResourceReference, NoteCall, StoryReference,
    BookmarkStart, BookmarkEnd, Link, Revision, CommentReference, ContentControl,
    Alternative, Branch, Property, Opaque, NoteMark, NoteSeparatorRule, NoteContinuationRule
}

internal enum SemanticCoverage { Represented, Technical, InactiveAlternative, InventoryOnly }
internal enum ReferenceStatus { Resolved, External, Unresolved, Inert }

internal readonly record struct Utf16Span(int Start, int EndExclusive);
internal readonly record struct SemanticSource(string SnapshotId, string NodeId, SourceLocation Location, Utf16Span? Span);
internal readonly record struct SemanticElement(string Id, int Order, string? StoryId, SemanticKind Kind, SemanticCoverage Coverage);
internal sealed record SemanticStory(string RootId, string Kind, string? DeclaredId, string? OwnerId);
internal sealed record SemanticReference(string SourceId, string Kind, string? DeclaredId, Relationship? Relation,
    string? TargetId, MediaResource? Media, ReferenceStatus Status);
internal sealed record SemanticAlternative(string SourceId, string? SelectedBranchId, ImmutableArray<string> BranchIds);
internal sealed record RunningStory(string Kind, string Variant, string ReferenceId, SemanticReference Reference, int DeclaringSection);
internal sealed record SemanticSection(int Number, string? PropertiesId, string? ClosingParagraphId, int StartBlock, int EndBlockExclusive,
    ImmutableArray<RunningStory> RunningStories);
internal sealed record SemanticField(string Id, string StoryId, string? ParentFieldId, string Command,
    string? InstructionAttribute, ImmutableArray<string> InstructionIds, ImmutableArray<string> StoredResultIds,
    string? SeparatorId, string? EndId, bool StructureVerified, bool Locked, bool Dirty, string? BookmarkId);
internal sealed record SemanticCell(string Id, int? Column, int? ColumnSpan, string? VerticalOriginId, string? HorizontalOriginId);
internal sealed record SemanticRow(string Id, ImmutableArray<SemanticCell> Cells);
internal sealed record SemanticTable(string Id, ImmutableArray<string> GridColumns, ImmutableArray<SemanticRow> Rows);
internal sealed record SemanticStyle(string Id, string DeclaredId, string Type, bool IsDefault, string? ParentId)
{
    internal SemanticStyleReference? LinkedStyle {get;init;}
    internal SemanticStyleReference? NextStyle {get;init;}
}
internal sealed record SemanticStyleReference(string DeclarationId,string? TargetId);
internal sealed record SemanticStyleUse(string Id, ImmutableArray<string> BaseToDerivedIds, bool Resolved)
{
    internal bool MissingReferenceIgnored {get;init;}
}
internal sealed record SemanticStyleCollection(ImmutableArray<Relationship> Relations, string? RootId, string? DefaultsId,
    ImmutableArray<SemanticStyle> Definitions);
internal sealed record SemanticStyleCompatibility(Relationship? SettingsRelation, ImmutableArray<string> DeclarationIds, int? Mode);
internal enum StyleCollectionKind { Ordinary, Effects }
internal enum DependencyImpact { NoLayoutEffect, AffectsLayout, Undetermined }
internal sealed record SemanticCustomXml(Relationship Relation, string? RootId, ImmutableArray<Relationship> PartRelations,
    string? PropertiesId, Guid? StoreId, ImmutableArray<string> Entries, DependencyImpact Impact, string Reason, string? ConsumerId=null);
internal sealed record SemanticNumbering(string ParagraphId, string? InstanceId, string? AbstractId, string? LevelId,
    string? OverrideId, string? InstanceSourceId, string? LevelSourceId, int? Level, bool Suppressed);

/// <summary>Semantic projection over one accepted inventory. Consumers treat that snapshot as read-only.</summary>
internal sealed class SemanticDocument(
    InspectionManifest manifest, SemanticIndex index, ImmutableArray<SemanticElement> elements,
    IReadOnlyDictionary<string, SemanticElement> byId,
    ImmutableArray<SemanticStory> stories, ImmutableArray<string> bodyBlocks, ImmutableArray<SemanticSection> sections,
    ImmutableArray<SemanticReference> references, ImmutableArray<SemanticAlternative> alternatives,
    ImmutableArray<SemanticTable> tables, ImmutableArray<SemanticField> fields,ImmutableArray<SemanticTocEntry> tocEntries, SemanticStyleCollection ordinaryStyles,
    SemanticStyleCollection effectsStyles, SemanticStyleCollection effectiveStyles, SemanticStyleCompatibility styleCompatibility, double styleResolutionMs,
    ImmutableArray<SemanticStyleUse> styleUses, ImmutableArray<SemanticNumbering> numbering, ImmutableArray<SemanticCustomXml> customXml)
{
    readonly IReadOnlyDictionary<string, SemanticStyleUse> stylesByUse = styleUses.ToDictionary(s => s.Id, StringComparer.Ordinal);
    readonly ILookup<string, SemanticStyle> defaultStyles = effectiveStyles.Definitions.Where(s => s.IsDefault)
        .ToLookup(s => s.Type, StringComparer.Ordinal);

    internal string SnapshotId { get; } = manifest.SourceSha256!;
    internal string MainPart => index.Main;
    internal ImmutableArray<SemanticElement> Elements { get; } = elements;
    internal ImmutableArray<SemanticStory> Stories { get; } = stories;
    internal ImmutableArray<string> BodyBlocks { get; } = bodyBlocks;
    internal ImmutableArray<SemanticSection> Sections { get; } = sections;
    internal ImmutableArray<SemanticReference> References { get; } = references;
    internal ImmutableArray<SemanticAlternative> Alternatives { get; } = alternatives;
    internal ImmutableArray<SemanticTable> Tables { get; } = tables;
    internal ImmutableArray<SemanticField> Fields { get; } = fields;
    internal ImmutableArray<SemanticTocEntry> TocEntries {get;}=tocEntries;
    internal ImmutableHashSet<string> TocMarkerFields {get;}=tocEntries.SelectMany(e=>e.OperandFieldIds.Prepend(e.FieldId))
        .ToImmutableHashSet(StringComparer.Ordinal);
    internal SemanticStyleCollection OrdinaryStyles { get; } = ordinaryStyles;
    internal SemanticStyleCollection EffectsStyles { get; } = effectsStyles;
    internal SemanticStyleCollection EffectiveStyles { get; } = effectiveStyles;
    internal SemanticStyleCompatibility StyleCompatibility { get; } = styleCompatibility;
    internal double StyleResolutionMs { get; } = styleResolutionMs;
    internal ImmutableArray<SemanticStyle> Styles => EffectiveStyles.Definitions;
    internal ImmutableArray<SemanticStyleUse> StyleUses { get; } = styleUses;
    internal ImmutableArray<SemanticNumbering> Numbering { get; } = numbering;
    internal ImmutableArray<SemanticCustomXml> CustomXml { get; } = customXml;
    internal double CustomXmlResolutionMs { get; init; }
    internal double FieldAnalysisMs { get; init; }
    internal double TocEntryAnalysisMs {get;init;}
    internal IReadOnlyList<Diagnostic> InspectionDiagnostics { get; } = manifest.Diagnostics.AsReadOnly();
    internal IReadOnlyList<PartInventory> Parts { get; } = manifest.Parts.AsReadOnly();

    internal SemanticElement Element(string id) => byId[id];
    internal string? ParentId(string id) => index.Nodes[id].ParentId;
    internal bool IsStandaloneBookmark(string id)=>index.IsStandaloneBookmark(index.Nodes[id]);
    internal bool ProcessesContent(string id)=>index.ProcessesContent(index.Nodes[id]);
    internal IEnumerable<InventoryNode> StylePropertyChildren(string id) => index.LogicalChildren(index.Nodes[id]);
    internal string? StylePropertyParent(string id) => index.PropertyParent(index.Nodes[id])?.Id;
    internal string Name(string id) => index.Nodes[id].Name;
    internal string? NamespaceInScope(InventoryNode node,string prefix) => index.NamespaceInScope(node,prefix);
    internal string Revision(string id) => index.Nodes[id].Revision;
    internal string FieldRole(string id) => index.Nodes[id].FieldRole;
    internal string? RawText(string id) => index.Nodes[id].Text;
    internal string? Attribute(string id, string name, string ns = SemanticIndex.W) =>
        index.Nodes[id].Attributes.GetValueOrDefault(SemanticIndex.QName(ns, name));
    internal string? ChildId(string id, string local) => index.Child(index.Nodes[id], local)?.Id;
    internal string? RelatedPart(string type) => index.Related(MainPart, type)?.ResolvedPart;
    internal string? RelatedRoot(string type, string name) =>
        RelatedPart(type) is {} part ? index.Root(part, name)?.Id : null;

    internal string? ExternalHyperlinkBase()
    {
        const string ns="{http://schemas.openxmlformats.org/officeDocument/2006/extended-properties}";
        if(index.Related("/","extended-properties")?.ResolvedPart is not {} part||
            !Parts.Any(p=>p.PartUri==part&&p.ContentType=="application/vnd.openxmlformats-officedocument.extended-properties+xml")||
            SemanticIndex.Unique(index.ByPart[part].Where(node=>node.ParentId is null&&node.Name==ns+"Properties")) is not {} root||
            SemanticIndex.Unique(index.RawChildren(root).Where(node=>node.Name==ns+"HyperlinkBase")) is not {} basis)
            return null;
        var text=index.RawChildren(basis).Take(2).ToArray();
        return text is [{Kind:"textValue",Text:{Length:>0 and <=4096} value}]?value:null;
    }

    internal IReadOnlyDictionary<string,string> TextDocumentProperties(Action check)
    {
        const string properties="{http://schemas.openxmlformats.org/officeDocument/2006/custom-properties}";
        const string text="{http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes}lpwstr";
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(index.Related("/","custom-properties")?.ResolvedPart is not {} part||
            !Parts.Any(p=>p.PartUri==part&&p.ContentType=="application/vnd.openxmlformats-officedocument.custom-properties+xml")||
            SemanticIndex.Unique(index.ByPart[part].Where(n=>n.ParentId is null&&n.Name==properties+"Properties")) is not {} root)
            return values;
        foreach(var group in index.RawChildren(root).Where(n=>n.Name==properties+"property"&&
            n.Attributes.ContainsKey("{}name")).GroupBy(n=>n.Attributes["{}name"],StringComparer.OrdinalIgnoreCase))
        {
            check();
            if(SemanticIndex.Unique(group) is not {} property||property.Attributes.ContainsKey("{}linkTarget")||
                SemanticIndex.Unique(index.RawChildren(property).Where(n=>!n.Name.StartsWith('#'))) is not {Name:text} entry)
                continue;
            var nodes=index.RawChildren(entry).ToArray();
            if(nodes.All(n=>n.Kind=="textValue")&&string.Concat(nodes.Select(n=>n.Text)) is {Length:>0 and <=1024} value&&
                !value.Any(char.IsControl))
                values.Add(group.Key,value);
        }
        return values;
    }
    internal SemanticStyleUse? StyleUse(string id) => stylesByUse.GetValueOrDefault(id);
    internal SemanticStyle? DefaultStyle(string type) => SemanticIndex.Unique(defaultStyles[type]);
    internal IEnumerable<KeyValuePair<string, string>> Attributes(string id)
    {
        foreach (var attr in index.Nodes[id].Attributes) yield return attr;
    }

    internal SemanticSource Source(string id, Utf16Span? span = null)
    {
        var node = index.Nodes[id];
        if (span is {} s)
        {
            string? text = node.Text;
            if (node.Kind != "textValue" || text is null || s.Start < 0 || s.EndExclusive < s.Start ||
                s.EndExclusive > text.Length || SplitsScalar(text, s.Start) || SplitsScalar(text, s.EndExclusive))
                throw new ArgumentOutOfRangeException(nameof(span));
        }
        return new(SnapshotId, id, node.Source, span);
    }

    static bool SplitsScalar(string text, int offset) => offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]);

    // Raw and projected child access share node identities. Story boundaries remain explicit.
    internal IEnumerable<SemanticElement> Children(string id, bool includeInactive = false)
    {
        var children = includeInactive ? index.RawChildren(index.Nodes[id]) : index.Children(index.Nodes[id]);
        foreach (var node in children)
        {
            var element = byId[node.Id];
            if (includeInactive || element.Coverage != SemanticCoverage.InactiveAlternative) yield return element;
        }
    }

    internal IEnumerable<SemanticElement> BlocksOf(string id) =>
        index.LogicalChildren(index.Nodes[id]).Select(n => byId[n.Id])
            .Where(e => e.Coverage != SemanticCoverage.InactiveAlternative &&
                e.Kind is not (SemanticKind.Property or SemanticKind.Section));

    internal SemanticSource TextSource(string id)
    {
        var node = index.Nodes[id];
        return Source(id, new(0, node.Text?.Length ?? 0));
    }

    // Ordered declaration roots. A paragraph mark takes direct rPr from pPr.
    internal IEnumerable<string> PresentationDeclarations(string id, bool paragraphMark = false,
        Action<string>? unresolved = null, IEnumerable<string>? tableParagraphProperties = null,
        string? numberingParagraphProperties = null,string? paragraphStyleOverride=null)
    {
        var node = index.Nodes[id];
        bool isRun = SemanticIndex.Word(node, "r");
        bool isParagraph = SemanticIndex.Word(node, "p");
        if (!isRun && !isParagraph) yield break;
        bool character = isRun || paragraphMark;
        if (EffectiveStyles.DefaultsId is {} defaults && index.Child(index.Nodes[defaults], character ? "rPrDefault" : "pPrDefault") is {} kind &&
            index.Child(kind, character ? "rPr" : "pPr") is {} properties)
            yield return properties.Id;
        if(paragraphMark&&EffectiveStyles.DefaultsId is {} d&&index.Child(index.Nodes[d],"pPrDefault") is {} paragraphDefault&&
            index.Child(paragraphDefault,"pPr") is {} defaultProperties&&index.Child(defaultProperties,"rPr") is {} defaultMark&&
            BlocksOf(defaultMark.Id).Any(e=>TextStyleResolver.IsVisibility(Name(e.Id))))
            yield return defaultMark.Id;
        if (!character && tableParagraphProperties is not null)
            foreach (string propertiesId in tableParagraphProperties) yield return propertiesId;
        InventoryNode? paragraph = node;
        while (paragraph is not null && !SemanticIndex.Word(paragraph, "p") && byId[paragraph.Id].StoryId == byId[id].StoryId)
            paragraph = index.Parent(paragraph);
        if (paragraph is not null && SemanticIndex.Word(paragraph, "p"))
        {
            var pp = index.Child(paragraph, "pPr");
            foreach (string layer in Layers(pp, "pStyle", "paragraph", character ? "rPr" : "pPr"))
                yield return layer;
            // MS-OI29500 §17.7.2 applies the numbering properties after the paragraph style.
            if (!character && numberingParagraphProperties is not null) yield return numberingParagraphProperties;
            if (isParagraph && pp is not null&&paragraphStyleOverride is null)
            {
                if (!paragraphMark) yield return pp.Id;
                else if (index.Child(pp, "rPr") is {} mark) yield return mark.Id;
            }
        }
        if (isRun)
        {
            var rp = index.Child(node, "rPr");
            foreach (string layer in Layers(rp, "rStyle", "character", "rPr"))
                yield return layer;
            if (rp is not null) yield return rp.Id;
        }

        IEnumerable<string> Layers(InventoryNode? properties, string refName, string type, string layerName)
        {
            var reference = properties is null ? null : index.Child(properties, refName);
            string? use = type=="paragraph"?paragraphStyleOverride??reference?.Id:reference?.Id;
            if (use is null)
                use = DefaultStyle(type)?.Id;
            if (use is null) yield break;
            var resolved = StyleUse(use);
            if (resolved?.Resolved != true) unresolved?.Invoke(use);
            if (resolved is not null)
                foreach (string definition in resolved.BaseToDerivedIds)
                {
                    if (index.Child(index.Nodes[definition], layerName) is {} layer)
                        yield return layer.Id;
                    if(paragraphMark&&type=="paragraph"&&index.Child(index.Nodes[definition],"pPr") is {} p&&
                        index.Child(p,"rPr") is {} inheritedMark&&BlocksOf(inheritedMark.Id).Any(e=>TextStyleResolver.IsVisibility(Name(e.Id))))
                        yield return inheritedMark.Id;
                }
        }
    }

    internal string? StoredText(SemanticField field)
    {
        if (field.StoredResultIds.IsEmpty || field.StoredResultIds.Any(id => Element(id).Kind is
            SemanticKind.Symbol or SemanticKind.Drawing or SemanticKind.Opaque or
            SemanticKind.NoteCall or SemanticKind.NoteMark or SemanticKind.NoteSeparatorRule or SemanticKind.NoteContinuationRule))
            return null;
        return string.Concat(field.StoredResultIds.Select(id => Element(id).Kind switch
        {
            SemanticKind.Tab => "\t",
            SemanticKind.Break => "\n",
            SemanticKind.SoftHyphen => "\u00ad",
            SemanticKind.NoBreakHyphen => "\u2011",
            _ => RawText(id)
        }));
    }
}

internal sealed record SemanticResult(SemanticDocument? Document, ImmutableArray<FlowIssue> Issues);
