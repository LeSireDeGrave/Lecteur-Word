using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    readonly NativeSelectionOverlay nativeSelection=new(){Name="NativePageSelection",IsHitTestVisible=false,ClipToBounds=true};
    internal IReadOnlyList<SemanticSource> SelectedNativeSources {get;private set;}=[];
    int nativeSelectionPage;
    bool ShowsNativePages=>nativePageLease is {} lease&&ReferenceEquals(composition,lease.Snapshot.Composition);

    void SelectNativeAt(Point point)
    {
        if(!ShowsNativePages||CurrentPage==0)return;
        var selection=composition!.Pages[page].DisplayLines.SelectMany(PageCoordinates.Clusters)
            .Where(c=>c.Rect.Contains(point.X,point.Y)).MinBy(c=>Math.Abs(point.Y-c.Rect.Top-c.Rect.Height/2));
        SetNativeSelection(CurrentPage,selection is null?[]:[selection]);
    }

    void SetNativeSelection(int pageNumber,IReadOnlyList<PageCluster> clusters)
    {
        nativeSelectionPage=pageNumber;
        SelectedNativeSources=clusters.SelectMany(c=>c.Sources).Distinct().ToArray();
        nativeSelection.Set(clusters.Select(c=>c.Rect).ToArray(),zoom);
        UpdateNativeSelection();
    }

    void UpdateNativeSelection()
    {
        nativeSelection.IsVisible=ShowsNativePages&&CurrentPage>0&&CurrentPage==nativeSelectionPage;
        nativeSelection.SetZoom(zoom);
    }

    bool FillNativePageSearch(string needle)
    {
        if(!ShowsNativePages||CurrentPage==0||nativePageLease?.Snapshot.Lines is not {} snapshot||composition is null)return false;
        var lines=composition.Pages.SelectMany(p=>p.DisplayLines.Where(l=>!l.IsRepeated).Select(l=>(Page:p.Number,Line:l)))
            .ToLookup(x=>x.Line.ParagraphId);
        int count=0;
        foreach(var p in snapshot.Typography.Paragraphs)
        {
            int offset=0;
            while(count<100&&offset<=p.ContextLength-needle.Length)
            {
                int at=p.Context.IndexOf(needle,offset,StringComparison.OrdinalIgnoreCase);
                if(at<0||at+needle.Length>p.ContextLength)break;
                offset=at+needle.Length;
                var candidates=lines[p.Id].Where(x=>x.Line.Native is {} l&&l.ContextRange.Start<offset&&l.ContextRange.EndExclusive>at);
                var hit=candidates.GroupBy(x=>x.Page).FirstOrDefault();
                if(hit is null)continue;
                var clusters=hit.SelectMany(x=>PageCoordinates.Clusters(x.Line)).Where(c=>c.ContextRange.Start<offset&&c.ContextRange.EndExclusive>at).ToArray();
                if(clusters.Length==0)continue;
                var button=new Button{Content=SearchExcerpt(p.Context),HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch};
                ToolTip.SetTip(button,T($"Page {hit.Key} de la composition","Calculated page "+hit.Key));
                button.Click+=(_,_)=>
                {
                    GoToPage(hit.Key-1);SetNativeSelection(hit.Key,clusters);
                    viewport.Offset=new Vector(viewport.Offset.X,Math.Max(0,clusters.Min(c=>c.Rect.Top)*PointsToDip*zoom-44));
                };
                navItems.Children.Add(button);count++;
            }
            if(count==100)break;
        }
        navItems.Children.Add(new TextBlock
        {
            Text=count==100?T("100 résultats affichés","100 results shown"):
                count==1?T("1 résultat positionné","1 positioned result"):
                count+T(" résultats positionnés"," positioned results"),
            TextWrapping=TextWrapping.Wrap
        });
        return true;
    }

    sealed class NativeSelectionOverlay:Control
    {
        IReadOnlyList<PageRect> rectangles=[];
        double scale;
        static readonly IBrush Fill=new SolidColorBrush(Color.FromArgb(100,106,169,222));
        internal void Set(IReadOnlyList<PageRect> items,double zoom){rectangles=items;SetZoom(zoom);}
        internal void SetZoom(double zoom){scale=zoom*PointsToDip;InvalidateVisual();}
        public override void Render(DrawingContext context)
        {
            foreach(var r in rectangles)context.FillRectangle(Fill,new Rect(r.Left*scale,r.Top*scale,r.Width*scale,r.Height*scale));
        }
    }
}
