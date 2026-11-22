using System.Collections.Immutable;
using System.Globalization;
using LecteurWord.DocxCore;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal static partial class SemanticReader
{
    sealed partial class Builder
    {
        ImmutableArray<SemanticTable> BuildTables()
        {
            var tables = ImmutableArray.CreateBuilder<SemanticTable>();
            foreach (var element in OrderedElements)
            {
                Check();
                if (element.Kind != SemanticKind.Table || element.StoryId is null ||
                    element.Coverage == SemanticCoverage.InactiveAlternative) continue;
                var table = index.Nodes[element.Id];
                var grid = index.Child(table, "tblGrid");
                ImmutableArray<string> columns = grid is null ? [] : [..index.Children(grid)
                    .Where(n => Word(n, "gridCol")).Select(n => n.Id)];
                var rows = ImmutableArray.CreateBuilder<SemanticRow>();
                var previous = new Dictionary<(int Column, int Span), string>();
                foreach (var row in index.LogicalChildren(table).Where(n => Word(n, "tr")))
                {
                    Check();
                    var props = index.Child(row, "trPr");
                    int? column = Number(props is null ? null : index.Child(props, "gridBefore"), 0, 0);
                    var current = new Dictionary<(int, int), string>();
                    var cells = ImmutableArray.CreateBuilder<SemanticCell>();
                    string? horizontal = null;
                    foreach (var cell in index.LogicalChildren(row).Where(n => Word(n, "tc")))
                    {
                        Check();
                        var cellProps = index.Child(cell, "tcPr");
                        int? span = Number(cellProps is null ? null : index.Child(cellProps, "gridSpan"), 1, 1);
                        var vMerge = cellProps is null ? null : index.Child(cellProps, "vMerge");
                        var hMerge = cellProps is null ? null : index.Child(cellProps, "hMerge");
                        string? vOrigin = null;
                        if (column is {} c && span is {} s && c <= int.MaxValue - s)
                        {
                            if (vMerge is not null)
                            {
                                var key = (c, s);
                                vOrigin = Get(vMerge, "val") switch
                                {
                                    "restart" => cell.Id,
                                    "" or "continue" => previous.GetValueOrDefault(key),
                                    _ => null
                                };
                                if (vOrigin is not null) current.Add(key, vOrigin);
                            }
                        }
                        else
                        {
                            column = null;
                            Issue("SEMANTIC_TABLE_GRID", cell, "Colonne indéterminée après une valeur de grille invalide.");
                        }
                        if (vMerge is not null && vOrigin is null)
                            Issue("SEMANTIC_TABLE_MERGE", vMerge, "Origine de fusion verticale non établie dans la rangée précédente.");
                        horizontal = hMerge is null ? null : Get(hMerge, "val") switch
                        {
                            "restart" when column is not null => cell.Id,
                            "" or "continue" when column is not null => horizontal,
                            _ => null
                        };
                        if (hMerge is not null && horizontal is null)
                            Issue("SEMANTIC_TABLE_MERGE", hMerge, "Origine de fusion horizontale non établie.");
                        cells.Add(new(cell.Id, column, span, vOrigin, horizontal));
                        if (column is not null && span is not null) column += span;
                    }
                    rows.Add(new(row.Id, cells.ToImmutable()));
                    previous = current;
                }
                tables.Add(new(table.Id, columns, rows.ToImmutable()));
            }
            return tables.ToImmutable();
        }

        static int? Number(InventoryNode? declaration, int ifAbsent, int minimum) =>
            declaration is null ? ifAbsent :
            int.TryParse(Get(declaration, "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= minimum
                ? value : null;

        SemanticStyleCollection ReadStyleCollection(string relationType, string contentType)
        {
            ImmutableArray<Relationship> relations = [..index.RelationshipsOfType(index.Main, relationType)];
            if (relations.Length != 1 || relations[0] is not { Mode: "Internal", ResolvedPart: {} part } ||
                !manifest.Parts.Any(p => p.PartUri == part && p.ContentType == contentType) ||
                index.Root(part, "styles") is not {} root)
                return new(relations, null, null, []);

            var definitions = ImmutableArray.CreateBuilder<SemanticStyle>();
            foreach (var node in index.RawChildren(root))
            {
                Check();
                if (!Word(node, "style") || !Active(node)) continue;
                var basedOn = index.Child(node, "basedOn");
                definitions.Add(new(node.Id, Get(node, "styleId"), Get(node, "type"), On(node, "default"),
                    basedOn is not null && index.Definition(part, "style", Get(basedOn, "val")) is {} parent &&
                    parent.ParentId == root.Id ? parent.Id : null));
            }
            return new(relations, root.Id, index.Child(root, "docDefaults")?.Id, definitions.ToImmutable());
        }

        SemanticStyleCompatibility ReadStyleCompatibility()
        {
            var links = index.RelationshipsOfType(index.Main, R + "/settings");
            var relation = Unique(links);
            if (relation is not { Mode: "Internal", ResolvedPart: {} part } ||
                !manifest.Parts.Any(p => p.PartUri == part && p.ContentType ==
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml") ||
                index.Root(part, "settings") is not {} root)
                return new(relation, [], links.Any() ? null : 12);
            var declarations = ImmutableArray.CreateBuilder<string>();
            foreach (var compat in index.LogicalChildren(root).Where(n => Word(n, "compat")))
            foreach (var declaration in index.LogicalChildren(compat))
            {
                Check();
                if (Word(declaration, "compatSetting") && Get(declaration, "name") == "compatibilityMode" &&
                    Get(declaration, "uri") == "http://schemas.microsoft.com/office/word")
                    declarations.Add(declaration.Id);
            }
            int? mode = declarations.Count switch
            {
                0 => 12,
                1 when int.TryParse(Get(index.Nodes[declarations[0]], "val"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int value) && value is 11 or 12 or 14 or 15 => value,
                _ => null
            };
            return new(relation, declarations.ToImmutable(), mode);
        }

        (ImmutableArray<SemanticStyleUse>, ImmutableArray<SemanticNumbering>) BuildStyles(
            SemanticStyleCollection ordinary, SemanticStyleCollection effects)
        {
            string? numberingPart = index.Related(index.Main, "numbering")?.ResolvedPart;
            var uses = ImmutableArray.CreateBuilder<SemanticStyleUse>();
            var numbering = ImmutableArray.CreateBuilder<SemanticNumbering>();
            // Reuse the recorded style references and validate each chain in the related styles part.
            var byReference = new Dictionary<string, SemanticStyleUse>(StringComparer.Ordinal);
            var chains = new Dictionary<(string? Root, string Id, string Type), (ImmutableArray<string> Sources, bool Valid)>();
            foreach (var collection in new[] { ordinary, effects })
            foreach (var definition in collection.Definitions)
                Resolve(index.Nodes[definition.Id], definition.DeclaredId, definition.Type, collection);
            foreach (var element in OrderedElements)
            {
                Check();
                if (element.Kind != SemanticKind.Style || element.Coverage == SemanticCoverage.InactiveAlternative) continue;
                var node = index.Nodes[element.Id];
                if (Word(node, "style")) continue;
                string type = Word(node, "pStyle") ? "paragraph" : Word(node, "rStyle") ? "character" : "table";
                Resolve(node, Get(node, "val"), type, ordinary);
            }
            var defaults = Unique(ordinary.Definitions.Where(s => s.IsDefault && s.Type == "paragraph"));
            foreach (var element in OrderedElements)
            {
                Check();
                if (element.Kind != SemanticKind.Paragraph || element.StoryId is null ||
                    element.Coverage == SemanticCoverage.InactiveAlternative) continue;
                var p = index.Nodes[element.Id];
                var pp = index.Child(p, "pPr");
                var style = pp is null ? null : index.Child(pp, "pStyle");
                string? num = null;
                string? level = null;
                bool inheritedNumbering = false;
                var styleChain = byReference.GetValueOrDefault(style?.Id ?? defaults?.Id ?? "")?.BaseToDerivedIds ?? [];
                if (style is not null || defaults is not null)
                {
                    foreach (string id in styleChain)
                    {
                        Check();
                        Apply(index.Child(index.Nodes[id], "pPr"), inherited: true);
                    }
                }
                Apply(pp, inherited: false);
                if (num is null && level is null) continue;
                string requested = num is null ? "" : Get(index.Nodes[num], "val");
                int? levelNumber = Number(level is null ? null : index.Nodes[level], 0, 0);
                var instance = index.Definition(numberingPart, "num", requested);
                var abstractRef = instance is null ? null : index.Child(instance, "abstractNumId");
                var definition = abstractRef is null ? null : index.Definition(numberingPart, "abstractNum", Get(abstractRef, "val"));
                if (inheritedNumbering && level is null)
                {
                    for (int i = styleChain.Length - 1; definition is not null && i >= 0; i--)
                    {
                        Check();
                        string name = Get(index.Nodes[styleChain[i]], "styleId");
                        var matches = index.Children(definition).Where(n => Word(n, "lvl") &&
                            index.Child(n, "pStyle") is {} binding && Get(binding, "val") == name).Take(2).ToArray();
                        if (matches.Length == 0) continue;
                        levelNumber = matches.Length == 1 && int.TryParse(Get(matches[0],"ilvl"),
                            NumberStyles.Integer,CultureInfo.InvariantCulture,out int linked) && linked is >=0 and <=8 ? linked : null;
                        if (matches.Length == 1) level = index.Child(matches[0], "pStyle")!.Id;
                        break;
                    }
                }
                var mapped = definition is null ? null : FindLevel(definition, "lvl");
                var over = instance is null ? null : FindLevel(instance, "lvlOverride");
                if (over is not null && index.Child(over, "lvl") is {} levelOverride) mapped = levelOverride;
                bool suppress = requested == "0";
                if (!suppress && (instance is null || mapped is null))
                    Issue("SEMANTIC_NUMBERING_UNRESOLVED", p, "Numérotation ou niveau non rattaché. Déclarations et sources conservées.");
                numbering.Add(new(p.Id, instance?.Id, definition?.Id, mapped?.Id, over?.Id, num, level, levelNumber, suppress));

                InventoryNode? FindLevel(InventoryNode container, string kind) => levelNumber is not (>= 0 and <= 8)
                    ? null : Unique(index.Children(container).Where(n => Word(n, kind) &&
                        int.TryParse(Get(n, "ilvl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i == levelNumber));

                void Apply(InventoryNode? properties, bool inherited)
                {
                    if (properties is null || index.Child(properties, "numPr") is not {} n) return;
                    if (index.Child(n, "numId") is {} id)
                    {
                        num = id.Id;
                        inheritedNumbering = inherited;
                    }
                    if (!inherited) level = index.Child(n, "ilvl")?.Id ?? level;
                }
            }
            return (uses.ToImmutable(), numbering.ToImmutable());

            void Resolve(InventoryNode origin, string name, string type, SemanticStyleCollection collection)
            {
                Check();
                string? root = collection.RootId;
                string? part = root is null ? null : index.Nodes[root].Source.PartUri;
                if (!chains.TryGetValue((root, name, type), out var result))
                {
                    var path = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    bool valid = name.Length > 0 && type is ("paragraph" or "character" or "table" or "numbering");
                    string current = name;
                    while (current.Length > 0)
                    {
                        Check();
                        if (path.Count >= 32 || !seen.Add(current) || index.Definition(part, "style", current) is not {} definition ||
                            !Active(definition) || Get(definition, "type") != type || definition.ParentId != root)
                        {
                            valid = false;
                            break;
                        }
                        path.Add(definition.Id);
                        var bases = index.LogicalChildren(definition).Where(n => Word(n, "basedOn")).Take(2).ToArray();
                        current = bases.Length == 1 ? Get(bases[0], "val") : "";
                        if (bases.Length > 1 || bases.Length == 1 && current.Length == 0) valid = false;
                    }
                    path.Reverse();
                    result = ([..path], valid);
                    chains.Add((root, name, type), result);
                }
                var use = new SemanticStyleUse(origin.Id, result.Sources, result.Valid);
                uses.Add(use);
                byReference.Add(origin.Id, use);
                if (!result.Valid && collection == ordinary)
                    Issue("SEMANTIC_STYLE_UNRESOLVED", origin, "Chaîne de styles absente, ambiguë, incompatible ou cyclique.");
            }
        }
    }
}
