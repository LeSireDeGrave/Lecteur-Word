using System.Buffers.Binary;
using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal readonly record struct MetafilePoint(int X,int Y);
internal readonly record struct MetafilePen(int Width,uint Rgb);
internal sealed record MetafileStroke(ImmutableArray<MetafilePoint> Points,MetafilePen Pen)
{
    internal int Order {get;init;}
}
internal sealed record MetafileText(string Outline,uint Rgb,int Order,string RequestedFace,string UsedFace,bool SimulatedItalic);

internal sealed record MetafilePicture(int Left,int Top,int Width,int Height,
    ImmutableArray<MetafileStroke> Strokes)
{
    internal ImmutableArray<MetafileFill> Fills {get;init;}=[];
    internal ImmutableArray<MetafileText> Texts {get;init;}=[];
    internal ushort? BaselineSixteenths {get;init;}
}

internal static partial class MetafileReader
{
    const int MaxRecords=4096,MaxPoints=32768;

    internal static bool IsMetafile(string contentType) =>
        contentType is "image/wmf" or "image/x-wmf" or "image/emf" or "image/x-emf";

    internal static MetafilePicture Read(ReadOnlySpan<byte> data,string type,CancellationToken token) =>
        type is "image/wmf" or "image/x-wmf"?ReadWmf(data,token):ReadEmf(data,token);

    static MetafilePicture ReadWmf(ReadOnlySpan<byte> data,CancellationToken token)
    {
        // MS-WMF 2.3.2.3 and 2.3.2.2. The placeable box supplies explicit playback bounds.
        const int placeable=22,header=18;
        if(data.Length<placeable+header||U32(data,0)!=0x9ac6cdd7||U16(data,4)!=0||U16(data,14)==0||U32(data,16)!=0)
            throw Invalid("En-tête WMF placeable absent ou invalide.");
        ushort checksum=0;
        for(int i=0;i<20;i+=2)checksum^=U16(data,i);
        if(checksum!=U16(data,20))throw Invalid("Somme de contrôle de l’en-tête WMF incohérente.");
        int left=I16(data,6),top=I16(data,8),right=I16(data,10),bottom=I16(data,12);
        var picture=new Builder(left,top,right,bottom);
        data=data[placeable..];
        uint largest=U32(data,12);
        int objectCount=U16(data,10);
        if(U16(data,0) is not (1 or 2)||U16(data,2)!=9||U16(data,4) is not (0x100 or 0x300)||
            2L*U32(data,6)!=data.Length||objectCount>256||U16(data,16)!=0||largest<3)
            throw Invalid("En-tête WMF ou table d’objets hors profil.");
        using var text=new WmfTextPlayback(token);
        var objects=new object?[objectCount];
        int selectedPen=-1,selectedFont=-1;
        var comments=new WmfComments(right-left,bottom-top);
        int count=0;
        uint largestSeen=0;
        for(int cursor=header;cursor<data.Length;)
        {
            token.ThrowIfCancellationRequested();
            if(++count>MaxRecords||data.Length-cursor<6)throw Invalid("Limite ou enregistrement tronqué dans le WMF.");
            uint words=U32(data,cursor);
            if(words<3||2L*words>data.Length-cursor)throw Invalid("Taille d’enregistrement WMF invalide.");
            largestSeen=Math.Max(largestSeen,words);
            ushort function=U16(data,cursor+4);
            var values=data.Slice(cursor+6,(int)(2*words)-6);
            cursor+=(int)(2*words);
            picture.Order=count;
            switch(function)
            {
                case 0x0000 when values.IsEmpty&&cursor==data.Length&&largestSeen==largest:
                    if(!comments.Complete)throw Invalid("Enveloppe WMF de commentaires d’application incomplète.");
                    return picture.Finish(text.Outlines) with{BaselineSixteenths=comments.Baseline};
                case 0x0103 when values.Length==2&&U16(values,0)==1: // MM_TEXT
                    break;
                case 0x020b when values.Length==4&&I16(values,2)==left&&I16(values,0)==top:
                case 0x020c when values.Length==4&&I16(values,2)==right-left&&I16(values,0)==bottom-top:
                    // Same logical window as the validated placeable frame.
                    break;
                case 0x02fa when values.Length==10:
                    // MS-WMF 2.3.4.5 and 3.1.4.1. Solid pen, assigned to the lowest free slot.
                    int index=Array.IndexOf(objects,null);
                    int width=I16(values,2);
                    if(index<0||U16(values,0)!=0||width is <0 or >512||U16(values,4)!=0||!WmfRgb(U32(values,6),out uint rgb))
                        throw Invalid("Stylet WMF hors profil ou table d’objets pleine.");
                    objects[index]=new MetafilePen(Math.Max(1,width),rgb);
                    break;
                case 0x02fb when values.Length==50:
                    int slot=Array.IndexOf(objects,null);
                    if(slot<0)throw Invalid("Table d’objets WMF pleine.");
                    objects[slot]=WmfTextPlayback.ReadFont(values);
                    break;
                case 0x012d when values.Length==2:
                    int selected=U16(values,0);
                    if(selected>=objects.Length||objects[selected] is null)
                        throw Invalid("Sélection d’un objet WMF absent ou hors profil.");
                    if(objects[selected] is MetafilePen pen){selectedPen=selected;picture.Pen=pen;}
                    else {selectedFont=selected;text.Font=(WmfLogicalFont)objects[selected]!;}
                    break;
                case 0x01f0 when values.Length==2:
                    int removed=U16(values,0);
                    if(removed>=objects.Length||objects[removed] is null||removed==selectedPen||removed==selectedFont)
                        throw Invalid("Suppression d’un objet WMF absent ou encore sélectionné.");
                    objects[removed]=null;
                    break;
                case 0x0102 when values.Length is 2 or 4&&U16(values,0) is 1 or 2&&
                    (values.Length==2||U16(values,2)==0):
                    text.Transparent=U16(values,0)==1;
                    break;
                case 0x0201 when values.Length==4:
                    break;
                case 0x0209 when values.Length==4:
                    text.Color=U32(values,0);
                    break;
                case 0x012e when values.Length is 2 or 4&&(values.Length==2||U16(values,2)==0):
                    text.Alignment=U16(values,0);
                    break;
                case 0x0214 when values.Length==4:
                    picture.Move(new(I16(values,2),I16(values,0)));
                    break;
                case 0x0213 when values.Length==4:
                    picture.Line(new(I16(values,2),I16(values,0)));
                    break;
                case 0x0325 when values.Length>=2:
                    int points=U16(values,0);
                    if(points<2||values.Length!=2+4*points)throw Invalid("Polyligne WMF mal dimensionnée.");
                    picture.Reserve(points);
                    var stroke=ImmutableArray.CreateBuilder<MetafilePoint>(points);
                    for(int i=0;i<points;i++)stroke.Add(new(I16(values,2+4*i),I16(values,4+4*i)));
                    picture.Add(stroke.MoveToImmutable());
                    break;
                case 0x0a32:
                    text.Place(values,picture);
                    break;
                case 0x0626 when values.Length>=8&&U16(values,0)==15&&U16(values,2)>=4&&
                    values.Length==4+((U16(values,2)+1)&~1):
                    if(comments.Accept(values.Slice(4,U16(values,2)),text.HasDrawn))break;
                    throw Invalid($"Enregistrement WMF 0x0626, commentaire 0x{U32(values,4):X8} hors du profil passif.");
                case 0x0626 when values.Length>=2:
                    throw Invalid($"Enregistrement WMF 0x0626, échappement 0x{U16(values,0):X4} hors du profil passif.");
                default:
                    throw Invalid($"Enregistrement WMF 0x{function:X4} hors profil ou non conforme.");
            }
        }
        throw Invalid("Fin WMF absente.");
    }

    // PALETTERGB and RGB have the same value on this direct-colour device.
    // Palette selection and palette-index colours are outside the admitted record set.
    static bool WmfRgb(uint color,out uint rgb)
    {
        rgb=((color&0xff)<<16)|(color&0xff00)|((color>>16)&0xff);
        return color>>24 is 0 or 2;
    }

    static MetafilePicture ReadEmf(ReadOnlySpan<byte> data,CancellationToken token)
    {
        // The three standard header lengths are supported without an OpenGL pixel format or description.
        const int baseHeader=88;
        if(data.Length<baseHeader||U32(data,0)!=1||U32(data,4) is not (88 or 100 or 108)||
            U32(data,4)>data.Length||U32(data,40)!=0x464d4520||
            U32(data,44)!=0x00010000||U32(data,48)!=data.Length||U16(data,56) is <1 or >256||U16(data,58)!=0||
            U32(data,60)!=0||U32(data,64)!=0||U32(data,68)!=0||
            I32(data,32)<=I32(data,24)||I32(data,36)<=I32(data,28)||
            I32(data,72)<=0||I32(data,76)<=0||I32(data,80)<=0||I32(data,84)<=0)
            throw Invalid("En-tête EMF, cadre ou table d’objets hors profil.");
        int header=(int)U32(data,4);
        if(header>=100&&(U32(data,88)!=0||U32(data,92)!=0||U32(data,96)!=0)||
            header==108&&(I32(data,100)<0||I32(data,104)<0))
            throw Invalid("Descripteur de pixels, OpenGL ou dimensions du périphérique hors profil.");
        double horizontal=I32(data,72)/(header==108&&I32(data,100)>0?I32(data,100)/10d:100d*I32(data,80));
        double vertical=I32(data,76)/(header==108&&I32(data,104)>0?I32(data,104)/10d:100d*I32(data,84));
        if(Math.Abs(I32(data,24)*horizontal-I32(data,8))>1||Math.Abs(I32(data,28)*vertical-I32(data,12))>1||
            !FrameEnd(I32(data,32)*horizontal,I32(data,16))||!FrameEnd(I32(data,36)*vertical,I32(data,20)))
            throw Invalid("Le cadre physique EMF ne correspond pas aux limites du périphérique.");
        var picture=new Builder(I32(data,8),I32(data,12),I32(data,16)+1L,I32(data,20)+1L);
        int count=1;
        EmfPathPlayback? paths=null;
        bool alternative=false;
        for(int cursor=header;cursor<data.Length;)
        {
            token.ThrowIfCancellationRequested();
            if(++count>MaxRecords||data.Length-cursor<8)throw Invalid("Limite ou enregistrement tronqué dans l’EMF.");
            uint function=U32(data,cursor),size=U32(data,cursor+4);
            if(size<8||size%4!=0||size>data.Length-cursor)throw Invalid("Taille d’enregistrement EMF invalide.");
            var values=data.Slice(cursor+8,(int)size-8);
            cursor+=(int)size;
            if(function==14&&values.Length==12&&cursor==data.Length&&count==U32(data,52)&&
                U32(values,0)==0&&U32(values,4) is 0 or 16&&U32(values,8)==size)
                return paths?.Finish()??(alternative?throw Invalid("Alternative EMF sans tracés qualifiés."):picture.Finish());
            if(count==2&&function==70&&IsBoundedAlternative(values))
            {
                alternative=true;
                continue;
            }
            if(count==(alternative?3:2)&&function==17&&values.Length==4&&I32(values,0)==8)
            {
                paths=new EmfPathPlayback(I32(data,8),I32(data,12),I32(data,16),I32(data,20),U16(data,56));
                continue;
            }
            if(paths is not null)
            {
                paths.Apply(function,values);
                continue;
            }
            switch(function)
            {
                case 17 when values.Length==4&&I32(values,0)==1:
                    break;
                case 27 when values.Length==8:
                    picture.Move(new(I32(values,0),I32(values,4)));
                    break;
                case 54 when values.Length==8:
                    picture.Line(new(I32(values,0),I32(values,4)));
                    break;
                case 4 when values.Length>=20:
                    uint points=U32(values,16);
                    if(points<2||20+8L*points!=values.Length)throw Invalid("Polyligne EMF mal dimensionnée.");
                    picture.Reserve((int)points);
                    var stroke=ImmutableArray.CreateBuilder<MetafilePoint>((int)points);
                    int left=I32(values,0),top=I32(values,4),right=I32(values,8),bottom=I32(values,12);
                    for(int i=0;i<points;i++)
                    {
                        var point=new MetafilePoint(I32(values,20+i*8),I32(values,24+i*8));
                        if(point.X<left||point.X>right||point.Y<top||point.Y>bottom)
                            throw Invalid("Point hors des limites de la polyligne EMF.");
                        stroke.Add(point);
                    }
                    picture.Add(stroke.MoveToImmutable());
                    break;
                default:
                    throw Invalid($"Enregistrement EMF {function} hors profil ou non conforme.");
            }
        }
        throw Invalid("Fin EMF absente.");

        static bool FrameEnd(double physical,int limit)=>physical>=limit-1d&&physical<=limit+2d;
    }

    sealed class Builder
    {
        readonly int left,top,width,height;
        readonly ImmutableArray<MetafileStroke>.Builder strokes=ImmutableArray.CreateBuilder<MetafileStroke>();
        MetafilePoint? current;
        int points;
        internal MetafilePen Pen {get;set;}=new(1,0);
        internal int Order {get;set;}
        internal MetafilePoint? Current=>current;
        internal void UpdateTextPosition(MetafilePoint point)=>current=point;

        internal Builder(int left,int top,long right,long bottom)
        {
            if(right-left is <1 or >32768||bottom-top is <1 or >32768)
                throw Invalid("Cadre du métafichier hors limites.");
            this.left=left;
            this.top=top;
            width=(int)(right-left);
            height=(int)(bottom-top);
        }

        internal void Move(MetafilePoint point)
        {
            Check(point);
            current=point;
        }

        internal void Line(MetafilePoint point)
        {
            if(current is not {} previous)throw Invalid("Ligne sans origine explicite.");
            Reserve(2);
            Add([previous,point]);
            current=point;
        }

        internal void Reserve(int count)
        {
            if(count>MaxPoints-points)throw Invalid("Budget de points du métafichier dépassé.");
            points+=count;
        }

        internal void Add(ImmutableArray<MetafilePoint> stroke)
        {
            foreach(var point in stroke)Check(point);
            strokes.Add(new(stroke,Pen){Order=Order});
        }

        internal void CheckTextInk(float x0,float y0,float x1,float y1)
        {
            const int overhang=4096;
            if(!(x0>=left-overhang&&y0>=top-overhang&&x1<=left+width+overhang&&y1<=top+height+overhang&&
                x0<left+width&&x1>left&&y0<top+height&&y1>top))
                throw Invalid("Glyphe WMF sans intersection avec le cadre ou dépassement du budget de contour.");
        }

        internal MetafilePicture Finish(ImmutableArray<MetafileText> texts=default)
        {
            if(strokes.Count==0&&texts.IsDefaultOrEmpty)throw Invalid("Aucun tracé visible dans le métafichier.");
            return new(left,top,width,height,strokes.ToImmutable()){Texts=texts.IsDefault?[]:texts};
        }

        void Check(MetafilePoint point)
        {
            if((long)point.X-left<0||(long)point.X-left>=width||(long)point.Y-top<0||(long)point.Y-top>=height)
                throw Invalid("Point hors du cadre du métafichier.");
        }
    }

    static ushort U16(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    static short I16(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    static uint U32(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    static int I32(ReadOnlySpan<byte> data,int offset)=>BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    static InvalidDataException Invalid(string message)=>new(message);
}
