using System.Globalization;

namespace LecteurWord.Flow;

internal static class WordColor
{
    internal static string? ApplyLuminance(string color,string? tint,string? shade)
    {
        if(string.IsNullOrEmpty(tint)&&string.IsNullOrEmpty(shade))return color;
        if(color.Length!=6||!int.TryParse(color,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out int rgb))return null;
        bool useShade=string.IsNullOrEmpty(tint);
        string? raw=useShade?shade:tint;
        if(raw is not {Length:2}||!byte.TryParse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out byte factor))return null;
        double r=(rgb>>16)/255d,g=((rgb>>8)&255)/255d,b=(rgb&255)/255d;
        double min=Math.Min(r,Math.Min(g,b)),max=Math.Max(r,Math.Max(g,b));
        double original=(min+max)/2;
        double lum=useShade?original*(factor/255d):1-(1-original)*(factor/255d);
        double divisor=1-Math.Abs(2*original-1);
        double ratio=divisor>0?(1-Math.Abs(2*lum-1))/divisor:0;
        return Hex(r)+Hex(g)+Hex(b);

        string Hex(double v)=>Math.Clamp((int)Math.Round((lum+(v-original)*ratio)*255),0,255).ToString("X2",CultureInfo.InvariantCulture);
    }
}
