using System.Diagnostics;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal enum PageStyleSelection { Ordinary, EcmaCompatibility, Equivalent, EquivalentComposition, Unresolved, OrdinaryWithPlainCopy }
internal sealed record PageStyleDecision(PageStyleSelection Selection, SemanticSource? SelectedRoot, double ComparisonMs,
    DependencyImpact? EffectsImpact)
{
    internal bool CanCompareEffective {get;init;}
}

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
            var selected=model.EffectiveStyles.RootId is {} id?model.Source(id):(SemanticSource?)null;
            if(styleCompositionComparison)
            {
                // The caller already validated both collections against the same package.
                // This private trial has no publication path until both full layouts agree.
                StyleDecision=new(PageStyleSelection.EquivalentComposition,selected,0,DependencyImpact.NoLayoutEffect);
                return;
            }
            long start=Stopwatch.GetTimestamp();
            bool canCompare=false;
            var choice=Compare();
            StyleDecision=new(choice,selected,Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                effects.Relations.IsEmpty?null:choice is PageStyleSelection.Equivalent or PageStyleSelection.EquivalentComposition or
                    PageStyleSelection.EcmaCompatibility or PageStyleSelection.OrdinaryWithPlainCopy?
                    DependencyImpact.NoLayoutEffect:DependencyImpact.Undetermined)
                {CanCompareEffective=canCompare};

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
                // MS-DOCX §2.3.5 and Appendix B note 1 define the ECMA-376 / Word 2007 profile.
                if(model.StyleCompatibility.Mode==12)
                {
                    Issue("PAGE_STYLES_EFFECTS_ECMA_IGNORED",second,
                        "Profil de lecture ECMA-376 (mode 12), collection ordinaire. La partie auxiliaire reste inventoriée.",false);
                    return PageStyleSelection.EcmaCompatibility;
                }
                if(model.StyleCompatibility.Mode is null)
                {
                    var declaration=model.StyleCompatibility.DeclarationIds.FirstOrDefault();
                    Issue("PAGE_STYLES_COMPATIBILITY_UNRESOLVED",declaration is null?second:nodes[declaration],
                        "Le profil de compatibilité enregistré est ambigu ou hors profil.");
                    return PageStyleSelection.Unresolved;
                }

                int firstDifferenceIssue=issues.Count;
                bool sameContext=SameStyleContext(first,second);
                bool sameDefaultStyles=DefaultStyles(ordinary).SequenceEqual(DefaultStyles(effects));
                bool equivalent=sameContext&&sameDefaultStyles&&SameChildren(first,second,OtherThanStyles);
                canCompare=model.StyleCompatibility.Mode is 14 or 15&&sameContext&&sameDefaultStyles&&
                    (equivalent||FirstChildDifference(first,second,OtherThanStyles,compareComposition:true) is null);
                if(!equivalent)
                {
                    Issue("PAGE_STYLES_EFFECTS_DEFAULTS",second,"Valeurs documentaires, contexte de compatibilité XML ou styles par défaut divergents.");
                }
                var used=new HashSet<string>(StringComparer.Ordinal);
                var byName=effects.Definitions.ToDictionary(d=>d.DeclaredId,StringComparer.Ordinal);
                bool generatedToc=model.Fields.Any(f=>f is {Command:"TOC",StructureVerified:true,Locked:false,StoredResultIds.IsEmpty:true});
                foreach(var definition in ordinary.Definitions.Where(d=>d.IsDefault))Use(model.StyleUse(definition.Id)!);
                foreach(var use in model.StyleUses)
                {
                    token.ThrowIfCancellationRequested();
                    if(model.Element(use.Id) is {StoryId:not null,Coverage:not SemanticCoverage.InactiveAlternative})Use(use);
                }

                var ordinaryById=ordinary.Definitions.ToDictionary(d=>d.Id,StringComparer.Ordinal);
                if(generatedToc)
                {
                    foreach(var definition in ordinary.Definitions.Where(TocEntryStyle))Use(model.StyleUse(definition.Id)!);
                    var names=ordinary.Definitions.Select(d=>d.DeclaredId).ToHashSet(StringComparer.Ordinal);
                    foreach(var definition in effects.Definitions.Where(d=>TocEntryStyle(d)&&!names.Contains(d.DeclaredId)))
                    {
                        equivalent=canCompare=false;
                        Issue("PAGE_STYLES_EFFECTS_ONLY_STYLE",nodes[definition.Id],
                            "Style de sommaire régénéré disponible uniquement dans la collection auxiliaire : "+definition.DeclaredId+".");
                    }
                }
                foreach(var id in used.Order(StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var style=nodes[id];
                    string name=A(style,"styleId");
                    if(!byName.TryGetValue(name,out var alternate))
                    {
                        equivalent=false;
                        canCompare &= EmptyTextStyle(ordinaryById[id]);
                        Issue("PAGE_STYLES_EFFECTS_MISSING_STYLE",style,"Style utilisé absent de la partie auxiliaire : "+name+".");
                        continue;
                    }
                    var other=nodes[alternate.Id];
                    if(model.StyleUse(alternate.Id)?.Resolved!=true)
                    {
                        equivalent=false;
                        canCompare=false;
                        Issue("PAGE_STYLES_EFFECTS_USE_UNRESOLVED",other,"La chaîne auxiliaire du style "+name+" n’a pas pu être démontrée.");
                    }
                    var difference=ParentName(ordinaryById[id])!=ParentName(alternate)?"style/basedOn":
                        FirstDifference(style,other,SignificantStyleChild);
                    if(difference is not null)
                    {
                        equivalent=false;
                        canCompare &= ParentName(ordinaryById[id])==ParentName(alternate)&&
                            FirstDifference(style,other,SignificantStyleChild,compareComposition:true) is null;
                        Issue("PAGE_STYLES_EFFECTS_DIVERGENT",other,"Déclarations divergentes du style "+name+" ("+difference+").");
                    }
                }

                if(!equivalent)
                {
                    // MS-DOCX §2.1.1 describes the auxiliary part as a copy of the
                    // Style Definitions part. For this mode-14 profile there is no
                    // effect extension to interpret and the ordinary cascade is usable.
                    if(model.StyleCompatibility.Mode==14&&sameContext&&PlainStyleParts(first,second)&&
                        issues.Skip(firstDifferenceIssue).All(issue=>issue.Code is "PAGE_STYLES_EFFECTS_DIVERGENT" or
                            "PAGE_STYLES_EFFECTS_DEFAULTS" or "PAGE_STYLES_EFFECTS_MISSING_STYLE"))
                    {
                        for(int i=firstDifferenceIssue;i<issues.Count;i++)
                            issues[i]=issues[i] with{BlocksRendering=false};
                        canCompare=false;
                        Issue("PAGE_STYLES_EFFECTS_PLAIN_COPY",second,
                            "Profil 14 sans extension d’effets dans les styles. La composition utilise la collection ordinaire. Les divergences de la copie restent inventoriées.",false);
                        return PageStyleSelection.OrdinaryWithPlainCopy;
                    }
                    return PageStyleSelection.Unresolved;
                }
                canCompare=false;
                Issue("PAGE_STYLES_EFFECTS_EQUIVALENT",second,
                    "Déclarations de rendu et sélecteurs examinés équivalents. Styles ordinaires sélectionnés.",false);
                return PageStyleSelection.Equivalent;

                void Use(SemanticStyleUse use)
                {
                    if(use.MissingReferenceIgnored&&byName.TryGetValue(A(nodes[use.Id],"val"),out var definition))
                    {
                        equivalent=false;
                        canCompare &= EmptyTextStyle(definition);
                        Issue("PAGE_STYLES_EFFECTS_ONLY_STYLE",nodes[definition.Id],
                            "Le style référencé est défini uniquement dans la collection auxiliaire : "+definition.DeclaredId+".");
                    }
                    used.UnionWith(use.BaseToDerivedIds);
                    if(use.Resolved)return;
                    equivalent=false;
                    canCompare=false;
                    Issue("PAGE_STYLES_EFFECTS_USE_UNRESOLVED",nodes[use.Id],"La chaîne ordinaire du style actif n’a pas pu être démontrée.");
                }

                string? ParentName(SemanticStyle style)=>style.ParentId is {} parent?A(nodes[parent],"styleId"):null;
                bool TocEntryStyle(SemanticStyle style)
                {
                    if(style.Type!="paragraph")return false;
                    string? name=model.ChildId(style.Id,"name") is {} declaration?model.Attribute(declaration,"val"):null;
                    for(int level=1;level<=9;level++)
                        if(style.DeclaredId.Equals($"TOC{level}",StringComparison.OrdinalIgnoreCase)||
                            string.Equals(name,$"toc {level}",StringComparison.OrdinalIgnoreCase))return true;
                    return false;
                }
                bool SignificantStyleChild(InventoryNode n)=>SignificantFormattingChild(n)||generatedToc&&Is(n,"name");
                bool EmptyTextStyle(SemanticStyle definition) =>
                    !generatedToc&&!definition.IsDefault&&definition.Type is "paragraph" or "character"&&
                    model.StyleUse(definition.Id) is {Resolved:true,BaseToDerivedIds.Length:1}&&
                    !model.StylePropertyChildren(definition.Id).Any(SignificantStyleChild);
            }
        }

        bool PlainStyleParts(InventoryNode first,InventoryNode second)
        {
            if(IgnorableNamespaces(first) is null||IgnorableNamespaces(second) is null)return false;
            string word="{"+W+"}";
            foreach(var node in input.Manifest.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if(node.Source.PartUri!=first.Source.PartUri&&node.Source.PartUri!=second.Source.PartUri)continue;
                if(!Significant(node))continue;
                if(!node.Name.StartsWith(word,StringComparison.Ordinal)||
                    AttributesOf(node,node.ParentId is null?Ignorable:null).Any(attribute=>
                        !attribute.Key.StartsWith(word,StringComparison.Ordinal)))return false;
            }
            return true;
        }

        bool InvalidCollection(SemanticStyleCollection collection) =>
            collection.Definitions.GroupBy(d=>d.DeclaredId,StringComparer.Ordinal).Any(g=>g.Key.Length==0||g.Count()>1)||
            collection.Definitions.Where(d=>d.IsDefault).GroupBy(d=>d.Type,StringComparer.Ordinal).Any(g=>g.Count()>1)||
            collection.Definitions.Any(d=>nodes[d.Id].Attributes.TryGetValue(WordDefault,out var value)&&ReadOnOff(value,false) is null)||
            collection.RootId is {} id&&semantic!.StylePropertyChildren(id).Where(n=>Is(n,"docDefaults")).Skip(1).Any();

        static IEnumerable<(string Type,string Id)> DefaultStyles(SemanticStyleCollection collection) =>
            collection.Definitions.Where(d=>d.IsDefault).Select(d=>(d.Type,d.DeclaredId)).Order();

        bool OtherThanStyles(InventoryNode n)=>!Is(n,"style")&&!Is(n,"latentStyles");

        bool SignificantFormattingChild(InventoryNode n) => !(Is(n,"name")||Is(n,"aliases")||Is(n,"uiPriority")||Is(n,"qFormat")||
            Is(n,"semiHidden")||Is(n,"unhideWhenUsed")||Is(n,"hidden")||Is(n,"rsid")||Is(n,"locked")||Is(n,"personal")||
            Is(n,"personalCompose")||Is(n,"personalReply")||Is(n,"next")||Is(n,"link")||Is(n,"basedOn")||Is(n,"autoRedefine"));

        bool SameChildren(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include) =>
            FirstChildDifference(a,b,include) is null;

        string? FirstDifference(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include,bool compareComposition=false)
        {
            token.ThrowIfCancellationRequested();
            if(a.Name!=b.Name)return ShortName(a)+(b.Name.StartsWith('#')?"":" / "+ShortName(b));
            if(a.Text!=b.Text||!SameAttributes(a,b,compareComposition))return ShortName(a);
            var child=FirstChildDifference(a,b,include,compareComposition);
            return child is null?null:ShortName(a)+"/"+child;
        }

        string? FirstChildDifference(InventoryNode a,InventoryNode b,Func<InventoryNode,bool> include,bool compareComposition=false)
        {
            bool alternative=SemanticIndex.IsAlternative(a);
            int selectedOrdinal=alternative?SelectedBranchOrdinal(a):-1;
            if(alternative&&selectedOrdinal!=SelectedBranchOrdinal(b))
                return ShortName(a);
            using var left=children[a.Id].Where(Significant).Where(include).GetEnumerator();
            using var right=children[b.Id].Where(Significant).Where(include).GetEnumerator();
            int ordinal=0;
            while(left.MoveNext())
            {
                if(!right.MoveNext())return ShortName(left.Current);
                // Keep control attributes comparable because they govern which choice is selected.
                // The discarded payload is retained in the inventory and has no local layout effect.
                if(alternative&&ordinal++!=selectedOrdinal)
                {
                    if(left.Current.Name!=right.Current.Name||!SameAttributes(left.Current,right.Current,false))
                        return ShortName(left.Current);
                    continue;
                }
                var difference=FirstDifference(left.Current,right.Current,_=>true,compareComposition);
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
            if(!SameAttributeSet(a,b,Ignorable))return false;
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

        HashSet<string>? IgnorableNamespaces(InventoryNode node) =>
            node.Attributes.TryGetValue(Ignorable,out string? list)?ExpandedNames(node,list,false):new(StringComparer.Ordinal);

        HashSet<string>? ExpandedNames(InventoryNode node,string list,bool qualified)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var name in list.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries))
            {
                int colon=name.IndexOf(':');
                if(qualified&&(colon<=0||colon==name.Length-1||name.IndexOf(':',colon+1)>=0))return null;
                string prefix=qualified?name[..colon]:name;
                if(semantic!.NamespaceInScope(node,prefix) is not {Length:>0} ns)return null;
                names.Add("{"+ns+"}"+(qualified?name[(colon+1)..]:""));
            }
            return names.Count==0?null:names;
        }

        bool SameAttributeSet(InventoryNode a,InventoryNode b,string? except,bool compareComposition=false)
        {
            var left=AttributesOf(a,except);
            if(left.Count()!=AttributesOf(b,except).Count())return false;
            foreach(var (key,value) in left)
            {
                if(!b.Attributes.TryGetValue(key,out var other))return false;
                bool prefixes=key is Ignorable or MarkupCompatibility+"MustUnderstand"||
                    key=="{}Requires"&&a.Name==MarkupCompatibility+"Choice";
                bool qualified=key is MarkupCompatibility+"ProcessContent" or MarkupCompatibility+"PreserveElements" or
                    MarkupCompatibility+"PreserveAttributes";
                if(prefixes||qualified)
                {
                    if(ExpandedNames(a,value,qualified) is not {} expanded||
                        ExpandedNames(b,other,qualified) is not {} alternate||!expanded.SetEquals(alternate))return false;
                }
                else if(value!=other&&!(compareComposition&&ComparedInComposition(a,key)))return false;
            }
            return true;
        }

        bool SameAttributes(InventoryNode a,InventoryNode b,bool compareComposition)
        {
            // Normalise spellings of an existing boolean declaration without treating absence as false.
            string? valueKey=Is(a,"style")?WordDefault:IsOnOffProperty(a)?WordValue:null;
            if(valueKey is not null)
            {
                string? left=a.Attributes.GetValueOrDefault(valueKey),right=b.Attributes.GetValueOrDefault(valueKey);
                if(left!=right&&ReadOnOff(left,valueKey==WordValue) is {} l&&ReadOnOff(right,valueKey==WordValue)==l)
                    return SameAttributeSet(a,b,valueKey);
            }
            return SameAttributeSet(a,b,null,compareComposition);
        }

        // Every allowed difference is measured by the shared compositor. New or unsupported
        // properties must keep the collection difference blocking until qualified individually.
        bool ComparedInComposition(InventoryNode node,string attribute) => node.ParentId is {} parent&&
            (Is(nodes[parent],"rPr")&&(Is(node,"sz")&&attribute==WordValue||
                Is(node,"rFonts")&&attribute is "{" + W + "}ascii" or "{" + W + "}hAnsi" or "{" + W + "}eastAsia" or "{" + W + "}cs")||
             Is(nodes[parent],"pPr")&&(Is(node,"spacing")&&attribute is "{" + W + "}before" or "{" + W + "}after" or
                "{" + W + "}line" or "{" + W + "}lineRule"||Is(node,"ind")&&attribute is "{" + W + "}left" or "{" + W + "}right" or
                "{" + W + "}start" or "{" + W + "}end" or "{" + W + "}firstLine" or "{" + W + "}hanging"));

        bool IsOnOffProperty(InventoryNode node) => node.ParentId is {} id&&
            (Is(nodes[id],"rPr")&&(Is(node,"b")||Is(node,"i")||Is(node,"bCs")||Is(node,"iCs")||
                TextStyleResolver.IsVisibility(node.Name)||Is(node,"rtl")||Is(node,"cs"))||
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
