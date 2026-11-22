using System.Collections.Immutable;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum SemanticKind
{
    Metadata, Story, Section, Paragraph, Run, Text, DeletedText, FieldInstruction, FieldMarker, SimpleField,
    Tab, Break, SoftHyphen, NoBreakHyphen, Symbol, Table, Row, Cell, Style, Numbering,
    Drawing, InlineDrawing, FloatingDrawing, ResourceReference, NoteCall, StoryReference,
    BookmarkStart, BookmarkEnd, Link, Revision, CommentReference, ContentControl,
    Alternative, Branch, Property, Opaque
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
internal sealed record SemanticStyle(string Id, string DeclaredId, string Type, bool IsDefault, string? ParentId);
internal sealed record SemanticStyleUse(string Id, ImmutableArray<string> BaseToDerivedIds, bool Resolved);
internal sealed record SemanticStyleCollection(ImmutableArray<Relationship> Relations, string? RootId, string? DefaultsId,
    ImmutableArray<SemanticStyle> Definitions);
internal sealed record SemanticStyleCompatibility(Relationship? SettingsRelation, ImmutableArray<string> DeclarationIds, int? Mode);
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
    ImmutableArray<SemanticTable> tables, ImmutableArray<SemanticField> fields, SemanticStyleCollection ordinaryStyles,
    SemanticStyleCollection effectsStyles, SemanticStyleCompatibility styleCompatibility, double styleResolutionMs,
    ImmutableArray<SemanticStyleUse> styleUses, ImmutableArray<SemanticNumbering> numbering, ImmutableArray<SemanticCustomXml> customXml)
{
    readonly IReadOnlyDictionary<string, SemanticStyleUse> stylesByUse = styleUses.ToDictionary(s => s.Id, StringComparer.Ordinal);
    readonly ILookup<string, SemanticStyle> defaultStyles = ordinaryStyles.Definitions.Where(s => s.IsDefault)
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
    internal SemanticStyleCollection OrdinaryStyles { get; } = ordinaryStyles;
    internal SemanticStyleCollection EffectsStyles { get; } = effectsStyles;
    internal SemanticStyleCompatibility StyleCompatibility { get; } = styleCompatibility;
    internal double StyleResolutionMs { get; } = styleResolutionMs;
    internal ImmutableArray<SemanticStyle> Styles => OrdinaryStyles.Definitions;
    internal ImmutableArray<SemanticStyleUse> StyleUses { get; } = styleUses;
    internal ImmutableArray<SemanticNumbering> Numbering { get; } = numbering;
    internal ImmutableArray<SemanticCustomXml> CustomXml { get; } = customXml;
    internal double CustomXmlResolutionMs { get; init; }
    internal IReadOnlyList<Diagnostic> InspectionDiagnostics { get; } = manifest.Diagnostics.AsReadOnly();
    internal IReadOnlyList<PartInventory> Parts { get; } = manifest.Parts.AsReadOnly();

    internal SemanticElement Element(string id) => byId[id];
    internal string? ParentId(string id) => index.Nodes[id].ParentId;
    internal string Name(string id) => index.Nodes[id].Name;
    internal string Revision(string id) => index.Nodes[id].Revision;
    internal string FieldRole(string id) => index.Nodes[id].FieldRole;
    internal string? RawText(string id) => index.Nodes[id].Text;
    internal string? Attribute(string id, string name, string ns = SemanticIndex.W) =>
        index.Nodes[id].Attributes.GetValueOrDefault(SemanticIndex.QName(ns, name));
    internal string? ChildId(string id, string local) => index.Child(index.Nodes[id], local)?.Id;
    internal string? RelatedPart(string type) => index.Related(MainPart, type)?.ResolvedPart;
    internal string? RelatedRoot(string type, string name) =>
        RelatedPart(type) is {} part ? index.Root(part, name)?.Id : null;
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

    // Ordered declaration roots for the first style profile. The mark uses the paragraph's
    // direct rPr separately. These layers are evidence for a future property-specific cascade.
    internal IEnumerable<string> PresentationDeclarations(string id, bool paragraphMark = false,
        Action<string>? unresolved = null, IEnumerable<string>? tableParagraphProperties = null,
        string? numberingParagraphProperties = null)
    {
        var node = index.Nodes[id];
        bool isRun = SemanticIndex.Word(node, "r");
        bool isParagraph = SemanticIndex.Word(node, "p");
        if (!isRun && !isParagraph) yield break;
        bool character = isRun || paragraphMark;
        if (OrdinaryStyles.DefaultsId is {} defaults && index.Child(index.Nodes[defaults], character ? "rPrDefault" : "pPrDefault") is {} kind &&
            index.Child(kind, character ? "rPr" : "pPr") is {} properties)
            yield return properties.Id;
        if (!character && tableParagraphProperties is not null)
            foreach (string propertiesId in tableParagraphProperties) yield return propertiesId;
        if (!character && numberingParagraphProperties is not null) yield return numberingParagraphProperties;

        InventoryNode? paragraph = node;
        while (paragraph is not null && !SemanticIndex.Word(paragraph, "p") && byId[paragraph.Id].StoryId == byId[id].StoryId)
            paragraph = index.Parent(paragraph);
        if (paragraph is not null && SemanticIndex.Word(paragraph, "p"))
        {
            var pp = index.Child(paragraph, "pPr");
            foreach (string layer in Layers(pp, "pStyle", "paragraph", character ? "rPr" : "pPr"))
                yield return layer;
            if (isParagraph && pp is not null)
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
            string? use = reference?.Id;
            if (use is null)
                use = DefaultStyle(type)?.Id;
            if (use is null) yield break;
            var resolved = StyleUse(use);
            if (resolved?.Resolved != true) unresolved?.Invoke(use);
            if (resolved is not null)
                foreach (string definition in resolved.BaseToDerivedIds)
                    if (index.Child(index.Nodes[definition], layerName) is {} layer)
                        yield return layer.Id;
        }
    }

    internal string? StoredText(SemanticField field)
    {
        if (field.StoredResultIds.IsEmpty || field.StoredResultIds.Any(id => Element(id).Kind is
            SemanticKind.Symbol or SemanticKind.Drawing or SemanticKind.Opaque or SemanticKind.NoteCall))
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
