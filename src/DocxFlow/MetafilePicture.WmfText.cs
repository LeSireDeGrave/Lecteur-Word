using System.Collections.Immutable;
using System.Text;
using SkiaSharp;

namespace LecteurWord.Flow;

internal static partial class MetafileReader
{
    sealed record WmfLogicalFont(string Family,short Height,short Width,short Escapement,short Orientation,
        short Weight,byte Italic,byte Underline,byte Strike,byte Charset);

    sealed class WmfTextPlayback(CancellationToken token):IDisposable
    {
        readonly FontResolver.FaceSelection faces=new(new(),token.ThrowIfCancellationRequested);
        readonly ImmutableArray<MetafileText>.Builder outlines=ImmutableArray.CreateBuilder<MetafileText>();
        int characters,outlineBytes;
        internal WmfLogicalFont? Font {get;set;}
        internal uint Color {get;set;}
        internal ushort Alignment {get;set;}
        internal bool Transparent {get;set;}
        internal ImmutableArray<MetafileText> Outlines=>outlines.ToImmutable();
        internal bool HasDrawn=>outlines.Count!=0;

        // MS-WMF §2.2.1.2. The font object is validated again when selected for actual output.
        internal static WmfLogicalFont ReadFont(ReadOnlySpan<byte> data)
        {
            var name=data[18..50];
            int end=name.IndexOf((byte)0);
            if(end<=0||!Printable(name[..end]))throw Invalid("Nom de fonte WMF absent ou non qualifié.");
            return new(Encoding.ASCII.GetString(name[..end]),I16(data,0),I16(data,2),I16(data,4),I16(data,6),
                I16(data,8),data[10],data[11],data[12],data[13]);
        }

        internal void Place(ReadOnlySpan<byte> record,Builder picture)
        {
            if(record.Length<8||!Transparent||Alignment is not (24 or 25)||!WmfRgb(Color,out uint rgb)||
                Font is not {Height:>=-4096 and <0,Width:0,Escapement:0,Orientation:0,Weight:400 or 700,
                    Italic:0 or 1,Underline:0,Strike:0,Charset:0 or 1 or 2} logical||U16(record,6)!=0)
                throw Invalid("État ou options EXTTEXTOUT WMF hors profil.");
            int length=U16(record,4);
            int advanceStart=8+((length+1)&~1);
            if(length==0||length>4096-characters||record.Length!=advanceStart+length*2)
                throw Invalid("Longueur du texte ou nombre d’avances EXTTEXTOUT WMF hors profil.");
            var bytes=record.Slice(8,length);
            bool symbol=logical.Charset==2||logical.Charset==1&&logical.Family.Equals("Symbol",StringComparison.OrdinalIgnoreCase);
            if(!symbol&&!Printable(bytes))throw Invalid("Encodage de caractères EXTTEXTOUT WMF hors profil.");
            var origin=Alignment==25?picture.Current??throw Invalid("Position courante WMF absente."):
                new MetafilePoint(I16(record,2),I16(record,0));
            bool bold=logical.Weight>=600,italic=logical.Italic!=0;
            var resolution=faces.ExactInstalled(logical.Family,bold,italic);
            bool namedSymbol=false,simulated=false;
            if(resolution is null&&symbol&&logical.Family.Equals("Symbol",StringComparison.OrdinalIgnoreCase)&&!bold)
            {
                resolution=faces.ExactInstalled("Standard Symbols PS",false,italic);
                if(resolution is null&&italic)
                {
                    resolution=faces.ExactInstalled("Standard Symbols PS",false,false);
                    simulated=resolution is not null;
                }
                namedSymbol=resolution is not null;
            }
            if(resolution is null&&!symbol&&Compatible(logical.Family) is {} compatible)
                resolution=faces.ExactInstalled(compatible,bold,italic);
            if(resolution is null&&logical.Family.Equals("DejaVu Serif",StringComparison.OrdinalIgnoreCase))
                resolution=faces.Bundled(bold,italic);
            if(resolution is null)throw Invalid("Face exacte du texte WMF indisponible : "+logical.Family+".");
            using var face=FontResolver.Open(resolution);
            if(face.GetTableSize(0x66766172)>0)throw Invalid("Variation de fonte WMF hors profil.");
            var cmap=symbol&&!namedSymbol&&face.GetTableSize(0x636d6170) is >0 and <=1_048_576?
                face.GetTableData(0x636d6170)??[]:[];
            using var font=new SKFont(face,-logical.Height){Hinting=SKFontHinting.None,LinearMetrics=true,Subpixel=true};
            var names=namedSymbol?ReadSymbolNames(resolution,bytes):null;
            using var drawn=new SKPath();
            int x=origin.X;
            for(int i=0;i<bytes.Length;i++)
            {
                token.ThrowIfCancellationRequested();
                ushort glyph;
                if(names is not null)glyph=names[i];
                else if(symbol)
                {
                    var mapping=SymbolCmap.Lookup(cmap,bytes[i],face.GlyphCount,token.ThrowIfCancellationRequested);
                    if(mapping.Status!=SymbolCmap.Status.Present)throw Invalid("Table symbolique de la fonte WMF indisponible.");
                    glyph=mapping.Glyph;
                }
                else glyph=font.GetGlyph(bytes[i]);
                if(glyph==0)throw Invalid($"Glyphe WMF manquant dans {logical.Family} pour l’octet 0x{bytes[i]:X2}.");
                using var contour=font.GetGlyphPath(glyph);
                if(contour is {IsEmpty:false})
                {
                    // GDI-compatible default shear as exercised by Wine's FreeType backend.
                    if(simulated)contour.Transform(new SKMatrix{ScaleX=1,ScaleY=1,SkewX=-.25f,Persp2=1});
                    var bounds=contour.TightBounds;
                    picture.CheckTextInk(x+bounds.Left,origin.Y+bounds.Top,x+bounds.Right,origin.Y+bounds.Bottom);
                    picture.Reserve(contour.PointCount);
                    drawn.AddPath(contour,x,origin.Y);
                }
                else if(bytes[i]!=0x20)throw Invalid("Contour de glyphe WMF indisponible.");
                x+=I16(record,advanceStart+i*2);
            }
            characters+=length;
            if(Alignment==25)picture.UpdateTextPosition(new(x,origin.Y));
            if(drawn.IsEmpty)return;
            string svg=drawn.ToSvgPathData();
            if(svg.Length>512_000-outlineBytes)throw Invalid("Budget de contours texte WMF dépassé.");
            outlineBytes+=svg.Length;
            outlines.Add(new(svg,rgb,picture.Order,logical.Family,resolution.UsedFamily,simulated));
        }

        static string? Compatible(string family)=>family.ToUpperInvariant() switch
        {
            "TIMES NEW ROMAN"=>"Liberation Serif","ARIAL"=>"Liberation Sans","COURIER NEW"=>"Liberation Mono",_=>null
        };

        unsafe ushort[] ReadSymbolNames(FontResolution resolution,ReadOnlySpan<byte> bytes)
        {
            var data=resolution.Data??throw Invalid("Face symbolique WMF sans instantané.");
            fixed(byte* pointer=data.Bytes)
            {
                using var blob=new HarfBuzzSharp.Blob((nint)pointer,data.Bytes.Length,HarfBuzzSharp.MemoryMode.Duplicate);
                using var face=new HarfBuzzSharp.Face(blob,data.Index);
                using var font=new HarfBuzzSharp.Font(face);
                font.SetFunctionsOpenType();
                var glyphs=new ushort[bytes.Length];
                for(int i=0;i<bytes.Length;i++)
                {
                    token.ThrowIfCancellationRequested();
                    if(LegacySymbols.PostScriptGlyphName(bytes[i]) is not {} name||
                        !font.TryGetGlyphFromName(name,out uint glyph)||glyph==0||glyph>=face.GlyphCount||glyph>ushort.MaxValue)
                        throw Invalid($"Glyphe Adobe Symbol indisponible pour l’octet 0x{bytes[i]:X2}.");
                    glyphs[i]=(ushort)glyph;
                }
                return glyphs;
            }
        }

        static bool Printable(ReadOnlySpan<byte> bytes)
        {
            foreach(byte value in bytes)if(value is <0x20 or >0x7e)return false;
            return true;
        }

        public void Dispose()=>faces.Dispose();
    }
}
