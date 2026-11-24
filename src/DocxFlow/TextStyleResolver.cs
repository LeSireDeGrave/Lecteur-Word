using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

internal sealed record TextStyle
{
    internal float HorizontalScale {get;init;}=100;
    internal bool Bold { get; init; }
    internal bool Italic { get; init; }
    internal bool Caps { get; init; }
    internal bool SmallCaps { get; init; }
    internal bool? ComplexBold { get; init; }
    internal bool? ComplexItalic { get; init; }
    internal bool RightToLeft { get; init; }
    internal bool Complex { get; init; }
    internal bool Hidden { get; init; }
    internal bool WebHidden { get; init; }
    internal bool SpecialVanish { get; init; }
    internal bool VisibilityUncertain { get; init; }
    internal bool SuppressText => Hidden || VisibilityUncertain;
    internal float Size { get; init; } = 12;
    internal float? ComplexSize { get; init; }
    internal float Tracking { get; init; }
    internal float? KernAt { get; init; }
    internal float BaselineShift { get; init; }
    internal string VerticalAlignment { get; init; } = "baseline";
    internal string Color { get; init; } = "auto";
    internal string Underline { get; init; } = "none";
    internal string UnderlineColor { get; init; } = "auto";
    internal string? Background { get; init; }
    internal TextShadow? Shadow { get; init; }
    internal string Language { get; init; } = "und";
    internal string? EastAsianLanguage { get; init; }
    internal string? BidiLanguage { get; init; }
    internal string Ligatures { get; init; } = "default";
    internal WordFontSlots Slots { get; init; } = new();
    internal ImmutableDictionary<string, SemanticSource> Origins { get; init; } = ImmutableDictionary<string, SemanticSource>.Empty;
}

internal sealed partial class TextStyleResolver(SemanticDocument model, Action<string, string, string> issue,TableStyleCascade? tableStyles=null)
{
    const string W = SemanticIndex.W;
    internal const string W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    const string A = SemanticIndex.Drawing;
    readonly Dictionary<string, TextStyle> cache = new(StringComparer.Ordinal);
    readonly Dictionary<string, string[]> tableLayers = new(StringComparer.Ordinal);
    readonly Lazy<(string? Mapping, string? Scheme)> theme = new(() =>
    {
        string? Find(string part, string name) => model.Elements
            .Where(e => model.Name(e.Id) == name && model.Source(e.Id).Location.PartUri == part)
            .Select(e => e.Id).SingleOrDefaultIfUnique();
        return (model.RelatedPart("settings") is {} s ? Find(s, "{" + W + "}clrSchemeMapping") : null,
            model.RelatedPart("theme") is {} t ? Find(t, "{" + A + "}clrScheme") : null);
    });

    internal int CacheHits { get; private set; }
    long visibilityTicks;
    internal long VisibilityTicks => visibilityTicks;
    internal static bool IsVisibility(string name)=>name is "{" + W + "}vanish" or "{" + W + "}webHidden" or "{" + W + "}specVanish";
    internal static bool? OnOff(string? raw)=>raw switch
    {
        null or "1" or "true" or "on" => true,
        "0" or "false" or "off" => false,
        _ => null
    };
    internal void Clear() { cache.Clear(); tableLayers.Clear(); }
    string? Attr(string id, string key, string ns = W) => model.Attribute(id, key, ns);
    bool Word(string id, string name) => model.Name(id) == SemanticIndex.QName(W,name);
    bool ParentIs(string id, string name) => model.StylePropertyParent(id) is {} p && Word(p, name);

    internal (bool Rtl, SemanticSource? Origin) ParagraphDirection(string paragraph)
    {
        (bool, SemanticSource?) direction = (false, null);
        foreach (string properties in model.PresentationDeclarations(paragraph, unresolved: Unresolved,
            tableParagraphProperties:tableStyles?.Text(paragraph).Select(id=>model.ChildId(id,"pPr")).OfType<string>()))
        {
            if (model.ChildId(properties, "bidi") is not {} id) continue;
            if (Toggle(id) is {} value) direction = (value, model.Source(id));
            else Unknown(id);
        }
        return direction;
    }

    internal TextStyle Resolve(string runOrParagraph, bool mark,string? lastRoot=null,string? paragraphStyleOverride=null)
    {
        var roots = model.PresentationDeclarations(runOrParagraph, mark, Unresolved,paragraphStyleOverride:paragraphStyleOverride).ToList();
        string? table = Ancestor(runOrParagraph, "tbl");
        if (table is not null)
            roots.InsertRange(roots.Count > 0 && ParentIs(roots[0], "rPrDefault") ? 1 : 0, tableStyles is null?TableLayers(table):
                tableStyles.Text(runOrParagraph).Select(id=>model.ChildId(id,"rPr")).OfType<string>());
        if(lastRoot is not null)roots.Add(lastRoot);
        string key = string.Join(' ', roots);
        if (cache.TryGetValue(key, out var existing)) { CacheHits++; return existing; }

        var style = new TextStyle();
        var origins = ImmutableDictionary.CreateBuilder<string, SemanticSource>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            bool toggles = ParentIs(root,"style")&&
                (tableStyles is null||model.StylePropertyParent(root) is not {} st||Attr(st,"type")!="table")||
                tableStyles is null&&ParentIs(root,"tblStylePr");
            string? parent=model.StylePropertyParent(root);
            bool inheritedMark=mark&&parent is not null&&Word(parent,"pPr")&&model.ParentId(parent)!=runOrParagraph;
            bool visibilityToggle=ParentIs(root,"style")||ParentIs(root,"tblStylePr")||
                inheritedMark&&ParentIs(parent!,"style");
            HashSet<string>? visibilitySeen=null;
            foreach (var property in model.BlocksOf(root))
            {
                string id = property.Id, name = model.Name(id);
                string? raw = Attr(id, "val");
                if(IsVisibility(name))
                {
                    long started=Stopwatch.GetTimestamp();
                    visibilitySeen??=new(StringComparer.Ordinal);
                    if(!visibilitySeen.Add(name)||Toggle(id) is not {} value||model.BlocksOf(id).Any()||
                        model.Attributes(id).Any(a=>a.Key!="{" + W + "}val"&&!a.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal)))
                    {
                        style=style with {VisibilityUncertain=true};
                        issue("TEXT_VISIBILITY_UNRESOLVED",id,"Propriété de visibilité irrégulière. Déclaration source conservée.");
                    }
                    else if(name=="{" + W + "}vanish")
                    {
                        if(!visibilityToggle||value)
                        {
                            style=style with {Hidden=visibilityToggle?!style.Hidden:value};
                            Mark("hidden",id);
                        }
                    }
                    else if(name=="{" + W + "}webHidden")
                    {
                        style=style with {WebHidden=value};
                        Mark("webHidden",id);
                    }
                    else
                    {
                        style=style with {SpecialVanish=value};
                        Mark("specVanish",id);
                    }
                    visibilityTicks+=Stopwatch.GetTimestamp()-started;
                    continue;
                }
                if(inheritedMark)continue;
                switch (name)
                {
                    case "{" + W + "}rFonts":
                        style = style with { Slots = WordFontSlots.Apply(style.Slots,model.Source(id).Location,k=>Attr(id,k)) };
                        Mark("rFonts",id);
                        if (style.Slots.Hint is not ("" or "default"))
                            issue("TEXT_FONT_HINT_PARTIAL", id, "Sélection Word selon hint hors profil complet.");
                        break;
                    case "{" + W + "}b": Boolean("bold",style.Bold,v=>style with {Bold=v}); break;
                    case "{" + W + "}i": Boolean("italic",style.Italic,v=>style with {Italic=v}); break;
                    case "{" + W + "}caps": Boolean("caps",style.Caps,v=>style with {Caps=v,SmallCaps=!v&&style.SmallCaps}); break;
                    case "{" + W + "}smallCaps": Boolean("smallCaps",style.SmallCaps,v=>style with {SmallCaps=v,Caps=!v&&style.Caps}); break;
                    case "{" + W + "}bCs": Boolean("complexBold",style.ComplexBold??false,v=>style with {ComplexBold=v}); break;
                    case "{" + W + "}iCs": Boolean("complexItalic",style.ComplexItalic??false,v=>style with {ComplexItalic=v}); break;
                    case "{" + W + "}rtl": Boolean("rtl",style.RightToLeft,v=>style with {RightToLeft=v},false); break;
                    case "{" + W + "}cs": Boolean("cs",style.Complex,v=>style with {Complex=v},false); break;
                    case "{" + W + "}sz": Numeric("size",2,3276,2,v=>style with {Size=v}); break;
                    case "{" + W + "}szCs": Numeric("complexSize",2,3276,2,v=>style with {ComplexSize=v}); break;
                    case "{" + W + "}kern": Numeric("kernAt",0,3276,2,v=>style with {KernAt=v}); break;
                    case "{" + W + "}spacing": Numeric("tracking",-31680,31680,20,v=>style with {Tracking=v}); break;
                    case "{" + W + "}w": Numeric("horizontalScale",1,600,1,v=>style with {HorizontalScale=v}); break;
                    case "{" + W + "}position": Numeric("baselineShift",-3168,3168,2,v=>style with {BaselineShift=v}); break;
                    case "{" + W + "}vertAlign":
                        if(raw is "baseline" or "superscript" or "subscript")
                        {
                            style=style with{VerticalAlignment=raw};
                            Mark("verticalAlignment",id);
                        }
                        else Unknown(id);
                        break;
                    case "{" + W + "}color":
                        string? color = Color(id, raw, out var target);
                        if (color is null) Unknown(id);
                        else
                        {
                            style = style with { Color = color };
                            Mark("color",id);
                            if (target is not null) Mark("colorThemeValue",target);
                            else origins.Remove("colorThemeValue");
                        }
                        break;
                    case "{" + W + "}u":
                        // MS-OI29500 §2.1.100: omitted type and color inherit independently.
                        if (raw is not null)
                        {
                            if (!Underlines.Contains(raw)) Unknown(id);
                            else { style = style with { Underline=raw }; Mark("underline",id); }
                        }
                        string? declaredUnderlineColor=Attr(id,"color");
                        if (declaredUnderlineColor is not null||Attr(id,"themeColor") is not (null or "none"))
                        {
                            string? underlineColor=Color(id,declaredUnderlineColor,out var underlineTarget);
                            if (underlineColor is null) Unknown(id);
                            else
                            {
                                style=style with {UnderlineColor=underlineColor};
                                Mark("underlineColor",id);
                                if(underlineTarget is not null)Mark("underlineColorThemeValue",underlineTarget);
                                else origins.Remove("underlineColorThemeValue");
                            }
                        }
                        break;
                    case "{" + W + "}strike" or "{" + W + "}dstrike" when Toggle(id)==false:break;
                    case "{" + W + "}em" when raw=="none":break;
                    case "{" + W + "}bdr" when raw is "none" or "nil":break;
                    case "{" + W + "}shd" when raw is "clear" or "nil":
                        string? fill=raw=="nil"?"auto":Color(id,Attr(id,"fill")??"auto",out _,
                            "themeFill","themeFillTint","themeFillShade");
                        if(fill is null)Unknown(id);
                        else
                        {
                            style=style with{Background=fill=="auto"?null:fill};
                            Mark("background",id);
                        }
                        break;
                    case "{" + W + "}lang":
                        Language("val","language",v=>style with {Language=v});
                        Language("eastAsia","eastAsianLanguage",v=>style with {EastAsianLanguage=v});
                        Language("bidi","bidiLanguage",v=>style with {BidiLanguage=v});
                        break;
                    case "{" + W14 + "}ligatures":
                        string? feature = Attr(id,"val",W14);
                        if (IsLigatureValue(feature))
                        { style = style with { Ligatures=feature! }; Mark("ligatures",id); }
                        else Unknown(id);
                        break;
                    case "{" + W14 + "}shadow":
                        if(ReadShadow(id,out string? shadowTheme) is {} shadow)
                        {
                            style=style with{Shadow=shadow};
                            Mark("shadow",id);
                            if(shadowTheme is not null)Mark("shadowThemeValue",shadowTheme);
                            else origins.Remove("shadowThemeValue");
                        }
                        else Unknown(id);
                        break;
                    case "{" + W14 + "}textOutline" when NoOutlineStroke(id):
                        Mark("textOutline",id);
                        break;
                    case "{" + W + "}rStyle" or "{" + W + "}rPrChange" or "{" + W + "}noProof":
                        break;
                    default:
                        Unknown(id);
                        break;
                }

                void Boolean(string propertyName,bool current,Func<bool,TextStyle> apply,bool isToggle = true)
                {
                    if (Toggle(id) is not {} value) { Unknown(id); return; }
                    if(toggles&&isToggle)
                    {
                        if(!value) return;
                        value=!current;
                    }
                    style=apply(value);
                    Mark(propertyName,id);
                }

                void Numeric(string propertyName,int minimum,int maximum,int divisor,Func<float,TextStyle> apply)
                {
                    if(!int.TryParse(raw,NumberStyles.Integer,CultureInfo.InvariantCulture,out int n)||n<minimum||n>maximum)
                    { Unknown(id); return; }
                    style=apply((float)n/divisor);
                    Mark(propertyName,id);
                }

                void Language(string keyName,string propertyName,Func<string,TextStyle> apply)
                {
                    if (Attr(id,keyName) is not {} code) return;
                    if (code.Length is <1 or >48 || !code.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'))
                    { Unknown(id); return; }
                    style=apply(code);
                    Mark(propertyName,id);
                }
            }
        }
        style = style with { Origins = origins.ToImmutable() };
        if (cache.Count < 2048) cache.Add(key, style);
        return style;

        void Mark(string property,string id) => origins[property] = model.Source(id);
    }

    bool? Toggle(string id) => OnOff(Attr(id,"val"));

    string? Ancestor(string id, string name)
    {
        var story = model.Element(id).StoryId;
        for(string? parent = model.ParentId(id);parent is not null && model.Element(parent).StoryId==story;parent = model.ParentId(parent))
            if (Word(parent,name)) return parent;
        return null;
    }

    string[] TableLayers(string table)
    {
        if (tableLayers.TryGetValue(table,out var cached)) return cached;
        string? p = model.ChildId(table,"tblPr");
        string? use = p is null ? null : model.ChildId(p,"tblStyle");
        use ??= model.Styles.Where(s=>s.Type=="table"&&s.IsDefault).SingleOrDefaultIfUnique()?.Id;
        var chain = use is null ? null : model.StyleUse(use);
        if (use is not null && chain?.Resolved != true) Unresolved(use);
        var layers = new List<string>();
        if (chain is not null)
        {
            foreach (string definition in chain.BaseToDerivedIds)
            {
                if (model.ChildId(definition,"rPr") is {} properties) layers.Add(properties);
                foreach(var condition in model.BlocksOf(definition).Where(e=>Word(e.Id,"tblStylePr")))
                {
                    if (model.ChildId(condition.Id,"rPr") is not {} c) continue;
                    if (Attr(condition.Id,"type")=="wholeTable") layers.Add(c);
                    else issue("TEXT_TABLE_CONDITION_PARTIAL",condition.Id,"Propriétés typographiques de région conservées. Leur sélection reste à qualifier.");
                }
            }
        }
        var result=layers.ToArray();
        if(tableLayers.Count<512)tableLayers.Add(table,result);
        return result;
    }

    string? Color(string id,string? value,out string? target,string themeKey="themeColor",
        string tintKey="themeTint",string shadeKey="themeShade")
    {
        target=null;
        string? key=Attr(id,themeKey);
        if (key is not (null or "none"))
        {
            key=WordColor.ThemeSlot(theme.Value.Mapping is {} m?Attr(m,WordColor.MappingKey(key))??key:key);
            value=ThemeRgb(key,out target);
            if(value is not null)value=WordColor.ApplyLuminance(value,Attr(id,tintKey),Attr(id,shadeKey));
        }
        return value is "auto" || value is {Length:6} && value.All(Uri.IsHexDigit) ? value : null;
    }

    string? ThemeRgb(string key,out string? target)
    {
        target=null;
        if(theme.Value.Scheme is not {} scheme)return null;
        var entry=model.BlocksOf(scheme).Where(n=>model.Name(n.Id)==SemanticIndex.QName(A,key)).Select(n=>n.Id).SingleOrDefaultIfUnique();
        if(entry is null)return null;
        target=model.BlocksOf(entry).Select(n=>n.Id).SingleOrDefaultIfUnique();
        if(target is null||model.BlocksOf(target).Any())return null;
        return model.Name(target) switch
        {
            "{" + A + "}srgbClr"=>Attr(target,"val",""),
            "{" + A + "}sysClr"=>Attr(target,"lastClr",""),
            _=>null
        };
    }

    void Unknown(string id) => issue("TEXT_PROPERTY_PARTIAL",id,"Propriété typographique inconnue, invalide ou hors profil. Déclaration conservée.");
    void Unresolved(string id) => issue("TEXT_STYLE_REFERENCE_PARTIAL",id,"Chaîne de style incomplète. Seules les déclarations résolues sont appliquées.");

    static readonly HashSet<string> Underlines = new(StringComparer.Ordinal)
    {
        "none","single","words","double","thick","dotted","dottedHeavy","dash","dashedHeavy",
        "dashLong","dashLongHeavy","dotDash","dashDotHeavy","dotDotDash","dashDotDotHeavy","wave","wavyHeavy","wavyDouble"
    };
    static readonly HashSet<string> LigatureValues = new(StringComparer.Ordinal)
    {
        "none","standard","contextual","historical","discretionary","standardContextual","standardHistorical",
        "standardDiscretionary","contextualHistorical","contextualDiscretionary","historicalDiscretionary",
        "standardContextualHistorical","standardContextualDiscretionary","standardHistoricalDiscretionary",
        "contextualHistoricalDiscretionary","all"
    };
    internal static bool IsLigatureValue(string? value) => value is not null&&LigatureValues.Contains(value);
}

internal static class TextStyleEnumerable
{
    internal static T? SingleOrDefaultIfUnique<T>(this IEnumerable<T> source) where T:class => SemanticIndex.Unique(source);
}
