using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

// A shared index over the accepted inventory. Strings, XML attributes and binaries stay in DocxCore.
internal sealed class SemanticIndex
{
    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    internal const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const string StylesEffectsRelation = "http://schemas.microsoft.com/office/2007/relationships/stylesWithEffects";
    internal const string Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    internal const string Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    internal const string Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal const string Vml = "urn:schemas-microsoft-com:vml";
    const string WordPrefix = "{" + W + "}";
    const string Xmlns = "http://www.w3.org/2000/xmlns/";

    readonly ILookup<string, InventoryNode> children;
    readonly ILookup<(string Part, string Id), Relationship> relations;
    readonly ILookup<(string Part, string Type), Relationship> relationsByType;
    readonly ILookup<(string Part, string Name, string Id), InventoryNode> definitions;
    readonly Dictionary<string, (InventoryNode? Branch, bool Valid)> alternatives = new(StringComparer.Ordinal);

    internal SemanticIndex(InspectionManifest manifest, Action check)
    {
        var byId = new Dictionary<string, InventoryNode>(manifest.Nodes.Count, StringComparer.Ordinal);
        var ordered = manifest.Nodes.AsReadOnly();
        foreach (var node in ordered)
        {
            check();
            byId.Add(node.Id, node);
        }
        Nodes = byId;
        Ordered = ordered;
        children = ordered.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!);
        ByPart = ordered.ToLookup(n => n.Source.PartUri);
        relations = manifest.Relationships.ToLookup(r => (r.SourcePart, r.Id));
        relationsByType = manifest.Relationships.ToLookup(r => (r.SourcePart, r.Type));
        definitions = ordered.Where(n => DefinitionAttribute(n) is not null)
            .ToLookup(n => (n.Source.PartUri, n.Name, Get(n, DefinitionAttribute(n)!)));
        Main = Related("/", "officeDocument")?.ResolvedPart ?? "";
        Body = Unique(Root(Main, "document") is {} document ? Children(document).Where(n => Word(n, "body")) : []);
    }

    internal IReadOnlyDictionary<string, InventoryNode> Nodes { get; }
    internal IReadOnlyList<InventoryNode> Ordered { get; }
    internal ILookup<string, InventoryNode> ByPart { get; }
    internal string Main { get; }
    internal InventoryNode? Body { get; }

    internal static string QName(string ns, string local) => "{" + ns + "}" + local;
    internal static bool Word(InventoryNode node, string local) =>
        node.Name.Length == WordPrefix.Length + local.Length &&
        node.Name.StartsWith(WordPrefix, StringComparison.Ordinal) &&
        node.Name.AsSpan(WordPrefix.Length).SequenceEqual(local);
    internal static string Get(InventoryNode node, string local, string ns = W) =>
        node.Attributes.GetValueOrDefault(QName(ns, local)) ?? "";
    internal static T? Unique<T>(IEnumerable<T> values) where T : class
    {
        using var it = values.GetEnumerator();
        if (!it.MoveNext()) return null;
        T first = it.Current;
        return it.MoveNext() ? null : first;
    }
    internal InventoryNode? Parent(InventoryNode node) => node.ParentId is {} id ? Nodes[id] : null;
    internal IEnumerable<InventoryNode> RawChildren(InventoryNode node) => children[node.Id];
    internal InventoryNode? Child(InventoryNode node, string name) => Unique(LogicalChildren(node).Where(n => Word(n, name)));
    internal InventoryNode? Root(string part, string name) =>
        Unique(ByPart[part].Where(n => n.ParentId is null && Word(n, name)));

    internal Relationship? Relation(string part, string id) =>
        id.Length == 0 ? null : Unique(relations[(part, id)]);

    internal IEnumerable<Relationship> RelationshipsOfType(string part, string type) => relationsByType[(part, type)];

    internal Relationship? Related(string part, string type)
    {
        var relation = Unique(relationsByType[(part, R + "/" + type)]);
        return relation is { Mode: "Internal", ResolvedPart: not null } ? relation : null;
    }

    internal InventoryNode? Definition(string? part, string kind, string id) =>
        part is null || id.Length == 0 ? null : Unique(definitions[(part, QName(W, kind), id)]);

    internal bool IsStandaloneBookmark(InventoryNode node)
    {
        bool start=Word(node,"bookmarkStart");
        if(!start&&!Word(node,"bookmarkEnd"))return false;
        if(!int.TryParse(Get(node,"id"),System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,out _)||start&&Get(node,"name").Length==0)
            return false;
        return node.Attributes.Keys.All(key=>key.StartsWith("{"+Xmlns+"}",StringComparison.Ordinal)||
                key=="{"+W+"}id"||start&&key=="{"+W+"}name")&&
            RawChildren(node).All(n=>n.Kind=="textValue"&&string.IsNullOrWhiteSpace(n.Text)||n.Name=="#Comment");
    }

    static string? DefinitionAttribute(InventoryNode node) => node.Name switch
    {
        "{" + W + "}style" => "styleId",
        "{" + W + "}num" => "numId",
        "{" + W + "}abstractNum" => "abstractNumId",
        "{" + W + "}footnote" or "{" + W + "}endnote" or "{" + W + "}comment" => "id",
        _ => null
    };

    internal static bool IsAlternative(InventoryNode node) => node.Name == "{" + Mc + "}AlternateContent";

    internal InventoryNode? PropertyParent(InventoryNode node)
    {
        var parent = Parent(node);
        while (parent is not null && (IsAlternative(parent) || ProcessesContent(parent) ||
            parent.Name is "{" + Mc + "}Choice" or "{" + Mc + "}Fallback"))
            parent = Parent(parent);
        return parent;
    }

    internal IEnumerable<InventoryNode> Children(InventoryNode node) =>
        !IsAlternative(node) ? children[node.Id] : SelectedBranch(node) is {} chosen ? [chosen] : [];

    internal InventoryNode? SelectedBranch(InventoryNode node) => ProjectAlternative(node).Branch;

    internal bool IsEmptyStyleAlternative(InventoryNode node)
    {
        if (ProjectAlternative(node) is not (null, true)) return false;
        return Root(node.Source.PartUri, "styles") is not null;
    }

    (InventoryNode? Branch, bool Valid) ProjectAlternative(InventoryNode node)
    {
        if (alternatives.TryGetValue(node.Id, out var known)) return known;
        InventoryNode? selected = null;
        InventoryNode? fallback = null;
        int choices=0;
        bool valid=true;
        foreach(var branch in RawChildren(node))
        {
            if(branch.Kind=="textValue"&&string.IsNullOrWhiteSpace(branch.Text)||branch.Name=="#Comment")continue;
            if(branch.Name=="{"+Mc+"}Choice"&&fallback is null)
            {
                choices++;
                string requires=Get(branch,"Requires","");
                var prefixes=Prefixes(requires);
                if(prefixes.Length==0||prefixes.Any(prefix=>NamespaceInScope(branch,prefix) is null))valid=false;
                else if(selected is null&&SupportedPrefixes(branch,requires))selected=branch;
            }
            else if(branch.Name=="{"+Mc+"}Fallback"&&fallback is null&&choices>0)fallback=branch;
            else if (!IsIgnorableElement(branch)) valid=false;
        }
        valid &= choices > 0;
        var result = (valid ? selected ?? fallback : null, valid);
        alternatives.Add(node.Id, result);
        return result;
    }

    internal bool IsUnderInactiveBranch(InventoryNode node)
    {
        for (var parent = Parent(node); parent is not null; node = parent, parent = Parent(node))
            if (IsAlternative(parent) && SelectedBranch(parent)?.Id != node.Id) return true;
        return false;
    }

    internal bool UnsupportedMustUnderstand(InventoryNode node)
    {
        string prefixes = Get(node, "MustUnderstand", Mc);
        return prefixes.Length > 0 && !SupportedPrefixes(node, prefixes);
    }

    bool SupportedPrefixes(InventoryNode node, string list)
    {
        // This profile understands WML for semantic extraction. Drawing extensions keep their fallback.
        var prefixes = Prefixes(list);
        return prefixes.Length > 0 && prefixes.All(prefix => UnderstoodNamespace(NamespaceInScope(node, prefix)));
    }

    // Partial drawing and Office-extension readers do not implement their entire namespaces.
    static bool UnderstoodNamespace(string? ns) => ns is W or R or "http://www.w3.org/XML/1998/namespace";

    internal string? NamespaceInScope(InventoryNode node, string prefix)
    {
        if(prefix=="xml")return "http://www.w3.org/XML/1998/namespace";
        string key = QName(Xmlns, prefix);
        for (InventoryNode? current = node; current is not null; current = Parent(current))
            if (current.Attributes.TryGetValue(key, out var value)) return value;
        return null;
    }

    static string[] Prefixes(string values)=>values.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries);

    internal bool ProcessesContent(InventoryNode node)
    {
        int split=node.Name.IndexOf('}');
        if(split<=1)return false;
        string ns=node.Name[1..split];
        if(UnderstoodNamespace(ns)||ns==Mc)return false;
        string local=node.Name[(split+1)..];
        for(InventoryNode? cursor=node;cursor is not null;cursor=Parent(cursor))
        {
            if(!cursor.Attributes.TryGetValue("{"+Mc+"}ProcessContent",out var names))continue;
            foreach(string name in Prefixes(names))
            {
                int colon=name.IndexOf(':');
                if(colon<=0)continue;
                string child=name[(colon+1)..];
                if((child==local||child=="*")&&NamespaceInScope(cursor,name[..colon])==ns&&
                    IgnorableAt(cursor,ns))return true;
            }
        }
        return false;
    }

    bool IgnorableAt(InventoryNode declaration,string ns)
    {
        for(InventoryNode? cursor=declaration;cursor is not null;cursor=Parent(cursor))
            if(cursor.Attributes.TryGetValue("{"+Mc+"}Ignorable",out var prefixes)&&
                Prefixes(prefixes).Any(prefix=>NamespaceInScope(cursor,prefix)==ns))return true;
        return false;
    }

    bool IsIgnorableElement(InventoryNode node)
    {
        int end = node.Name.IndexOf('}');
        if (end <= 1) return false;
        string ns = node.Name[1..end];
        return ns != Mc && !UnderstoodNamespace(ns) && IgnorableAt(node, ns) && !ProcessesContent(node);
    }

    internal IEnumerable<InventoryNode> LogicalChildren(InventoryNode node)
    {
        foreach (var child in Children(node))
        {
            if (Word(child, "sdt"))
            {
                foreach (var content in Children(child).Where(n => Word(n, "sdtContent")))
                    foreach (var descendant in LogicalChildren(content)) yield return descendant;
            }
            else if (IsAlternative(child) || ProcessesContent(child) || child.Name is "{" + Mc + "}Choice" or "{" + Mc + "}Fallback"
                or "{" + W + "}sdtContent" or "{" + W + "}customXml"
                or "{" + W + "}ins" or "{" + W + "}moveTo")
            {
                foreach (var descendant in LogicalChildren(child)) yield return descendant;
            }
            else if (child.Name is not ("{" + W + "}del" or "{" + W + "}moveFrom" or "{" + W + "}customXmlPr") && !child.Name.StartsWith('#'))
                yield return child;
        }
    }
}
