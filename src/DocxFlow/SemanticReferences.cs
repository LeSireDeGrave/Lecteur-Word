using System.Collections.Immutable;
using LecteurWord.DocxCore;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal static partial class SemanticReader
{
    sealed partial class Builder
    {
        Dictionary<string, MediaResource>? mediaByPart;
        ILookup<string, SemanticElement>? namedBookmarks;

        SemanticReference RelatedReference(InventoryNode node, string kind, string id, string? type = null)
        {
            var relation = index.Relation(node.Source.PartUri, id);
            if (relation is null || type is not null && relation.Type != R + "/" + type)
            {
                Issue("SEMANTIC_RELATION_UNRESOLVED", node, "Identifiant de relation absent, ambigu ou de type incompatible.");
                return new(node.Id, kind, id, relation, null, null, ReferenceStatus.Unresolved);
            }
            if (relation.Mode == "External")
                return new(node.Id, kind, id, relation, null, null, ReferenceStatus.External);
            if (relation.ResolvedPart is null)
            {
                Issue("SEMANTIC_RELATION_UNRESOLVED", node, "Cible interne de la relation indisponible.");
                return new(node.Id, kind, id, relation, null, null, ReferenceStatus.Unresolved);
            }
            mediaByPart ??= manifest.Media.ToDictionary(m => m.PartUri, StringComparer.Ordinal);
            var root = Unique(index.ByPart[relation.ResolvedPart].Where(n => n.ParentId is null && !n.Name.StartsWith('#')));
            var media = mediaByPart.GetValueOrDefault(relation.ResolvedPart);
            if (kind == "image" && media is null)
            {
                Issue("SEMANTIC_IMAGE_UNRESOLVED", node, "Relation conservée sans média d’image inventorié.");
                return new(node.Id, kind, id, relation, root?.Id, null, ReferenceStatus.Unresolved);
            }
            return new(node.Id, kind, id, relation, root?.Id, media,
                kind == "package" ? ReferenceStatus.Inert : ReferenceStatus.Resolved);
        }

        SemanticReference StoryReference(InventoryNode node)
        {
            string kind = Word(node, "headerReference") ? "header" : "footer";
            var reference = RelatedReference(node, kind, Get(node, "id", R), kind);
            var root = reference.Relation?.ResolvedPart is {} part ? index.Root(part, kind == "header" ? "hdr" : "ftr") : null;
            if (reference.Status == ReferenceStatus.Resolved && root is not null)
                return reference with { TargetId = root.Id };
            Issue("SEMANTIC_STORY_UNRESOLVED", node, "Histoire d’en-tête ou de pied non rattachée.");
            return reference with { TargetId = null, Status = ReferenceStatus.Unresolved };
        }

        SemanticReference NoteOrComment(InventoryNode node, string kind)
        {
            string id = Get(node, "id");
            var relation = index.Related(node.Source.PartUri, kind + "s");
            var definition = index.Definition(relation?.ResolvedPart, kind, id);
            if (definition is null || !Active(definition) || index.Parent(definition) is not {} parent ||
                !Word(parent, kind + "s") || kind != "comment" && Get(definition, "type") is not ("" or "normal"))
            {
                Issue("SEMANTIC_STORY_UNRESOLVED", node, "Corps référencé absent, ambigu ou hors profil.");
                return new(node.Id, kind, id, relation, null, null, ReferenceStatus.Unresolved);
            }
            return new(node.Id, kind, id, relation, definition.Id, null, ReferenceStatus.Resolved);
        }

        string? Bookmark(string name)
        {
            if (name.Length == 0) return null;
            namedBookmarks ??= OrderedElements.Where(e => e.Kind == SemanticKind.BookmarkStart && e.StoryId is not null &&
                e.Coverage == SemanticCoverage.Represented)
                .ToLookup(e => Get(index.Nodes[e.Id], "name"), StringComparer.Ordinal);
            var candidates = namedBookmarks[name];
            using var iterator = candidates.GetEnumerator();
            if (!iterator.MoveNext()) return null;
            string id = iterator.Current.Id;
            return iterator.MoveNext() ? null : id;
        }

        ImmutableArray<SemanticReference> BuildReferences()
        {
            var references = ImmutableArray.CreateBuilder<SemanticReference>();
            var bookmarkStarts = OrderedElements.Where(e => e.Kind == SemanticKind.BookmarkStart &&
                e.Coverage == SemanticCoverage.Represented && e.StoryId is not null)
                .Select(e => index.Nodes[e.Id])
                .ToLookup(n => (elements[n.Id].StoryId, Get(n, "id")));

            foreach (var element in OrderedElements)
            {
                Check();
                if (element.Coverage == SemanticCoverage.InactiveAlternative) continue;
                var node = index.Nodes[element.Id];
                switch (element.Kind)
                {
                    case SemanticKind.NoteCall:
                        references.Add(NoteOrComment(node, Word(node, "footnoteReference") ? "footnote" : "endnote"));
                        break;
                    case SemanticKind.CommentReference:
                        references.Add(NoteOrComment(node, "comment"));
                        break;
                    case SemanticKind.StoryReference:
                        // Section construction resolves each referenced relationship once.
                        if (runningReferences.TryGetValue(node.Id, out var running)) references.Add(running);
                        else references.Add(StoryReference(node));
                        break;
                    case SemanticKind.Link:
                        string linkId = Get(node, "id", R);
                        if (linkId.Length > 0)
                            references.Add(RelatedReference(node, "hyperlink", linkId, "hyperlink"));
                        else if (Get(node, "anchor") is { Length: > 0 } anchor)
                        {
                            string? target = Bookmark(anchor);
                            if (target is null) Issue("SEMANTIC_BOOKMARK_UNRESOLVED", node, "Signet interne absent ou ambigu.");
                            references.Add(new(node.Id, "bookmark", anchor, null, target, null,
                                target is null ? ReferenceStatus.Unresolved : ReferenceStatus.Resolved));
                        }
                        break;
                    case SemanticKind.BookmarkEnd:
                        string id = Get(node, "id");
                        var start = id.Length > 0 ? Unique(bookmarkStarts[(element.StoryId, id)]) : null;
                        if (start is not null && elements[start.Id].Order >= element.Order) start = null;
                        if (start is null) Issue("SEMANTIC_BOOKMARK_UNRESOLVED", node, "Début de signet absent ou ambigu dans cette histoire.");
                        references.Add(new(node.Id, "bookmarkEnd", DeclaredId(node), null, start?.Id, null,
                            start is null ? ReferenceStatus.Unresolved : ReferenceStatus.Resolved));
                        break;
                    case SemanticKind.ResourceReference:
                        foreach (string key in new[] { "embed", "link", "id" })
                            if (Get(node, key, R) is { Length: > 0 } resource)
                                references.Add(RelatedReference(node, "image", resource, "image"));
                        break;
                    default:
                        foreach (var attribute in node.Attributes)
                            if (attribute.Key is "{" + R + "}id" or "{" + R + "}embed" or "{" + R + "}link")
                                references.Add(RelatedReference(node, "package", attribute.Value));
                        break;
                }
            }
            return references.ToImmutable();
        }

        readonly Dictionary<string, SemanticReference> runningReferences = new(StringComparer.Ordinal);

        ImmutableArray<SemanticSection> BuildSections(InventoryNode body, InventoryNode[] blocks)
        {
            var sections = ImmutableArray.CreateBuilder<SemanticSection>();
            var running = new Dictionary<(string Kind, string Variant), RunningStory>();
            int start = 0;
            void Finish(InventoryNode? properties, string? paragraphId, int end)
            {
                int number = sections.Count + 1;
                if (properties is not null)
                {
                    var explicitReferences = index.LogicalChildren(properties).Where(n => Word(n, "headerReference") || Word(n, "footerReference"))
                        .GroupBy(n => (Kind: Word(n, "headerReference") ? "header" : "footer", Variant: Get(n, "type")));
                    foreach (var group in explicitReferences)
                    {
                        Check();
                        var reference = Unique(group);
                        if (reference is null || group.Key.Variant is not ("default" or "first" or "even"))
                        {
                            foreach (var extra in group)
                            {
                                Issue("SEMANTIC_SECTION_REFERENCE", extra, "Variantes d’en-tête ou de pied ambiguës ou inconnues.");
                                runningReferences[extra.Id] = StoryReference(extra) with { TargetId = null, Status = ReferenceStatus.Unresolved };
                            }
                            if (group.Key.Variant is "default" or "first" or "even")
                            {
                                var first = group.First();
                                running[group.Key] = new(group.Key.Kind, group.Key.Variant, first.Id, runningReferences[first.Id], number);
                            }
                            continue;
                        }
                        var target = StoryReference(reference);
                        runningReferences[reference.Id] = target;
                        running[group.Key] = new(group.Key.Kind, group.Key.Variant, reference.Id, target, number);
                    }
                }
                sections.Add(new(number, properties?.Id, paragraphId, start, end, [..running.Values]));
                start = end;
            }

            for (int i = 0; i < blocks.Length; i++)
            {
                Check();
                var block = blocks[i];
                if (Word(block, "p") && index.Child(block, "pPr") is {} pp && index.Child(pp, "sectPr") is {} end)
                    Finish(end, block.Id, i + 1);
            }
            var final = index.Child(body, "sectPr");
            if (final is not null || start < blocks.Length || sections.Count == 0)
                Finish(final, null, blocks.Length);
            return sections.ToImmutable();
        }
    }
}
