using System.Diagnostics;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PageStyleSelection { Ordinary, Equivalent, Unresolved }
internal sealed record PageStyleDecision(PageStyleSelection Selection, SemanticSource? SelectedRoot, double ComparisonMs,
    DependencyImpact? EffectsImpact);

public static partial class LayoutReader
{
    internal const string StylesEffectsRelation=SemanticIndex.StylesEffectsRelation;

    sealed partial class Reader
    {
        internal PageStyleDecision? StyleDecision {get;private set;}
        const string WordValue="{"+W+"}val";
        const string WordDefault="{"+W+"}default";
        const string MarkupCompatibility="{"+SemanticIndex.Mc+"}";
        const string Ignorable=MarkupCompatibility+"Ignorable";
        const string Xmlns="{http://www.w3.org/2000/xmlns/}";

        void InspectStyles(InventoryNode body)
        {
            var model=semantic!;
            var ordinary=model.OrdinaryStyles;
            var effects=model.EffectsStyles;
            var selected=ordinary.RootId is {} id?model.Source(id):(SemanticSource?)null;
            long start=Stopwatch.GetTimestamp();
            var choice=Compare();
            StyleDecision=new(choice,selected,Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                effects.Relations.IsEmpty?null:choice==PageStyleSelection.Equivalent?DependencyImpact.NoLayoutEffect:DependencyImpact.Undetermined);

            PageStyleSelection Compare()
            {
                if(ordinary.Relations.Length>0&&ordinary.RootId is null)
                {
                    Issue("PAGE_STYLES_ORDINARY_INVALID",null,"Relation interne, type de contenu ou racine de la collection ordinaire non établis.");
                    return PageStyleSelection.Unresolved;
                }
                if(ordinary.RootId is {} root&&InvalidCollection(ordinary))
                {
                    Issue("PAGE_STYLES_ORDINARY_INVALID",nodes[root],"Identifiants ou déclarations par défaut invalides dans la collection ordinaire.");
                    return PageStyleSelection.Unresolved;
                }
                if(effects.Relations.Length==0)return PageStyleSelection.Ordinary;
                if(ordinary.RootId is not {} firstRoot||effects.RootId is not {} secondRoot||firstRoot==secondRoot||InvalidCollection(effects))
                {
                    Issue("PAGE_STYLES_EFFECTS_INVALID",body,"Relation, type de contenu, racine ou identifiants des styles auxiliaires invalides.");
                    return PageStyleSelection.Unresolved;
                }

                var first=nodes[firstRoot];
                var second=nodes[secondRoot];
                if(input.Manifest.Relationships.Any(r=>r.SourcePart==first.Source.PartUri||r.SourcePart==second.Source.PartUri))
                {
                    Issue("PAGE_STYLES_EFFECTS_DEPENDENCY",second,"Les dépendances propres aux collections demandent une qualification de leur portée.");
                    return PageStyleSelection.Unresolved;
                }
                bool equivalent=true;
                if(model.StyleCompatibility.Mode is null)
                {
                    equivalent=false;
                    var declaration=model.StyleCompatibility.DeclarationIds.FirstOrDefault();
                    Issue("PAGE_STYLES_COMPATIBILITY_UNRESOLVED",declaration is null?second:nodes[declaration],
                        "Le profil de compatibilité enregistré est ambigu ou hors profil.");
                }

                if(!SameStyleContext(first,second)||!SameChildren(first,second,OtherThanStyles)||
                    !DefaultStyles(ordinary).SequenceEqual(DefaultStyles(effects)))
                {
                    equivalent=false;
                    Issue("PAGE_STYLES_EFFECTS_DEFAULTS",second,"Valeurs documentaires, contexte de compatibilité XML ou styles par défaut divergents.");
                }
                var used=new HashSet<string>(StringComparer.Ordinal);
                foreach(var definition in ordinary.Definitions.Where(d=>d.IsDefault))Use(model.StyleUse(definition.Id)!);
                foreach(var use in model.StyleUses)
                {
                    token.ThrowIfCancellationRequested();
                    if(model.Element(use.Id) is {StoryId:not null,Coverage:not SemanticCoverage.InactiveAlternative})Use(use);
                }

                var byName=effects.Definitions.ToDictionary(d=>d.DeclaredId,StringComparer.Ordinal);
                foreach(var id in used.Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var style=nodes[id];
                    string name=A(style,"styleId");
                    if(!byName.TryGetValue(name,out var alternate))
                    {
                        equivalent=false;
                        Issue("PAGE_STYLES_EFFECTS_MISSING_STYLE",style,"Style utilisé absent de la partie auxiliaire : "+name+".");
                        continue;
                    }
                    var other=nodes[alternate.Id];
                    if(model.StyleUse(alternate.Id)?.Resolved!=true)
                    {
                        equivalent=false;
                        Issue("PAGE_STYLES_EFFECTS_USE_UNRESOLVED",other,"La chaîne auxiliaire du style "+name+" n’a pas pu être démontrée.");
                    }
                    var difference=FirstDifference(style,other,SignificantStyleChild);
                    if(difference is not null)
                    {
                        equivalent=false;
                        Issue("PAGE_STYLES_EFFECTS_DIVERGENT",other,"Propriétés actives divergentes du style "+name+
                            " ("+difference+"). Les styles ordinaires sont utilisés pour les pages candidates.");
                    }
                }

                if(!equivalent)return PageStyleSelection.Unresolved;
                Issue("PAGE_STYLES_EFFECTS_EQUIVALENT",second,
                    "Déclarations de rendu et sélecteurs examinés équivalents. Styles ordinaires sélectionnés.",false);
                return PageStyleSelection.Equivalent;

                void Use(SemanticStyleUse use)
                {
                    used.UnionWith(use.BaseToDerivedIds);
                    if(use.Resolved)return;
                    equivalent=false;
                    Issue("PAGE_STYLES_EFFECTS_USE_UNRESOLVED",nodes[use.Id],"La chaîne ordinaire du style actif n’a pas pu être démontrée.");
                }
            }
        }

        bool InvalidCollection(SemanticStyleCollection collection) =>
            collection.Definitions.GroupBy(d=>d.DeclaredId,StringComparer.Ordinal).Any(g=>g.Key.Length==0||g.Count()>1)||
            collection.Definitions.Where(d=>d.IsDefault).GroupBy(d=>d.Type,StringComparer.Ordinal).Any(g=>g.Count()>1)||
            collection.Definitions.Any(d=>nodes[d.Id].Attributes.TryGetValue(WordDefault,out var value)&&ReadOnOff(value,false) is null)||
            collection.RootId is {} id&&children[id].Where(n=>Is(n,"docDefaults")).Skip(1).Any();

        static IEnumerable<(string Type,string Id)> DefaultStyles(SemanticStyleCollection collection) =>
            collection.Definitions.Where(d=>d.IsDefault).Select(d=>(d.Type,d.DeclaredId)).Order();

        bool OtherThanStyles(InventoryNode n)=>!Is(n,"style")&&!Is(n,"latentStyles");

        bool SignificantStyleChild(InventoryNode n) => !(Is(n,"name")||Is(n,"aliases")||Is(n,"uiPriority")||Is(n,"qFormat")||
            Is(n,"semiHidden")||Is(n,"unhideWhenUsed")||Is(n,"hidden")||Is(n,"rsid")||Is(n,"locked")||Is(n,"personal")||
            Is(n,"personalCompose")||Is(n,"personalReply")||Is(n,"next")||Is(n,"autoRedefine"));

        bool SameChildren(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include) =>
            FirstChildDifference(a,b,include) is null;

        string? FirstDifference(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include)
        {
            token.ThrowIfCancellationRequested();
            if(a.Name!=b.Name)return ShortName(a)+(b.Name.StartsWith('#')?"":" / "+ShortName(b));
            if(a.Text!=b.Text||!SameAttributes(a,b))return ShortName(a);
            var child=FirstChildDifference(a,b,include);
            return child is null?null:ShortName(a)+"/"+child;
        }

        string? FirstChildDifference(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include)
        {
            if(SemanticIndex.IsAlternative(a)&&SelectedBranchOrdinal(a)!=SelectedBranchOrdinal(b))
                return ShortName(a);
            using var left=children[a.Id].Where(Significant).Where(include).GetEnumerator();
            using var right=children[b.Id].Where(Significant).Where(include).GetEnumerator();
            while(left.MoveNext())
            {
                if(!right.MoveNext())return ShortName(left.Current);
                var difference=FirstDifference(left.Current,right.Current,_=>true);
                if(difference is not null)return difference;
            }
            return right.MoveNext()?ShortName(right.Current):null;
        }

        int SelectedBranchOrdinal(InventoryNode alternative)
        {
            string? chosen=semantic!.Children(alternative.Id).FirstOrDefault().Id;
            return children[alternative.Id].Where(Significant).TakeWhile(n=>n.Id!=chosen).Count();
        }

        static bool Significant(InventoryNode n)=>n.Kind=="textValue"?!string.IsNullOrWhiteSpace(n.Text):!n.Name.StartsWith('#');
        static string ShortName(InventoryNode n)=>n.Name[(n.Name.IndexOf('}')+1)..];

        bool SameStyleContext(InventoryNode a,InventoryNode b)
        {
            if(!AttributesOf(a,Ignorable).SequenceEqual(AttributesOf(b,Ignorable)))return false;
            if(a.Attributes.Keys.Any(key=>key!=Ignorable&&key.StartsWith(MarkupCompatibility,StringComparison.Ordinal))&&
                !a.Attributes.Where(x=>x.Key.StartsWith(Xmlns,StringComparison.Ordinal))
                    .SequenceEqual(b.Attributes.Where(x=>x.Key.StartsWith(Xmlns,StringComparison.Ordinal))))return false;
            var left=IgnorableNamespaces(a);
            var right=IgnorableNamespaces(b);
            if(left is null||right is null)return false;
            left.SymmetricExceptWith(right);
            if(left.Count==0)return true;

            // An unused namespace declaration cannot change the style properties in either XML tree.
            left.Add(MarkupCompatibility);
            foreach(var node in input.Manifest.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if(node.Source.PartUri!=a.Source.PartUri&&node.Source.PartUri!=b.Source.PartUri)continue;
                if(node.ParentId is not null&&(Affected(node.Name)||node.Attributes.Keys.Any(Affected)))return false;
            }
            return true;

            bool Affected(string name)=>left.Any(ns=>name.StartsWith(ns,StringComparison.Ordinal));
        }

        static HashSet<string>? IgnorableNamespaces(InventoryNode node)
        {
            var namespaces=new HashSet<string>(StringComparer.Ordinal);
            if(!node.Attributes.TryGetValue(Ignorable,out string? list))return namespaces;
            foreach(var prefix in list.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries))
            {
                if(!node.Attributes.TryGetValue(Xmlns+prefix,out string? ns))return null;
                namespaces.Add("{"+ns+"}");
            }
            return namespaces;
        }

        bool SameAttributes(InventoryNode a,InventoryNode b)
        {
            // Normalise spellings of an existing boolean declaration without treating absence as false.
            string? valueKey=Is(a,"style")?WordDefault:IsOnOffProperty(a)?WordValue:null;
            if(valueKey is not null)
            {
                string? left=a.Attributes.GetValueOrDefault(valueKey),right=b.Attributes.GetValueOrDefault(valueKey);
                if(left!=right&&ReadOnOff(left,valueKey==WordValue) is {} l&&ReadOnOff(right,valueKey==WordValue)==l)
                    return AttributesOf(a,valueKey).SequenceEqual(AttributesOf(b,valueKey));
            }
            return AttributesOf(a,null).SequenceEqual(AttributesOf(b,null));
        }

        bool IsOnOffProperty(InventoryNode node) => node.ParentId is {} id&&
            (Is(nodes[id],"rPr")&&(Is(node,"b")||Is(node,"i")||Is(node,"bCs")||Is(node,"iCs")||
                Is(node,"vanish")||Is(node,"rtl")||Is(node,"cs"))||
             Is(nodes[id],"pPr")&&(Is(node,"keepNext")||Is(node,"keepLines")||Is(node,"widowControl")||
                Is(node,"pageBreakBefore")||Is(node,"contextualSpacing")));

        static bool? ReadOnOff(string? raw,bool element) => raw switch
        {
            null when element=>true,
            "1" or "true" or "on"=>true,
            "0" or "false" or "off"=>false,
            _=>null
        };

        static IEnumerable<KeyValuePair<string,string>> AttributesOf(InventoryNode n,string? except) =>
            n.Attributes.Where(x=>x.Key!=except&&!x.Key.StartsWith(Xmlns,StringComparison.Ordinal));
    }
}
