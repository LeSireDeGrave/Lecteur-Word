using System.Globalization;

namespace LecteurWord.Flow;

internal sealed record TextShadow(string Color,byte Alpha,float OffsetX,float OffsetY,float Blur);

internal sealed partial class TextStyleResolver
{
    internal static bool IsGlyphEffect(string name)=>name is "{"+W14+"}shadow" or "{"+W14+"}textOutline";
    internal bool SupportsGlyphEffect(string id,string name)=>name=="{"+W14+"}shadow"?
        ReadShadow(id,out _) is not null:NoOutlineStroke(id);

    // MS-DOCX §2.6.3.30. This profile supports translated glyph masks with
    // identity scales and no skew. Limits bound the cost of offscreen masks.
    TextShadow? ReadShadow(string id,out string? themeSource)
    {
        themeSource=null;
        if(!EffectAttributes(id,"blurRad","dist","dir","sx","sy","kx","ky","algn")||
            Attr(id,"sx",W14) is not ("100000" or "100%")||Attr(id,"sy",W14) is not ("100000" or "100%")||
            ReadInteger(id,"kx",0,0) is null||ReadInteger(id,"ky",0,0) is null||
            Attr(id,"algn",W14) is not (null or "none" or "tl" or "t" or "tr" or "l" or "ctr" or "r" or "bl" or "b" or "br")||
            ReadInteger(id,"blurRad",0,12*12700) is not {} blur||
            ReadInteger(id,"dist",0,72*12700) is not {} distance||
            ReadInteger(id,"dir",0,21_599_999) is not {} direction||
            model.BlocksOf(id).Select(n=>n.Id).SingleOrDefaultIfUnique() is not {} color||
            !EffectAttributes(color,"val"))return null;
        string? rgb=Attr(color,"val",W14);
        if(model.Name(color)=="{"+W14+"}schemeClr"&&rgb is not null)
            rgb=ThemeRgb(rgb,out themeSource);
        else if(model.Name(color)!="{"+W14+"}srgbClr")return null;
        if(rgb is not {Length:6}||!rgb.All(Uri.IsHexDigit))return null;
        var mods=model.BlocksOf(color).Take(2).ToArray();
        int alpha=100_000;
        if(mods.Length>0)
        {
            string a=mods[0].Id;
            if(mods.Length!=1||model.Name(a)!="{"+W14+"}alpha"||!EffectAttributes(a,"val")||model.BlocksOf(a).Any()||
                Attr(a,"val",W14) is null||ReadInteger(a,"val",0,100_000) is not {} opacity)return null;
            alpha=opacity;
        }
        double radians=direction*Math.PI/10_800_000;
        double points=distance/12700d;
        return new(rgb,(byte)Math.Round(alpha*255d/100_000),
            (float)(points*Math.Cos(radians)),(float)(points*Math.Sin(radians)),blur/12700f);
    }

    // MS-DOCX §2.6.3.36. Explicit noFill leaves no stroke to draw.
    bool NoOutlineStroke(string id)
    {
        if(!EffectAttributes(id,"w","cap","cmpd","algn")||
            ReadInteger(id,"w",0,20116800) is null||
            Attr(id,"cap",W14) is not (null or "flat" or "rnd" or "sq")||
            Attr(id,"cmpd",W14) is not (null or "sng" or "dbl" or "thickThin" or "thinThick" or "tri")||
            Attr(id,"algn",W14) is not (null or "ctr" or "in"))return false;
        var parts=model.BlocksOf(id).Take(4).Select(n=>n.Id).ToArray();
        if(parts.Length==0||model.Name(parts[0])!="{"+W14+"}noFill"||!Empty(parts[0]))return false;
        int next=1;
        if(next<parts.Length&&model.Name(parts[next])=="{"+W14+"}prstDash")
        {
            string dash=parts[next++];
            if(!EffectAttributes(dash,"val")||model.BlocksOf(dash).Any()||
                Attr(dash,"val",W14) is not (null or "solid" or "dot" or "dash" or "lgDash" or
                    "dashDot" or "lgDashDot" or "lgDashDotDot" or "sysDash" or "sysDot" or "sysDashDot" or "sysDashDotDot"))return false;
        }
        if(next<parts.Length&&model.Name(parts[next]) is "{"+W14+"}round" or "{"+W14+"}bevel"&&Empty(parts[next]))next++;
        return next==parts.Length;

        bool Empty(string child)=>EffectAttributes(child)&&!model.BlocksOf(child).Any();
    }

    int? ReadInteger(string id,string key,int minimum,int maximum)
    {
        string? value=Attr(id,key,W14);
        if(value is null)return 0;
        return int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int parsed)&&
            parsed>=minimum&&parsed<=maximum?parsed:null;
    }

    bool EffectAttributes(string id,params string[] names)=>model.Attributes(id).All(a=>
        a.Key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal)||
        names.Any(name=>a.Key=="{"+W14+"}"+name));
}
