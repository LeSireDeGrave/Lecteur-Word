using System.Globalization;

namespace LecteurWord.Flow;

internal static class WordColor
{
    internal static string MappingKey(string theme)=>theme switch
    {
        "text1"=>"t1","text2"=>"t2","background1"=>"bg1","background2"=>"bg2",_=>theme
    };

    internal static string ThemeSlot(string theme)=>theme switch
    {
        "text1" or "dark1"=>"dk1","text2" or "dark2"=>"dk2","background1" or "light1"=>"lt1",
        "background2" or "light2"=>"lt2","hyperlink"=>"hlink","followedHyperlink"=>"folHlink",_=>theme
    };

    internal static string? ApplyLuminance(string color,string? tint,string? shade)
    {
        if(tint is null&&shade is null)return color;
        if(color.Length!=6||!int.TryParse(color,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out int rgb))return null;
        // MS-OI29500 §§2.1.72 and 2.1.100: a tint takes precedence over a shade.
        string? raw=tint??shade;
        if(raw is not {Length:2}||!byte.TryParse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out byte factor))return null;
        double r=(rgb>>16)/255d,g=((rgb>>8)&255)/255d,b=(rgb&255)/255d;
        double min=Math.Min(r,Math.Min(g,b)),max=Math.Max(r,Math.Max(g,b));
        double original=(min+max)/2;
        double lum=tint is null?original*(factor/255d):1-(1-original)*(factor/255d);
        double divisor=1-Math.Abs(2*original-1);
        double ratio=divisor>0?(1-Math.Abs(2*lum-1))/divisor:0;
        return Hex(r)+Hex(g)+Hex(b);

        string Hex(double component)
        {
            double scaled=(lum+(component-original)*ratio)*255;
            // Discard fractional darkened channel values. The tiny tolerance
            // only compensates floating-point error at exact integer boundaries.
            int channel=(int)(tint is null?Math.Floor(scaled+1e-10):Math.Round(scaled));
            return Math.Clamp(channel,0,255).ToString("X2",CultureInfo.InvariantCulture);
        }
    }

    internal static uint ModulateDrawingLuminance(uint rgb,long factor)
    {
        double r=(rgb>>16)/255d,g=((rgb>>8)&255)/255d,b=(rgb&255)/255d;
        double minimum=Math.Min(r,Math.Min(g,b)),maximum=Math.Max(r,Math.Max(g,b));
        double original=(minimum+maximum)/2;
        double luminance=original*factor/100_000d;
        double saturationFactor=1-Math.Abs(2*original-1);
        double ratio=saturationFactor>0?(1-Math.Abs(2*luminance-1))/saturationFactor:0;
        return (Channel(r)<<16)|(Channel(g)<<8)|Channel(b);

        uint Channel(double value)=>(uint)Math.Clamp(Math.Round((luminance+(value-original)*ratio)*255),0,255);
    }
}
