using System.Security.Cryptography;
using SkiaSharp;

namespace LecteurWord.Flow;

public static partial class Composer
{
    // The drawing painter remains shared with the reader and the PDF exporter.
    internal static InlineImage? PublicationImage(InlineImage original,LineComposer? painter,CancellationToken token)
    {
        return Render(original.ResourceId,original.Source,new(0,0,original.Width,original.Height),
            canvas=>
            {
                var bitmaps=new Dictionary<string,SKBitmap>(StringComparer.Ordinal);
                try{DrawImage(canvas,bitmaps,original,0,0,painter);}
                finally{foreach(var bitmap in bitmaps.Values)bitmap.Dispose();}
            },original.Decorative,token);
    }

    internal static InlineImage? PublicationDrawing(ComposedPage page,string id,IReadOnlyList<PlacedFloatingPicture> pictures,
        IReadOnlyList<PlacedFloatingTextFrame> frames,CancellationToken token)
    {
        var boxes=pictures.Select(p=>new PageRect(p.X,p.Y,p.Picture.Image.Width,p.Picture.Image.Height))
            .Concat(frames.Select(f=>
            {
                float border=(f.Frame.Stroke?.Width??0)/2;
                return new PageRect(f.Frame.X-border,f.Frame.Y-border,f.Frame.Width+2*border,f.Frame.Height+2*border);
            })).Concat(frames.Where(f=>f.HasTextOverflow&&f.Transform is not null).Select(f=>f.Transform!.Value.Bounds(
                new(0,0,f.Content.Geometry.Width,f.Content.Geometry.Height)))).ToArray();
        if(boxes.Length==0)return null;
        float left=boxes.Min(b=>b.Left),top=boxes.Min(b=>b.Top),right=boxes.Max(b=>b.Left+b.Width),bottom=boxes.Max(b=>b.Top+b.Height);
        var group=page with{FloatingPictures=pictures,FloatingTextFrames=frames};
        return Render(id,pictures.FirstOrDefault()?.Picture.Image.Source??frames[0].Frame.Drawing,
            new(left,top,right-left,bottom-top),canvas=>
            {
                DrawFloatingDrawings(canvas,group,behind:true,clipToSheet:false);
                DrawFloatingDrawings(canvas,group,behind:false,clipToSheet:false);
            },pictures.Count>0&&frames.Count==0&&pictures.All(p=>p.Picture.Image.Decorative==true)?true:null,token);
    }

    static InlineImage? Render(string id,LecteurWord.DocxCore.SourceLocation source,PageRect box,Action<SKCanvas> paint,
        bool? decorative,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!float.IsFinite(box.Width)||!float.IsFinite(box.Height)||box.Width<=0||box.Height<=0)return null;
        const int maxDimension=1600;
        float scale=Math.Min(2,maxDimension/Math.Max(box.Width,box.Height));
        for(int pass=0;pass<2;pass++,scale*=.7f)
        {
            token.ThrowIfCancellationRequested();
            int width=Math.Max(1,(int)MathF.Ceiling(box.Width*scale)),height=Math.Max(1,(int)MathF.Ceiling(box.Height*scale));
            using var surface=SKSurface.Create(new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Premul));
            if(surface is null)return null;
            var canvas=surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-box.Left,-box.Top);
            paint(canvas);
            token.ThrowIfCancellationRequested();
            using var image=surface.Snapshot();
            using var data=image.Encode(SKEncodedImageFormat.Png,100);
            if(data is null||data.Size>4*1024*1024)continue;
            byte[] bytes=data.ToArray();
            return new InlineImage("publication:"+id,source,box.Width,box.Height,width,height,
                Convert.ToHexStringLower(SHA256.HashData(bytes)))
            {RenderBytes=bytes,Decorative=decorative,OriginalPixelWidth=width,OriginalPixelHeight=height};
        }
        return null;
    }
}
