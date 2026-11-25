using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LecteurWord.Flow;

namespace LecteurWord;

internal enum ReadingView { Unavailable, Continuous, SinglePage, FacingPages }

public sealed partial class ReaderWindow
{
    const double PageGap=28,SpreadGutter=24,PageTop=12,PageBottom=24,PageNumberSpace=18;
    const long RasterBudget=16_000_000,PageRasterBudget=4_000_000;
    readonly Canvas pageDeck=new(){Name="PageDeck",HorizontalAlignment=HorizontalAlignment.Center};
    readonly Dictionary<int,PageVisual> pageVisuals=[];
    readonly Dictionary<(int Index,float Scale),PageRaster> pageRasters=[];
    readonly Queue<(int Index,float Scale)> rasterOrder=[];
    long rasterPixels;
    readonly Dictionary<ReadingView,Button> viewButtons=[];
    Composition? displayedComposition;
    Rect[] pageBoxes=[];
    double deckZoom;
    ReadingView deckView;
    bool updatingPages;
    Vector? pageRevealOffset;
    ReadingView readingView=ReadingView.Unavailable;
    bool simplified=>readingView==ReadingView.Continuous;
    internal ReadingView CurrentReadingView=>readingView;
    internal IReadOnlyList<Rect> DisplayedPageBoxes=>pageBoxes;
    internal int RealizedPageCount=>pageVisuals.Count;
    internal long CachedPagePixels=>rasterPixels;

    sealed record PageRaster(Bitmap Bitmap,long Pixels);
    sealed record PageVisual(Border Frame,Image Paper,NativeSelectionOverlay Selection,PageMarksOverlay Marks)
    {
        internal (int Index,float Scale)? Raster {get;set;}
        internal bool MarksPrepared {get;set;}
    }

    void InitializeViewButtons()
    {
        foreach(var (mode,icon,name,fr,en) in new[]{
            (ReadingView.Continuous,"flow","ViewContinuous","Lecture continue","Continuous reading"),
            (ReadingView.SinglePage,"sheet","ViewSinglePage","Page simple","Single page"),
            (ReadingView.FacingPages,"facing","ViewFacingPages","Double page","Facing pages")})
        {
            var button=IconButton(icon,fr,en,()=>{SetReadingView(mode);return Task.CompletedTask;});
            button.Name=name;
            viewButtons.Add(mode,button);
        }
        AddGroup(viewButtons.Values.ToArray());
    }

    internal void SetReadingView(ReadingView next)
    {
        if(next==readingView||!CanUseView(next))return;
        var before=readingView;
        double y=PageOffsetInPoints();
        string? anchor=before==ReadingView.Continuous?ContinuousAnchor():CurrentBodyAnchor();
        readingView=next;
        fittingWidth=false;
        if(before==ReadingView.Continuous&&anchor is not null&&FindPageBlock(anchor) is {} location)
        {
            page=location.Index;
            y=location.Y;
            CurrentBlockId=anchor;
        }
        else if(next==ReadingView.Continuous&&anchor is not null)
            CurrentBlockId=anchor;

        UpdateUi();
        if(next==ReadingView.Continuous)
        {
            if(anchor is not null)NavigateToBlock(anchor,alignReadingStart:true);
            else ResetReadingPosition();
        }
        else RevealPage(page,y);
    }

    void UpdatePageViewButtons()
    {
        bool canRead=readable?.Available==true;
        foreach(var (mode,button) in viewButtons)
        {
            button.IsVisible=inspection is not null;
            button.IsEnabled=CanUseView(mode);
            bool active=canRead&&readingView==mode;
            UpdateToggle(button,active);
            AutomationProperties.SetItemStatus(button,active?T("Sélectionné","Selected"):"");
        }
    }

    bool CanUseView(ReadingView mode)=>mode switch
    {
        ReadingView.Continuous=>readable?.Available==true,
        ReadingView.SinglePage=>ShowsCalculatedPages,
        ReadingView.FacingPages=>ShowsCalculatedPages,
        _=>false
    };

    double PageOffsetInPoints()=>page<pageBoxes.Length?
        Math.Max(0,(viewport.Offset.Y-pageBoxes[page].Y)/(PointsToDip*deckZoom)):0;

    string? CurrentBodyAnchor()
    {
        if(!ShowsCalculatedPages||CurrentPage==0)return CurrentBlockId;
        var current=composition!.Pages[page];
        if(CurrentBlockId is {} selected&&FindPageBlock(selected)?.Index==page)return selected;
        double offset=PageOffsetInPoints();
        return current.Lines.Where(l=>!l.IsRepeated&&documentIndex?.BodyIndices.ContainsKey(l.ParagraphId)==true)
            .MinBy(l=>Math.Abs(l.Baseline-offset))?.ParagraphId;
    }

    void ResetPageDeck()
    {
        DetachPageRasters();
        pageDeck.Children.Clear();
        pageVisuals.Clear();
        foreach(var raster in pageRasters.Values)raster.Bitmap.Dispose();
        pageRasters.Clear();
        rasterOrder.Clear();
        rasterPixels=0;
        displayedComposition=null;
        pageBoxes=[];
        pageRevealOffset=null;
        pageDeck.Width=pageDeck.Height=0;
    }

    double PageWidthInPoints(int index)
    {
        var pages=composition!.Pages;
        if(readingView==ReadingView.SinglePage)return pages[index].Geometry.Width;
        int left=index%2==1?index:index-1;
        return (left>=0?pages[left].Geometry.Width:pages[0].Geometry.Width)+
            (left+1<pages.Count?pages[left+1].Geometry.Width:0);
    }

    void LayoutPageDeck()
    {
        if(CurrentPage==0||composition is not {} composed)return;
        if(ReferenceEquals(displayedComposition,composed)&&deckZoom==zoom&&deckView==readingView)return;

        if(deckZoom!=zoom)DetachPageRasters();
        displayedComposition=composed;
        deckZoom=zoom;
        deckView=readingView;
        var pages=composed.Pages;
        double scale=PointsToDip*zoom;
        bool facing=readingView==ReadingView.FacingPages;
        double width=facing?0:pages.Max(p=>p.Geometry.Width)*scale;
        var boxes=new Rect[pages.Count];
        double top=PageTop;
        for(int i=0;i<pages.Count;)
        {
            int end=facing&&i>0?Math.Min(i+2,pages.Count):i+1;
            double x=facing&&i==0?pages[0].Geometry.Width*scale+SpreadGutter:0;
            double rowHeight=0;
            for(;i<end;i++)
            {
                var geometry=pages[i].Geometry;
                double w=geometry.Width*scale,h=geometry.Height*scale;
                boxes[i]=new Rect(PageInset+(facing?x:(width-w)/2),top,w,h);
                rowHeight=Math.Max(rowHeight,h);
                x+=w+SpreadGutter;
            }
            if(facing)width=Math.Max(width,x-SpreadGutter);
            top+=rowHeight+PageNumberSpace+PageGap;
        }
        pageBoxes=boxes;
        pageDeck.Width=width+2*PageInset;
        pageDeck.Height=top-PageGap+PageBottom;
    }

    void RevealPage(int index,double y=0,double? x=null)
    {
        if(CurrentPage==0)return;
        updatingPages=true;
        try
        {
            LayoutPageDeck();
            viewport.UpdateLayout();
            var bounds=pageBoxes[index];
            double width=viewport.Viewport.Width;
            double horizontal=viewport.Offset.X;
            if(x is {} points&&width>0)
            {
                double focus=bounds.X+points*PointsToDip*zoom;
                if(focus<horizontal||focus>horizontal+width-24)horizontal=Math.Max(bounds.X,focus-24);
            }
            else if(bounds.X<horizontal||bounds.Right>horizontal+width)
                horizontal=bounds.Width>width?bounds.X:bounds.Right-width;
            double vertical=bounds.Y-PageTop+y*PointsToDip*zoom;
            viewport.Offset=new Vector(Math.Max(0,horizontal),Math.Max(0,vertical));
            pageRevealOffset=viewport.Offset;
            RealizeVisiblePages();
        }
        finally{updatingPages=false;}
        AlignRuler();
    }

    void PagedScrollChanged(ScrollChangedEventArgs change)
    {
        if(updatingPages||CurrentPage==0||pageBoxes.Length==0)return;
        bool requested=pageRevealOffset==viewport.Offset;
        pageRevealOffset=null;
        if(!requested&&change.OffsetDelta.Y!=0&&change.ViewportDelta==default&&change.ExtentDelta==default)
        {
            double y=viewport.Offset.Y+Math.Min(viewport.Viewport.Height*.25,80);
            double Distance(Rect bounds)=>Math.Max(0,Math.Max(bounds.Top-y,y-bounds.Bottom));
            int closest=Enumerable.Range(0,pageBoxes.Length).MinBy(i=>(Distance(pageBoxes[i]),i==page?0:1,
                Math.Abs(pageBoxes[i].Center.X-viewport.Offset.X-viewport.Viewport.Width/2)));
            if(closest!=page)
            {
                ActivatePage(closest);
                UpdateActions();
                UpdateNavigationPosition();
                if(navMode=="pages")FillPanel();
            }
        }
        RealizeVisiblePages();
        AlignRuler();
    }

    void ActivatePage(int index)
    {
        page=index;
        CurrentSection=composition!.Pages[index].Section;
        CurrentBlockId=null;
        SelectPageParagraph();
    }

    void RealizeVisiblePages()
    {
        if(CurrentPage==0||displayedComposition is not {} displayed||pageBoxes.Length==0)return;
        var view=new Rect(new Point(viewport.Offset.X,viewport.Offset.Y),viewport.Viewport);
        var wanted=Enumerable.Range(0,pageBoxes.Length).Where(i=>pageBoxes[i].Intersects(view)).ToHashSet();
        if(wanted.Count==0)wanted.Add(page);
        foreach(int obsolete in pageVisuals.Keys.Where(i=>!wanted.Contains(i)).ToArray())
        {
            var visual=pageVisuals[obsolete];
            visual.Paper.Source=null;
            pageDeck.Children.Remove(visual.Frame);
            pageVisuals.Remove(obsolete);
        }
        long budget=Math.Min(PageRasterBudget,RasterBudget/wanted.Count);
        foreach(int index in wanted.Order())
        {
            var bounds=pageBoxes[index];
            var source=displayed.Pages[index];
            if(!pageVisuals.TryGetValue(index,out var visual))
                pageVisuals.Add(index,visual=CreatePageVisual(index));
            Canvas.SetLeft(visual.Frame,bounds.X);
            Canvas.SetTop(visual.Frame,bounds.Y);
            visual.Frame.Width=bounds.Width;
            visual.Paper.Width=bounds.Width;
            visual.Paper.Height=bounds.Height;
            SetPageRaster(index,source,visual,budget);
        }
        TrimRasters();
        UpdateNativeSelection();
        UpdatePageMarks();
    }

    PageVisual CreatePageVisual(int index)
    {
        int numberValue=index+1;
        var id=ShowsNativePages?nativePageLease!.Snapshot.Pages[index].Id:(CalculatedPageId?)null;
        var image=new Image{Name="CalculatedPage",Tag=numberValue,Stretch=Stretch.Fill};
        image.PointerPressed+=(_,e)=>PagePointerPressed(index,image,e);
        var selection=new NativeSelectionOverlay{Name="NativePageSelection",IsHitTestVisible=false,ClipToBounds=true};
        ReaderAppearance.Use(selection,TextElement.ForegroundProperty,ReaderAppearance.Accent);
        var marks=new PageMarksOverlay{Name="PageNonprintingMarks",IsHitTestVisible=false,ClipToBounds=true};
        var layers=new Panel{ClipToBounds=true,Children={image,selection,marks}};
        var paper=new Border{Background=Brushes.White,Child=layers,
            BoxShadow=new BoxShadows(new BoxShadow{Blur=12,Color=Color.Parse("#36000000"),OffsetY=2})};
        var number=new TextBlock{Text=numberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize=10,Height=PageNumberSpace,HorizontalAlignment=HorizontalAlignment.Center};
        ReaderAppearance.Use(number,TextBlock.ForegroundProperty,ReaderAppearance.Secondary);
        var container=new Border{Name="PhysicalPage",Tag=id,Child=new StackPanel{Children={paper,number}}};
        AutomationProperties.SetName(container,T("Page physique ","Physical page ")+numberValue);
        pageDeck.Children.Add(container);
        return new(container,image,selection,marks);
    }

    void SetPageRaster(int index,ComposedPage source,PageVisual visual,long budget)
    {
        var geometry=source.Geometry;
        double sum=(double)geometry.Width+geometry.Height;
        double area=(double)geometry.Width*geometry.Height;
        // Bound ceil(width*scale)*ceil(height*scale), including pixel rounding.
        double limit=2*(budget-1)/(sum+Math.Sqrt(sum*sum+4*area*(budget-1)));
        float scale=(float)Math.Clamp(PointsToDip*zoom*RenderScaling,.1,Math.Min(3,Math.Max(.1,limit)));
        var key=(index,scale);
        if(visual.Raster==key)return;
        if(!pageRasters.TryGetValue(key,out var raster))
        {
            long pixels=(long)Math.Ceiling(geometry.Width*scale)*(long)Math.Ceiling(geometry.Height*scale);
            TrimRasters(RasterBudget-pixels);
            using var stream=ReadingPagePng(source,scale);
            var image=new Bitmap(stream);
            raster=new(image,(long)image.PixelSize.Width*image.PixelSize.Height);
            pageRasters.Add(key,raster);
            rasterOrder.Enqueue(key);
            rasterPixels+=raster.Pixels;
        }
        visual.Paper.Source=raster.Bitmap;
        visual.Raster=key;
    }

    void DetachPageRasters()
    {
        foreach(var visual in pageVisuals.Values)
        {
            visual.Paper.Source=null;
            visual.Raster=null;
        }
    }

    void TrimRasters(long limit=RasterBudget)
    {
        if(rasterPixels<=limit)return;
        var inUse=pageVisuals.Values.Select(v=>v.Raster).ToHashSet();
        int count=rasterOrder.Count;
        for(int i=0;i<count&&rasterPixels>limit;i++)
        {
            var key=rasterOrder.Dequeue();
            if(inUse.Contains(key)){rasterOrder.Enqueue(key);continue;}
            var raster=pageRasters[key];
            pageRasters.Remove(key);
            rasterPixels-=raster.Pixels;
            raster.Bitmap.Dispose();
        }
    }

    void RefreshPageLabels()
    {
        foreach(var (index,visual) in pageVisuals)
        {
            AutomationProperties.SetName(visual.Frame,T("Page physique ","Physical page ")+(index+1));
        }
    }
}
