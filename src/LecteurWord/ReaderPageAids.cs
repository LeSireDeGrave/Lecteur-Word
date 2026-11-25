using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Flow;
using SkiaSharp;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    const double PageInset=20;
    const double PointsToDip=96d/72;
    const double PointsPerMillimeter=72d/25.4;
    static readonly IBrush ChocolateMarks=Brush("#795238");
    readonly PageRuler ruler=new(){Name="PageRuler",Height=31,ClipToBounds=true};
    readonly Border rulerBar=new(){Name="RulerBar"};
    readonly RadioButton rulerCentimeters=new(){Name="RulerCentimeters",GroupName="RulerUnit",Content="cm",IsChecked=true};
    readonly RadioButton rulerMillimeters=new(){Name="RulerMillimeters",GroupName="RulerUnit",Content="mm"};
    readonly TextBlock rulerInfo=new(){Name="RulerGeometry",FontSize=10,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly Dictionary<string,PageLine> paragraphEnds=new(StringComparer.Ordinal);
    readonly Dictionary<string,LayoutParagraph> pageParagraphs=new(StringComparer.Ordinal);
    LayoutParagraph? ruledParagraph;
    bool rulerRequested,marksRequested;
    internal bool MarksRequested=>marksRequested;
    internal bool RulerVisible=>rulerBar.IsVisible;

    Control BuildRulerBar()
    {
        var stack=new StackPanel();
        var details=new Grid{ColumnDefinitions=new("Auto,*"),Margin=new Thickness(8,1)};
        var units=new StackPanel{Orientation=Orientation.Horizontal,Spacing=4,Children={rulerCentimeters,rulerMillimeters}};
        details.Children.Add(units);Grid.SetColumn(rulerInfo,1);details.Children.Add(rulerInfo);rulerInfo.Margin=new Thickness(7,0,0,0);
        stack.Children.Add(details);stack.Children.Add(ruler);rulerBar.Child=stack;
        rulerCentimeters.IsCheckedChanged+=(_,_)=>UpdateRuler();
        rulerMillimeters.IsCheckedChanged+=(_,_)=>UpdateRuler();
        viewport.LayoutUpdated+=(_,_)=>AlignRuler();
        return rulerBar;
    }

    void PagePointerPressed(int index,Control sheet,PointerPressedEventArgs e)
    {
        if(CurrentPage==0||e.GetCurrentPoint(sheet).Properties.IsRightButtonPressed)return;
        bool changed=page!=index;
        if(changed)ActivatePage(index);
        var point=e.GetPosition(sheet);
        var pt=new Point(point.X/(PointsToDip*zoom),point.Y/(PointsToDip*zoom));
        if(e.GetCurrentPoint(sheet).Properties.IsLeftButtonPressed&&FollowNativePageLink(pt))return;
        SelectNativeAt(pt);
        var current=composition!.Pages[index];
        var cell=current.Cells.FirstOrDefault(c=>pt.X>=c.X&&pt.X<c.X+c.Width&&pt.Y>=c.Y&&pt.Y<c.Y+c.Height);
        var line=current.Lines.Where(l=>l.CellId==cell?.CellId).MinBy(l=>Math.Abs(l.Baseline-pt.Y));
        if(line is not null&&pageParagraphs.TryGetValue(line.ParagraphId,out var paragraph))
        {
            ruledParagraph=paragraph;
            CurrentBlockId=paragraph.Id;
            CurrentSection=paragraph.Section;
        }
        if(changed&&navMode=="pages")FillPanel();
        UpdateActions();
        UpdateNavigationPosition();
    }

    void PreparePageAids()
    {
        paragraphEnds.Clear();pageParagraphs.Clear();
        if(composition?.Available!=true)return;
        foreach(var paragraph in pageModel?.Paragraphs??[])pageParagraphs[paragraph.Id]=paragraph;
        foreach(var pg in composition.Pages)
            foreach(var line in pg.DisplayLines.Where(l=>!l.IsRepeated))
            {
                paragraphEnds[line.ParagraphId]=line;
                if(line.Native is {} native&&pageParagraphs.TryGetValue(line.ParagraphId,out var data))
                    pageParagraphs[line.ParagraphId]=data with {Properties=data.Properties with
                    {
                        Left=PageComposer.Points(native.Properties.Left),Right=PageComposer.Points(native.Properties.Right),
                        FirstLine=PageComposer.Points(native.Properties.First),Tabs=native.Properties.Tabs.Select(t=>
                            new TabStop(PageComposer.Points(t.Position),t.Kind.ToString().ToLowerInvariant())).ToArray()
                    }};
            }
    }

    void ToggleRuler()
    {
        rulerRequested=!rulerRequested;
        UpdateRuler();
    }

    void ToggleMarks()
    {
        marksRequested=!marksRequested;
        UpdateToggle(marksToggle,marksRequested);
        SetReadingTemplate();
        UpdatePageMarks();
    }

    void UpdateRuler()
    {
        bool paginated=CurrentPage>0;
        if(rulerToggle is not null)rulerToggle.IsVisible=paginated;
        UpdateToggle(rulerToggle,paginated&&rulerRequested);
        rulerBar.IsVisible=paginated&&rulerRequested;
        if(!rulerBar.IsVisible)return;
        var pg=composition!.Pages[page];
        var g=pg.Geometry;
        AutomationProperties.SetName(rulerCentimeters,T("Règle en centimètres","Ruler in centimeters"));
        AutomationProperties.SetName(rulerMillimeters,T("Règle en millimètres","Ruler in millimeters"));
        bool cm=rulerMillimeters.IsChecked!=true;
        double divisor=PointsPerMillimeter*(cm?10:1);
        string unit=cm?"cm":"mm";
        bool a4=Math.Abs(g.Width/PointsPerMillimeter-210)<.8&&Math.Abs(g.Height/PointsPerMillimeter-297)<.8;
        bool a4Landscape=Math.Abs(g.Width/PointsPerMillimeter-297)<.8&&Math.Abs(g.Height/PointsPerMillimeter-210)<.8;
        string dimensions=string.Create(uiCulture,$"{g.Width/divisor:0.0} × {g.Height/divisor:0.0} {unit}");
        var props=ruledParagraph?.Properties;
        string margin=LocalFormat("ruler.margins","Marges G/D {0:F1} / {1:F1} {2}","Margins L/R {0:F1} / {1:F1} {2}",
            g.Left/divisor,g.Right/divisor,unit);
        var p=ruledParagraph;
        rulerInfo.Text=LocalFormat("document.section","Section {0}","Section {0}",p?.Section??pg.Section)+" · "+(a4||a4Landscape?"A4 · ":"")+dimensions+" · "+margin;
        string paragraph=props is null?"":LocalFormat("ruler.indents",
            " · Retraits G {0:F1}, D {1:F1}, 1ʳᵉ {2:F1} {3}"," · Indents L {0:F1}, R {1:F1}, first {2:F1} {3}",
            props.Left/divisor,props.Right/divisor,props.FirstLine/divisor,unit)+" · "+
            LocalCount("ruler.tabs",props.Tabs.Count,"{0:N0} tabulation","{0:N0} tabulations","{0:N0} tab stop","{0:N0} tab stops");
        rulerInfo.Text+=paragraph;
        var cell=p?.CellOwner is {} cellId?pg.Cells.FirstOrDefault(c=>c.CellId==cellId):null;
        var cellModel=p?.CellOwner is {} owner?pageModel?.Tables.SelectMany(t=>t.Cells).FirstOrDefault(c=>c.Id==owner):null;
        (float Left,float Right)? textArea=cell is not null&&cellModel is not null?
            (cell.X+cellModel.Margins.Left,cell.X+cell.Width-cellModel.Margins.Right):null;
        string label=rulerInfo.Text+LocalFormat("ruler.vertical-margins",
            " · Marges haut/bas {0:F1} / {1:F1} {2} · Cliquez dans un paragraphe pour afficher ses repères",
            " · Top/bottom margins {0:F1} / {1:F1} {2} · Click a paragraph to show its guides",
            g.Top/divisor,g.Bottom/divisor,unit);
        AutomationProperties.SetName(ruler,label);ToolTip.SetTip(rulerBar,label);
        ruler.Configure(g,props,textArea,cm,zoom,appearance.Brush(ReaderAppearance.Surface),
            appearance.Brush(ReaderAppearance.Secondary),appearance.Brush(ReaderAppearance.Line),appearance.Brush(ReaderAppearance.Selected));
        AlignRuler();
    }

    void AlignRuler()
    {
        if(!rulerBar.IsVisible||page>=pageBoxes.Length)return;
        double x=(pageDeck.TranslatePoint(new Point(),ruler)?.X??-viewport.Offset.X)+pageBoxes[page].X;
        ruler.Align(x);
    }

    void SelectPageParagraph(string? id=null)
    {
        ruledParagraph=CurrentPage>0?
            (id??composition!.Pages[page].Lines.FirstOrDefault(l=>!l.IsRepeated)?.ParagraphId) is {} chosen?
                pageParagraphs.GetValueOrDefault(chosen):null:null;
        UpdateRuler();
    }

    void UpdatePageMarks()
    {
        foreach(var (index,visual) in pageVisuals)
        {
            visual.Marks.IsVisible=marksRequested&&CurrentPage>0;
            if(!visual.Marks.IsVisible)continue;
            visual.Marks.SetInk(ChocolateMarks);
            if(visual.MarksPrepared){visual.Marks.SetScale(zoom);continue;}
            var marks=CollectPageMarks(composition!.Pages[index],paragraphEnds);
            visual.Marks.Set(marks,zoom);
            visual.MarksPrepared=true;
            if(marks.Count==MaxPageMarks)status.Text=T("Limite d’affichage des marques atteinte sur cette page","Display limit for marks reached on this page");
        }
    }

    const int MaxPageMarks=16_384;
    List<VisualMark> CollectPageMarks(ComposedPage pg,IReadOnlyDictionary<string,PageLine> ends)
    {
        var marks=new List<VisualMark>();
        var faces=new Dictionary<FontResolution,SKTypeface>();
        var fonts=new Dictionary<(FontResolution,float),SKFont>();
        const int maxMarks=MaxPageMarks;
        try
        {
            foreach(var line in pg.DisplayLines)
            {
                if(marks.Count>=maxMarks)break;
                float end=line.X;
                if(line.Native is not null)
                    foreach(var cluster in PageCoordinates.Clusters(line))
                    {
                        end=Math.Max(end,cluster.Rect.Left+cluster.Rect.Width);
                        if(cluster.Text is " " or "\u00a0"&&marks.Count<maxMarks&&cluster.Sources.Length==1)
                            marks.Add(new(cluster.Text==" "?"·":"°",cluster.Rect.Left+cluster.Rect.Width/2,
                                line.Baseline,cluster.Sources[0],true));
                    }
                foreach(var frag in line.Fragments.Where(f=>!f.Generated&&f.Style.Font is not null))
                {
                    var resolution=frag.Style.Font!;
                    if(!faces.TryGetValue(resolution,out var face))faces.Add(resolution,face=FontResolver.Open(resolution));
                    var key=(resolution,frag.Style.Size);
                    if(!fonts.TryGetValue(key,out var font))fonts.Add(key,font=new SKFont(face,frag.Style.Size));
                    end=Math.Max(end,frag.X+font.MeasureText(frag.Text));
                    var widths=font.GetGlyphWidths(frag.Text);
                    float left=frag.X;
                    int i=0,offset=frag.Offset;
                    foreach(var scalar in frag.Text.EnumerateRunes())
                    {
                        if(i>=widths.Length)break;
                        float width=widths[i++];
                        if(scalar.Value is ' ' or 0xA0 && marks.Count<maxMarks &&
                            documentIndex?.SpaceSource(frag.RunId,offset,(char)scalar.Value) is {} source)
                            marks.Add(new(scalar.Value==' '?"·":"°",left+width/2,line.Baseline,source,true));
                        offset+=scalar.Utf16SequenceLength;
                        left+=width;
                    }
                }
                foreach(var tab in line.Tabs)
                {
                    end=Math.Max(end,tab.To);
                    if(documentIndex?.Tabs.TryGetValue(tab.RunId,out var source)==true)Add("→",tab.From,source);
                }
                foreach(var image in line.Images)end=Math.Max(end,image.X+image.Image.Width);
                if(documentIndex?.PageBreakSources.TryGetValue(line,out var breakSource)==true)
                {
                    Add(line.HasExplicitPageBreak?"↡":"↵",end,breakSource);end+=10;
                }
                if(documentIndex?.Paragraphs.TryGetValue(line.ParagraphId,out var paragraph)==true&&
                    ends.TryGetValue(line.ParagraphId,out var last)&&ReferenceEquals(last,line))
                {
                    Add("¶",end,paragraph);
                    if(documentIndex.SectionClosers.TryGetValue(line.ParagraphId,out var section))Add("§",end+9,section);
                }

                void Add(string text,float x,SemanticSource source)
                {
                    if(marks.Count<maxMarks)marks.Add(new(text,x,line.Baseline,source));
                }
            }
        }
        finally
        {
            foreach(var font in fonts.Values)font.Dispose();
            foreach(var face in faces.Values)face.Dispose();
        }
        return marks;
    }

    void SetReadingTemplate()
    {
        readingList.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ReadableBlock>((b,_)=>b is null?new Border():RenderReadingBlock(b));
    }

    bool AddReadingMark(InlineCollection target,string glyph,NonprintingBudget budget,SemanticSource source,bool overPrevious=false)
    {
        if(!marksRequested)return true;
        if(budget.Remaining<=0){budget.Truncated=true;return false;}
        budget.Remaining--;
        var mark=new ReadingMark(glyph,ReadingAccent,overPrevious){Source=source};
        target.Add(new InlineUIContainer(mark));
        return true;
    }

    void AppendSourceText(InlineCollection dest,ReadableBlock block,NonprintingBudget budget)
    {
        string text=block.Text;
        if(!marksRequested){dest.Add(new Run(text));return;}
        int start=0;
        for(int i=0;i<text.Length;i++)
        {
            char scalar=text[i];
            string? symbol=scalar switch{' '=>"·",'\u00a0' or '\u202f'=>"°",'\t'=>"→",'\n'=>"↵",_=>null};
            if(symbol is null)continue;
            var source=scalar=='\t'&&documentIndex?.Tabs.TryGetValue(block.Id,out var tab)==true?tab:
                documentIndex?.SpaceSource(block.Id,i,scalar);
            if(source is null)continue;
            if(budget.Remaining<=0){budget.Truncated=true;break;}
            dest.Add(new Run(text[start..(i+1)]));
            AddReadingMark(dest,symbol,budget,source.Value,overPrevious:true);
            start=i+1;
        }
        if(start<text.Length)dest.Add(new Run(text[start..]));
    }

    internal sealed class NonprintingBudget
    {
        internal int Remaining=1024;
        internal bool Truncated;
    }

    internal sealed class ReadingMark : Control
    {
        readonly string glyph;
        readonly IBrush ink;
        readonly bool overPrevious;
        internal SemanticSource Source { get; init; }
        internal ReadingMark(string glyph,IBrush ink,bool overPrevious)
        {
            this.glyph=glyph;this.ink=ink;this.overPrevious=overPrevious;
            Name="NonprintingMark";Tag=glyph;IsHitTestVisible=false;Width=overPrevious ? .1 : Math.Max(9,glyph.Length*7);Height=13;
        }
        public override void Render(DrawingContext context)
        {
            var label=MarkText(glyph,11,ink);
            context.DrawText(label,new Point(overPrevious?-label.Width*.72:1,-1));
        }
    }

    readonly record struct VisualMark(string Text,float X,float Baseline,SemanticSource Source,bool Centered=false);
    internal IEnumerable<SemanticSource> PageMarkSources=>pageVisuals.TryGetValue(page,out var visual)?visual.Marks.Sources:[];
    sealed class PageMarksOverlay:Control
    {
        IReadOnlyList<VisualMark> marks=[];
        internal IEnumerable<SemanticSource> Sources=>marks.Select(m=>m.Source);
        IBrush ink=Brush("#795238");
        double scale=1;
        internal void Set(IReadOnlyList<VisualMark> next,double zoom){marks=next;scale=zoom*PointsToDip;InvalidateVisual();}
        internal void SetScale(double zoom)
        {
            double value=zoom*PointsToDip;
            if(scale==value)return;
            scale=value;InvalidateVisual();
        }
        internal void SetInk(IBrush color)
        {
            if(ReferenceEquals(ink,color))return;
            ink=color;InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            var text=new Dictionary<string,FormattedText>();
            foreach(var mark in marks)
            {
                if(!text.TryGetValue(mark.Text,out var label))text[mark.Text]=label=MarkText(mark.Text,Math.Clamp(9*scale,6,24),ink);
                context.DrawText(label,new Point(mark.X*scale-(mark.Centered?label.Width/2:0),mark.Baseline*scale-label.Baseline));
            }
        }
    }

    sealed class PageRuler:Control
    {
        PageGeometry? geometry;
        ParagraphLayout? paragraph;
        (float Left,float Right)? textArea;
        bool cm=true;
        double x,scale=1;
        IBrush panel=Brushes.White,ink=Brushes.Black,border=Brushes.Gray,margin=Brushes.LightGray;

        internal void Configure(PageGeometry g,ParagraphLayout? p,(float Left,float Right)? area,bool centimeters,double zoom,
            IBrush background,IBrush foreground,IBrush stroke,IBrush shade)
        {
            geometry=g;paragraph=p;textArea=area;cm=centimeters;scale=zoom*PointsToDip;panel=background;ink=foreground;border=stroke;margin=shade;InvalidateVisual();
        }

        internal void Align(double left)
        {
            if(Math.Abs(x-left)<.1)return;
            x=left;InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if(geometry is not {} g)return;
            double length=g.Width*scale,origin=x+g.Left*scale,right=x+(g.Width-g.Right)*scale;
            var pen=new Pen(border);
            var active=new Pen(ink,1.2);
            context.DrawRectangle(panel,pen,new Rect(x,0,length,30));
            context.FillRectangle(margin,new Rect(x,0,g.Left*scale,29));
            context.FillRectangle(margin,new Rect(right,0,g.Right*scale,29));
            double tick=PointsPerMillimeter*scale;
            int from=(int)Math.Ceiling((x-origin)/tick),to=(int)Math.Floor((x+length-origin)/tick);
            int major=10;
            if(tick<1.5)major=20;
            for(int mm=from;mm<=to;mm++)
            {
                double tx=origin+mm*tick;
                if(tx<0||tx>Bounds.Width)continue;
                bool label=mm%major==0;
                if(tick>=1.3||mm%5==0)context.DrawLine(pen,new Point(tx,0),new Point(tx,label?9:mm%5==0?7:4));
                if(label)
                    context.DrawText(MarkText((cm?mm/10:mm).ToString(CultureInfo.CurrentCulture),9,ink),new Point(tx+2,8));
            }
            context.DrawLine(active,new Point(origin,0),new Point(origin,29));
            context.DrawLine(active,new Point(right,0),new Point(right,29));
            if(paragraph is not {} p)return;
            double contentLeft=textArea is {} a?x+a.Left*scale:origin;
            double contentRight=textArea is {} b?x+b.Right*scale:right;
            Indent(contentLeft+(p.Left+p.FirstLine)*scale,16);
            Indent(contentLeft+p.Left*scale,23);
            Indent(contentRight-p.Right*scale,23);
            foreach(var tab in p.Tabs.Where(t=>t.Position>=0&&contentLeft+t.Position*scale<=contentRight))
            {
                double tx=contentLeft+tab.Position*scale;
                context.DrawLine(active,new Point(tx,23),new Point(tx,29));
                context.DrawLine(active,new Point(tx,29),new Point(tx+(tab.Alignment=="right"?-3:3),29));
            }
            void Indent(double at,double y)
            {
                if(at<x||at>x+length)return;
                context.DrawLine(active,new Point(at,y),new Point(at-3,y+5));
                context.DrawLine(active,new Point(at-3,y+5),new Point(at+3,y+5));
                context.DrawLine(active,new Point(at+3,y+5),new Point(at,y));
            }
        }
    }

    static FormattedText MarkText(string text,double size,IBrush ink)=>
        new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,size,ink);
}
