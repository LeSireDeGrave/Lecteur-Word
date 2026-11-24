using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal enum MetafilePathVerb { Move, Line, Cubic, Close }
internal readonly record struct MetafilePathCommand(MetafilePathVerb Verb,MetafilePoint Point,
    MetafilePoint Second=default,MetafilePoint Third=default);
internal sealed record MetafilePath(ImmutableArray<MetafilePathCommand> Commands,bool Winding);
internal sealed record MetafileFill(MetafilePath Path,uint Rgb,MetafilePath? Clip);

internal static partial class MetafileReader
{
    // MS-EMF 2.3.3.4.3. Accept a bounded alternate representation only with
    // the native path stream that follows. No decoder sees the alternate bytes.
    static bool IsBoundedAlternative(ReadOnlySpan<byte> values)
    {
        if(values.Length<56||U32(values,0)>values.Length-4||values.Length-4-U32(values,0)>3||
            U32(values,4)!=0x43494447||U32(values,8)!=0x40000004||U32(values,28)!=1)
            return false;
        return U32(values,32)==0x50444620&&U32(values,36)==1&&U32(values,40)>=5&&
            U32(values,44)==44&&44L+U32(values,40)==U32(values,0)&&values[48..53].SequenceEqual("%PDF-"u8);
    }

    // Same bounded EMF record loop as the line profile. This state accepts
    // axis-aligned window mapping, solid brushes, paths and a replaceable clip.
    private sealed class EmfPathPlayback(int left,int top,int right,int bottom,int handles)
    {
        readonly uint?[] brushes=new uint?[handles];
        readonly ImmutableArray<MetafileFill>.Builder fills=ImmutableArray.CreateBuilder<MetafileFill>();
        readonly List<MetafilePathCommand> commands=[];
        MetafilePoint? windowOrigin,viewportOrigin,windowSize,viewportSize;
        MetafilePath? ready,clip;
        bool drawing,figure,winding;
        int points;
        uint brushIndex=0x80000000,brushColor=0xffffff;

        internal void Apply(uint function,ReadOnlySpan<byte> values)
        {
            switch(function)
            {
                case 10 when values.Length==8&&windowOrigin is null&&points==0:
                    windowOrigin=Point32(values);
                    return;
                case 12 when values.Length==8&&viewportOrigin is null&&points==0:
                    viewportOrigin=Point32(values);
                    return;
                case 9 when values.Length==8&&windowSize is null&&points==0:
                    windowSize=Point32(values);
                    return;
                case 11 when values.Length==8&&viewportSize is null&&points==0:
                    viewportSize=Point32(values);
                    return;
                case 22 when values.Length==4&&U32(values,0)==24: // text alignment
                case 18 when values.Length==4&&U32(values,0) is 1 or 2: // background mode
                case 21 when values.Length==4&&U32(values,0) is >=1 and <=4: // bitmap stretch
                case 13 when values.Length==8: // brush origin, inert for solid brushes
                    return;
                case 75 when values.Length==8&&U32(values,0)==0&&U32(values,4)==5&&!drawing&&ready is null:
                    clip=null;
                    return;
                case 19 when values.Length==4&&U32(values,0) is 1 or 2&&!drawing&&ready is null:
                    winding=U32(values,0)==2;
                    return;
                case 39 when values.Length==16:
                    CreateBrush(values);
                    return;
                case 37 when values.Length==4:
                    SelectBrush(U32(values,0));
                    return;
                case 40 when values.Length==4:
                    uint removed=U32(values,0);
                    if(removed==0||removed>=brushes.Length||removed==brushIndex||brushes[removed] is null)
                        throw Invalid("Suppression d’un pinceau EMF absent ou sélectionné.");
                    brushes[removed]=null;
                    return;
                case 59 when values.IsEmpty&&!drawing&&ready is null:
                    ValidateMapping();
                    drawing=true;
                    return;
                case 27 when values.Length==8&&drawing:
                    var point=Point32(values);
                    Reserve(1);
                    CheckPoint(point);
                    commands.Add(new(MetafilePathVerb.Move,point));
                    figure=true;
                    return;
                case 88 or 89 when values.Length>=20&&drawing&&figure:
                    AppendPoints(values,function==88);
                    return;
                case 61 when values.IsEmpty&&drawing&&figure:
                    commands.Add(new(MetafilePathVerb.Close,default));
                    figure=false;
                    return;
                case 60 when values.IsEmpty&&drawing&&commands.Count>=2:
                    ready=new([..commands],winding);
                    commands.Clear();
                    drawing=figure=false;
                    return;
                case 67 when values.Length==4&&U32(values,0)==5:
                    clip=TakePath();
                    return;
                case 62 when values.Length==16&&I32(values,0)<=I32(values,8)&&I32(values,4)<=I32(values,12):
                    fills.Add(new(TakePath(),brushColor,clip));
                    return;
                default:
                    throw Invalid($"Enregistrement EMF {function} hors profil de tracé rempli ou non conforme.");
            }
        }

        internal MetafilePicture Finish()
        {
            ValidateMapping();
            if(drawing||ready is not null||fills.Count==0)
                throw Invalid("Tracé EMF incomplet ou aucun remplissage visible.");
            var size=windowSize!.Value;
            return new(0,0,size.X,size.Y,[]){Fills=fills.ToImmutable()};
        }

        void ValidateMapping()
        {
            if(left!=0||top!=0||windowOrigin is not {X:0,Y:0}||viewportOrigin is not {X:0,Y:0}||
                windowSize is not {X:>0 and <=32768,Y:>0 and <=32768}||
                viewportSize is not {} viewport||viewport.X!=right+1L||viewport.Y!=bottom+1L)
                throw Invalid("Fenêtre de tracé ou projection EMF hors profil.");
        }

        void CreateBrush(ReadOnlySpan<byte> values)
        {
            uint index=U32(values,0),color=U32(values,8);
            if(index==0||index>=brushes.Length||brushes[index] is not null||
                U32(values,4)!=0||color>0xffffff||U32(values,12)!=0)
                throw Invalid("Pinceau EMF hors profil.");
            brushes[index]=((color&0xff)<<16)|(color&0xff00)|(color>>16);
        }

        void SelectBrush(uint index)
        {
            if(index==0x80000000)brushColor=0xffffff;
            else if(index==0x80000004)brushColor=0;
            else if(index!=0&&index<brushes.Length&&brushes[index] is {} color)brushColor=color;
            else throw Invalid("Sélection d’un pinceau EMF hors profil.");
            brushIndex=index;
        }

        void AppendPoints(ReadOnlySpan<byte> values,bool cubic)
        {
            uint count=U32(values,16);
            if(count==0||20+4L*count!=values.Length||cubic&&count%3!=0)
                throw Invalid("Tableau de points de tracé EMF invalide.");
            Reserve((int)count);
            var data=values[20..];
            int stride=cubic?12:4;
            for(int i=0;i<data.Length;i+=stride)
            {
                var first=ReadPoint(data,i);
                commands.Add(cubic?new(MetafilePathVerb.Cubic,first,ReadPoint(data,i+4),ReadPoint(data,i+8)):
                    new(MetafilePathVerb.Line,first));
            }
        }

        MetafilePoint ReadPoint(ReadOnlySpan<byte> data,int offset)
        {
            var point=new MetafilePoint(I16(data,offset),I16(data,offset+2));
            CheckPoint(point);
            return point;
        }

        void CheckPoint(MetafilePoint point)
        {
            var size=windowSize!.Value;
            if(point.X < -size.X||point.X>2*size.X||point.Y < -size.Y||point.Y>2*size.Y)
                throw Invalid("Point de tracé EMF hors limites.");
        }

        void Reserve(int length)
        {
            if(length>MaxPoints-points)throw Invalid("Budget de points du tracé EMF dépassé.");
            points+=length;
        }

        MetafilePath TakePath()
        {
            var path=ready??throw Invalid("Opération EMF sans tracé terminé.");
            ready=null;
            return path;
        }

        static MetafilePoint Point32(ReadOnlySpan<byte> values)=>new(I32(values,0),I32(values,4));
    }
}
