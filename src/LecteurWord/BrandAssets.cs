using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LecteurWord;

/// <summary>Unmodified artwork supplied by the project owner; fixed application-lifetime resources.</summary>
public static class BrandAssets
{
    static readonly Uri iconUri=new("avares://LecteurWord/Assets/lecteur-word.png");
    static readonly Lazy<Bitmap> icon=new(()=>new Bitmap(AssetLoader.Open(iconUri)));
    static readonly Lazy<Bitmap> logo=new(()=>new Bitmap(AssetLoader.Open(new Uri("avares://LecteurWord/Assets/lecteur-word-logo.png"))));
    static readonly Lazy<WindowIcon> windowIcon=new(()=>new WindowIcon(AssetLoader.Open(iconUri)));
    public static WindowIcon WindowIcon=>windowIcon.Value;
    public static Image CreateImage(double size,bool fullLogo=false)
    {
        var image=new Image{Source=fullLogo?logo.Value:icon.Value,Width=size,Height=size,Stretch=Stretch.Uniform,Name=fullLogo?"LecteurWordLogo":"LecteurWordIcon"};
        AutomationProperties.SetName(image,"Lecteur Word");return image;
    }
}
