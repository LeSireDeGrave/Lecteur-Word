using System.Collections.Immutable;
using System.Text;
using LecteurWord.DocxCore;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal static partial class SemanticReader
{
    sealed partial class Builder
    {
        sealed class FieldFrame(InventoryNode start, string? parent, bool simple)
        {
            internal InventoryNode Start { get; } = start;
            internal string? Parent { get; } = parent;
            internal bool Simple { get; } = simple;
            internal bool InResult => Simple || Separator is not null;
            internal string? Separator { get; set; }
            internal bool Malformed { get; set; }
            internal List<string> Instruction { get; } = [];
            internal List<string> Result { get; } = [];
        }

        ImmutableArray<SemanticField> BuildFields()
        {
            var fields = new List<SemanticField>();
            foreach (var story in stories)
            {
                var root = index.Nodes[story.RootId];
                if (!Active(root)) continue;
                var frames = new List<FieldFrame>();
                Scan(root);
                while (frames.Count > 0) Finish(null);

                void Begin(InventoryNode node, bool simple)
                {
                    if (frames.Count >= 32) throw new SemanticLimitException();
                    frames.Add(new(node, frames.Count == 0 ? null : frames[^1].Start.Id, simple) { Malformed = node.Revision != "unchanged" });
                }

                void Finish(string? end)
                {
                    var frame = frames[^1];
                    frames.RemoveAt(frames.Count - 1);
                    string? attr = frame.Simple ? Get(frame.Start, "instr") : null;
                    string command = "";
                    string? bookmarkId = null;
                    string code = FieldPrefix(attr, frame.Instruction);
                    var prefix = code.AsSpan().TrimStart();
                    int stop = prefix.IndexOfAny(" \t\r\n");
                    if (stop >= 0 || code.Length < 1024) command = (stop < 0 ? prefix : prefix[..stop]).ToString().ToUpperInvariant();
                    if (command is "REF" or "PAGEREF" or "NOTEREF" && stop >= 0)
                    {
                        var arg = prefix[stop..].TrimStart();
                        int limit = !arg.IsEmpty && arg[0] == '"' ? arg[1..].IndexOf('"') : arg.IndexOfAny(" \t\r\n");
                        string name = !arg.IsEmpty && arg[0] == '"' ? limit < 0 ? "" : arg.Slice(1, limit).ToString() :
                            limit < 0 ? code.Length < 1024 ? arg.ToString() : "" : arg[..limit].ToString();
                        bookmarkId = Bookmark(name);
                        if (bookmarkId is null) Issue("SEMANTIC_FIELD_TARGET", frame.Start, "Cible de référence croisée non établie.");
                    }
                    bool verified = !frame.Malformed && (frame.Simple || end is not null) && command.Length > 0;
                    if (!verified) Issue("SEMANTIC_FIELD_STRUCTURE", frame.Start, "Champ irrégulier ou incomplet. Sources et résultat enregistré conservés.");
                    fields.Add(new(frame.Start.Id, story.RootId, frame.Parent, command, attr, [..frame.Instruction], [..frame.Result],
                        frame.Separator, end, verified, On(frame.Start, "fldLock"), On(frame.Start, "dirty"), bookmarkId));
                }

                void Scan(InventoryNode node)
                {
                    Check();
                    var element = elements[node.Id];
                    if (element.StoryId != story.RootId || element.Coverage == SemanticCoverage.InactiveAlternative) return;
                    if (element.Kind == SemanticKind.SimpleField)
                    {
                        Begin(node, true);
                        foreach (var child in index.Children(node)) Scan(child);
                        while (frames[^1].Start.Id != node.Id) Finish(null);
                        Finish(node.Id);
                        return;
                    }
                    if (element.Kind == SemanticKind.FieldMarker)
                    {
                        string type = Get(node, "fldCharType");
                        if (type == "begin") Begin(node, false);
                        else if (frames.Count == 0 || frames[^1].Simple)
                            Issue("SEMANTIC_FIELD_MARKER", node, "Marqueur de champ sans ouverture correspondante.");
                        else if (type == "end")
                        {
                            frames[^1].Malformed |= node.Revision != "unchanged";
                            Finish(node.Id);
                        }
                        else if (type == "separate" && frames[^1].Separator is null) frames[^1].Separator = node.Id;
                        else frames[^1].Malformed = true;
                        return;
                    }
                    if (frames.Count > 0)
                    {
                        var inner = frames[^1];
                        inner.Malformed |= node.Revision != "unchanged";
                        if (element.Kind == SemanticKind.FieldInstruction && !inner.InResult)
                            inner.Instruction.Add(node.Id);
                        if (element.Kind is SemanticKind.Text or SemanticKind.Tab or SemanticKind.Break
                            or SemanticKind.SoftHyphen or SemanticKind.NoBreakHyphen or SemanticKind.Symbol
                            or SemanticKind.Drawing or SemanticKind.NoteCall)
                        {
                            for (int i = frames.Count - 1; i >= 0 && frames[i].InResult; i--)
                            {
                                Check();
                                frames[i].Result.Add(node.Id);
                            }
                        }
                    }
                    if (element.Kind != SemanticKind.Drawing)
                        foreach (var child in index.Children(node)) Scan(child);
                }
            }
            return [..fields.OrderBy(f => elements[f.Id].Order)];
        }

        string FieldPrefix(string? attribute, List<string> instructions)
        {
            const int limit = 1024;
            if (attribute is not null) return attribute[..Math.Min(attribute.Length, limit)];
            var prefix = new StringBuilder();
            foreach (string id in instructions)
            {
                if (prefix.Length == limit) break;
                var text = index.Nodes[id].Text.AsSpan();
                prefix.Append(text[..Math.Min(text.Length, limit - prefix.Length)]);
            }
            return prefix.ToString();
        }

        static bool On(InventoryNode node, string attribute) => Get(node, attribute) is "1" or "true" or "on";
    }
}
