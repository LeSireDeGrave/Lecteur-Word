using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    internal IReadOnlyList<SemanticSource> SelectedNativeSources {get;private set;}=[];
    int nativeSelectionPage;
    IReadOnlyList<PageRect> nativeSelectionRects=[];
    bool ShowsNativePages=>nativePageLease?.Snapshot is {Coverage:PageCoverage.Complete,Composition.Available:true} snapshot&&
        ReferenceEquals(composition,snapshot.Composition);
    internal bool ShowsHistoricalPage=>composition is {Profile:PaginatedDocument.Profile,Available:true};
    bool ShowsCalculatedPages=>ShowsNativePages||ShowsHistoricalPage;

    bool FollowNativePageLink(Point point)
    {
        if(!ShowsNativePages||nativePageLease!.Snapshot is not {Semantic:{} semantic,PageLinkTargets:{Count:>0} links} snapshot)
            return false;
        var lines=composition!.Pages[page].DisplayLines.Where(l=>l.FrameTransform is not null||
            point.Y>=l.NativeTop&&point.Y<=l.NativeTop+l.Height).ToArray();
        var targetIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var line in lines)
        {
            foreach(var (id,rect) in PageCoordinates.FieldLabels(line))
                if(rect.Contains(point.X,point.Y)&&links.TryGetValue(id,out var bookmark))targetIds.Add(bookmark);
            foreach(var cluster in PageCoordinates.Clusters(line).Where(c=>c.Rect.Contains(point.X,point.Y)))
            {
                if(line.Native?.TocRow is {} toc)
                {
                    if(toc.Hyperlink)targetIds.Add(toc.Destination.NodeId);
                    continue;
                }
                foreach(var source in cluster.Sources)
                    for(string? id=source.NodeId;id is not null;id=semantic.ParentId(id))
                        if(links.TryGetValue(id,out var target)){targetIds.Add(target);break;}
            }
        }
        if(targetIds.Count!=1||!snapshot.FieldBookmarks.TryGetValue(targetIds.Single(),out var destination))return false;
        GoToPage(destination.Page-1);
        CurrentBlockId=destination.ParagraphId;
        SelectPageParagraph(destination.ParagraphId);
        SetNativeSelection(CurrentPage,[]);
        RevealPage(page,Math.Max(0,destination.Top-32),destination.Left);
        UpdateNavigationPosition();
        return true;
    }

    void SelectNativeAt(Point point)
    {
        if(!ShowsNativePages||CurrentPage==0)return;
        var selection=composition!.Pages[page].DisplayLines.SelectMany(line=>
                PageCoordinates.Images(line).Concat(PageCoordinates.Clusters(line)))
            .Where(c=>c.Rect.Contains(point.X,point.Y)).MinBy(c=>Math.Abs(point.Y-c.Rect.Top-c.Rect.Height/2));
        SetNativeSelection(CurrentPage,selection is null?[]:[selection]);
    }

    void SetNativeSelection(int pageNumber,IReadOnlyList<PageCluster> clusters)
    {
        nativeSelectionPage=pageNumber;
        SelectedNativeSources=clusters.SelectMany(c=>c.Sources).Distinct().ToArray();
        nativeSelectionRects=clusters.Select(c=>c.Rect).ToArray();
        UpdateNativeSelection();
    }

    void UpdateNativeSelection()
    {
        foreach(var (index,visual) in pageVisuals)
        {
            bool selected=CurrentPage>0&&index+1==nativeSelectionPage&&nativeSelectionRects.Count>0;
            visual.Selection.IsVisible=selected;
            if(selected)visual.Selection.Set(nativeSelectionRects,zoom);
        }
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
                    RevealPage(page,Math.Max(0,clusters.Min(c=>c.Rect.Top)-32),clusters.Min(c=>c.Rect.Left));
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
        internal void Set(IReadOnlyList<PageRect> items,double zoom)
        {
            double requested=zoom*PointsToDip;
            if(ReferenceEquals(rectangles,items)&&scale==requested)return;
            rectangles=items;scale=requested;InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            foreach(var r in rectangles)context.FillRectangle(Fill,new Rect(r.Left*scale,r.Top*scale,r.Width*scale,r.Height*scale));
        }
    }
}
