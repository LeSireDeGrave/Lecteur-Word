using System.Globalization;
using SkiaSharp;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;
public sealed record PageFragment(string RunId,SourceLocation Source,int Offset,string Text,float X,LayoutStyle Style)
{
    public bool Generated {get;init;}
}
public sealed record PageLine(string ParagraphId, SourceLocation Source, string Text, float X, float Baseline)
{
    public IReadOnlyList<PageFragment> Fragments {get;init;}=[];
    public float Height {get;init;}
    public string SourceBreak {get;init;}="";
    internal bool HasExplicitPageBreak {get;init;}
    public int Section {get;init;}=1;
    public string? CellId {get;init;}
    public bool IsRepeated {get;init;}
    public IReadOnlyList<PageImage> Images {get;init;}=[];
    public IReadOnlyList<PageTab> Tabs {get;init;}=[];
    internal ComposedLine? Native {get;init;}
    internal float NativeOffsetX {get;init;}
    internal float NativeTop {get;init;}
}
public sealed record ComposedPage(int Number, IReadOnlyList<PageLine> Lines)
{
    public IReadOnlyList<PageCell> Cells {get;init;}=[];
    public PageGeometry Geometry {get;init;}=new();
    public int Section {get;init;}=1;
    internal LineComposer? NativePainter {get;init;}
    internal IReadOnlyList<PageStoryPlacement> RunningStories {get;init;}=[];
    internal IEnumerable<PageLine> DisplayLines=>Lines.Concat(RunningStories.SelectMany(s=>s.Lines));
    internal IEnumerable<PageCell> DisplayCells=>Cells.Concat(RunningStories.SelectMany(s=>s.Cells));
}
public sealed record Composition(IReadOnlyList<ComposedPage> Pages, IReadOnlyList<FlowIssue> Issues)
{
    public DocumentFonts Fonts {get;init;}=new();
    public const float Width = 595.276f, Height = 841.89f, Margin = 54f, FontSize = 12f, Leading = 18f;
    public bool Available => Pages.Count>0 && !Issues.Any(i=>i.BlocksRendering);
    public string Profile {get;init;}=FlowDocument.Profile;
}
public static partial class Composer
{
    public const string FontFamily = "DejaVu Serif";
    public static string FontLicense
    {
        get
        {
            using var stream=typeof(Composer).Assembly.GetManifestResourceStream("LecteurWord.Fonts.License.txt")??throw new InvalidDataException("FONT_NOTICE_MISSING");
            using var reader=new StreamReader(stream);return reader.ReadToEnd();
        }
    }
    static readonly byte[] FontBytes=ReadFont();
    static byte[] ReadFont()
    {
        using var stream=typeof(Composer).Assembly.GetManifestResourceStream("LecteurWord.Fonts.DejaVuSerif.ttf")??throw new InvalidDataException("BUNDLED_FONT_MISSING");
        using var memory=new MemoryStream();stream.CopyTo(memory);return memory.ToArray();
    }
    static SKTypeface CreateTypeface()
    {
        using var data=SKData.CreateCopy(FontBytes);return SKTypeface.FromData(data);
    }
    public static bool SupportedText(string text) => text.EnumerateRunes().All(r=>
        r.Value is '\n' or '\t' ||
        ((r.Value is >=32 and <=0x24f || r.Value is >=0x2010 and <=0x2026) &&
        System.Text.Rune.GetUnicodeCategory(r) is not (UnicodeCategory.Control or UnicodeCategory.Format)));
    public static Composition Compose(FlowDocument document,CancellationToken token=default)
    {
        try{return ComposeCore(document,token);}
        catch(InvalidDataException e){return new([],[..document.Issues,new(e.Message,null,"Limite de composition / Composition limit",true)]);}
    }
    static Composition ComposeCore(FlowDocument document,CancellationToken token)
    {
        var issues=document.Issues.ToList();
        if(!document.CanRender)return new([],issues);
        using var typeface=CreateTypeface();
        using var font=new SKFont(typeface,Composition.FontSize);
        if(typeface is null || !typeface.FamilyName.Equals(FontFamily,StringComparison.Ordinal))
            return new([],[..issues,new("REFERENCE_FONT_MISSING",null,"Police de composition absente / Missing composition font",true)]);
        var pages=new List<ComposedPage>();var lines=new List<PageLine>();float y=Composition.Margin+Composition.FontSize;
        void Page(){pages.Add(new(pages.Count+1,lines.ToArray()));lines.Clear();y=Composition.Margin+Composition.FontSize;}
        void Line(FlowParagraph p,string text)
        {
            token.ThrowIfCancellationRequested();
            if(y>Composition.Height-Composition.Margin)Page();
            if(pages.Count>=500)throw new InvalidDataException("PAGE_LIMIT");
            lines.Add(new(p.Id,p.Source,text,Composition.Margin,y));y+=Composition.Leading;
        }
        foreach(var p in document.Paragraphs)
        {
            token.ThrowIfCancellationRequested();
            if(!SupportedText(p.Text)) {issues.Add(new("SCRIPT_NOT_QUALIFIED",p.Source,"Écriture hors profil de composition / Script outside composition profile",true));continue;}
            string expanded=p.Text.Replace("\t","    ",StringComparison.Ordinal);
            if(p.Text.Contains('\t'))issues.Add(new("TABS_PREVIEW_ONLY",p.Source,"Tabulations affichées à quatre espaces / Four-space tab preview",false));
            foreach(string physical in expanded.Split('\n'))
            {
                if(physical.Length==0){Line(p,"");continue;}
                if(!font.ContainsGlyphs(physical)){issues.Add(new("MISSING_GLYPH",p.Source,"Glyphe absent de la police / Missing font glyph",true));continue;}
                var enumerator=StringInfo.GetTextElementEnumerator(physical);string line="";
                while(enumerator.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    string element=enumerator.GetTextElement();
                    if(font.MeasureText(line+element)>Composition.Width-2*Composition.Margin && line.Length>0)
                    {
                        int split=line.LastIndexOf(' ');
                        if(split>0){Line(p,line[..(split+1)]);line=line[(split+1)..];}
                        else{Line(p,line);line="";}
                    }
                    line+=element;
                }
                Line(p,line);
            }
            y+=6;
            if(p.PageBreakAfter)Page();
        }
        if(lines.Count>0 || pages.Count==0)Page();
        if(issues.Any(i=>i.BlocksRendering))return new([],issues);
        return new(pages,issues);
    }
    public static void DrawPage(SKCanvas canvas,ComposedPage page)
    {
        canvas.Clear(SKColors.White);
        if(page.RunningStories.Count>0||page.Cells.Count>0||page.Lines.Any(l=>l.Native is not null||l.Fragments.Count>0||l.Images.Count>0)){DrawStyledPage(canvas,page);return;}
        using var typeface=CreateTypeface();using var font=new SKFont(typeface,Composition.FontSize);
        using var paint=new SKPaint{Color=SKColors.Black,IsAntialias=true};
        foreach(var line in page.Lines)canvas.DrawText(line.Text,line.X,line.Baseline,font,paint);
    }
    public static byte[] PagePng(ComposedPage page,float scale=1)
    {
        if(scale is <.1f or >3||!float.IsFinite(scale))throw new ArgumentOutOfRangeException(nameof(scale));
        if((double)page.Geometry.Width*page.Geometry.Height*scale*scale>16_000_000)throw new InvalidDataException("PAGE_RASTER_LIMIT");
        using var surface=SKSurface.Create(new SKImageInfo((int)Math.Ceiling(page.Geometry.Width*scale),(int)Math.Ceiling(page.Geometry.Height*scale)));
        surface.Canvas.Scale(scale);DrawPage(surface.Canvas,page);
        using var image=surface.Snapshot();using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
    }
}
