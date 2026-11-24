using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal readonly record struct FlowColumn(long Offset,long Width);
internal sealed record FlowColumns(ImmutableArray<FlowColumn> Columns,bool Balance,SemanticSource Source)
{
    internal long Narrowest=>Columns.Min(c=>c.Width);
}
internal readonly record struct PlacedFlowRegion(int Section,int Band,int Column,PageRegion Bounds,SemanticSource Source);
internal readonly record struct PageRegionCheckpoint(string BlockId,int BlockIndex,int Sheets,int PreviousSection,int? PrintedNumber);
internal sealed record PageRegionReplay(PageRegionCheckpoint Checkpoint,IReadOnlyList<ComposedPage> Prefix);
internal sealed record FlowExclusion(string DrawingId,long Above,long InkHeight,long Below)
{
    internal long Height=>Above+InkHeight+Below;
}
internal readonly record struct WrapPoint(long X,long Y);
internal sealed record SideExclusion(long Left,long Right,long Height,string WrapText,bool AllowOverlap)
{
    internal long Top {get;init;}
    internal ImmutableArray<WrapPoint> ThroughPolygon {get;init;}=[];
    internal long DistanceLeft {get;init;}
    internal long DistanceRight {get;init;}
    internal long DistanceBottom {get;init;}
    internal long CollisionLeft {get;init;}
    internal long CollisionRight {get;init;}
    internal long CollisionHeight {get;init;}

    internal (long Left,long Right) Project(long top,long bottom)
    {
        if(ThroughPolygon.IsEmpty)return (Left,Right);
        double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity;
        top=Math.Max(0,top-DistanceBottom);
        for(int i=0;i<ThroughPolygon.Length;i++)
        {
            var a=ThroughPolygon[i];
            var b=ThroughPolygon[(i+1)%ThroughPolygon.Length];
            if(a.Y>b.Y)(a,b)=(b,a);
            long first=Math.Max(a.Y,top),last=Math.Min(b.Y,bottom);
            if(first>last)continue;
            if(a.Y==b.Y){Include(a.X);Include(b.X);continue;}
            Include(a.X+(b.X-a.X)*(double)(first-a.Y)/(b.Y-a.Y));
            Include(a.X+(b.X-a.X)*(double)(last-a.Y)/(b.Y-a.Y));
        }
        return ((long)Math.Floor(minimum)-DistanceLeft,(long)Math.Ceiling(maximum)+DistanceRight);

        void Include(double x){minimum=Math.Min(minimum,x);maximum=Math.Max(maximum,x);}
    }
}

internal static partial class PageComposer
{
    internal static bool SameColumns(FlowColumns? left,FlowColumns? right)=>
        left is null?right is null:right is not null&&left.Columns.SequenceEqual(right.Columns);

    static FlowColumns? ReadFlowColumns(SemanticDocument model,string? id,long width,bool balance,Action<string,string> refuse)
    {
        if(id is null)return null;
        var columns=model.Children(id).Where(n=>model.Name(n.Id)==SemanticIndex.QName(SemanticIndex.W,"col")).ToArray();
        string? raw=model.Attribute(id,"num");
        string? equal=model.Attribute(id,"equalWidth");
        if(equal is not (null or "1" or "on" or "true" or "0" or "off" or "false")||
            model.Attribute(id,"sep") is not (null or "0" or "off" or "false"))
            return Reject("Séparateur vertical ou déclaration booléenne hors profil.");
        bool uniform=equal is not ("0" or "off" or "false");
        long? count=raw is null?(uniform?1:columns.Length):Number(id,"num",1,45);
        if(count is null or <1 or >45||width<=0)return Reject("Nombre de colonnes ou largeur du corps invalide.");
        if(uniform&&count==1)return null;
        var result=ImmutableArray.CreateBuilder<FlowColumn>((int)count.Value);
        long x=0;
        if(uniform)
        {
            long? gap=Distance(id,"space",720);
            if(gap is not {} g||width-g*(count.Value-1)<count.Value*12*TypographySession.EmuPerPoint)
                return Reject("Largeur insuffisante pour les colonnes et leurs espacements.");
            long columnWidth=(width-g*(count.Value-1))/count.Value;
            for(int i=0;i<count;i++)
            {
                result.Add(new(x,columnWidth));
                x+=columnWidth+g;
            }
        }
        else
        {
            if(columns.Length!=count)return Reject("Nombre de définitions de colonnes incompatible.");
            for(int i=0;i<columns.Length;i++)
            {
                string column=columns[i].Id;
                long? w=Distance(column,"w",null);
                long? gap=Distance(column,"space",0);
                if(w is not {} size||size<12*TypographySession.EmuPerPoint||gap is null||x+size>width)
                    return Reject("Largeur ou espacement de colonne hors du corps physique.");
                result.Add(new(x,size));
                x+=size+(i+1<columns.Length?gap.Value:0);
            }
        }
        return new(result.MoveToImmutable(),balance,model.Source(id));

        long? Number(string node,string key,long min,long max)=>
            long.TryParse(model.Attribute(node,key),System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,out long n)&&n>=min&&n<=max?n:null;
        long? Distance(string node,string key,long? fallback)=>
            (model.Attribute(node,key) is null?fallback:Number(node,key,0,28_800))*LinePropertiesReader.EmuPerTwip;
        FlowColumns? Reject(string reason)
        {
            refuse("PAGE_COLUMN_GEOMETRY_UNQUALIFIED",reason);
            return null;
        }
    }
}
