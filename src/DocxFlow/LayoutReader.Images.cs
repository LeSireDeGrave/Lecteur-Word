using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        readonly Dictionary<string,RasterResource> renderImages=[];long renderPixels;
        readonly Dictionary<string,MetafilePicture> renderMetafiles=[];
        const string WP="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",D="http://schemas.openxmlformats.org/drawingml/2006/main",PIC="http://schemas.openxmlformats.org/drawingml/2006/picture";
        const string Adec="http://schemas.microsoft.com/office/drawing/2017/decorative";
        const string A16="http://schemas.microsoft.com/office/drawing/2014/main";
        static readonly Guid DpiExtension=new("28A0092B-C50C-407E-A947-70E740481C1C"),DecorativeExtension=new("C183D7F6-B498-43B3-948B-1728B52AA6E4");
        static readonly Guid CreationExtension=new("FF2B5EF4-FFF2-40B4-BE49-F238E27FC236");
        IEnumerable<InventoryNode> Descendants(InventoryNode node)
        {
            var stack=new Stack<InventoryNode>();stack.Push(node);
            while(stack.Count>0){token.ThrowIfCancellationRequested();var current=stack.Pop();yield return current;foreach(var child in children[current.Id].Reverse())stack.Push(child);}
        }
        InlineImage? ReadInlineImage(InventoryNode drawing,IReadOnlySet<string>? handledAnchorNodes=null)
        {
            int before=issues.Count;WAttributes(drawing);var all=Descendants(drawing).ToArray();
            var inline=Elements(drawing).ToArray();if(inline.Length!=1||
                inline[0].Name!=(handledAnchorNodes is null?"{"+WP+"}inline":"{"+WP+"}anchor"))
            {Issue("LAYOUT_FLOATING_IMAGE_UNQUALIFIED",drawing,"Image flottante ou dessin hors profil ; aucune conversion silencieuse en image intégrée.");return null;}
            string ParentName(InventoryNode n)=>n.ParentId is {} id&&nodes.TryGetValue(id,out var parent)?parent.Name:"";
            void Plain(InventoryNode n,params string[] attrs)=>Attributes(n,attrs.Select(a=>"{}"+a).ToArray());
            bool PropertyList(InventoryNode list)=>ParentName(list)=="{"+WP+"}docPr"||ParentName(list)=="{"+PIC+"}cNvPr";
            bool? decorative=null;
            ImageInsets crop=default,effects=default;
            bool cropped=false,hasEffects=false,transformed=false,flipX=false,flipY=false;
            var decoratedProperties=new HashSet<string>(StringComparer.Ordinal);
            var creationProperties=new HashSet<string>(StringComparer.Ordinal);
            foreach(var n in all.Where(n=>n.Id!=drawing.Id&&handledAnchorNodes?.Contains(n.Id)!=true))
            {
                if(n.Kind=="textValue"){if(!string.IsNullOrWhiteSpace(n.Text))Issue("LAYOUT_DRAWING_TEXT_UNQUALIFIED",n,"Texte ou objet spécialisé dans le dessin non qualifié.");continue;}
                if(n.Name.StartsWith('#'))continue;
                if(n.Name=="{"+WP+"}inline"){Attributes(n,"{}distT","{}distB","{}distL","{}distR","{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}anchorId","{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}editId");if(n.Attributes.Any(a=>a.Key.StartsWith("{}dist",StringComparison.Ordinal)&&a.Value!="0"))Issue("LAYOUT_INLINE_DISTANCE_UNQUALIFIED",n,"Distance externe d’image non qualifiée.");}
                else if(n.Name=="{"+WP+"}extent"||n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}xfrm")Plain(n,"cx","cy");
                else if(n.Name=="{"+D+"}extLst")
                {Plain(n);if(ParentName(n)!="{"+D+"}blip"&&!PropertyList(n))Issue("LAYOUT_IMAGE_EXTENSION_UNQUALIFIED",n,"Extension de dessin hors profil.");}
                else if(n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}extLst")
                {
                    Plain(n,"uri");
                    var list=nodes[n.ParentId!];
                    if(!Guid.TryParse(n.Attributes.GetValueOrDefault("{}uri"),out var uri)||
                        !(uri==DpiExtension&&ParentName(list)=="{"+D+"}blip"||
                        PropertyList(list)&&Elements(n).Take(2).ToArray() is [var entry]&&
                        (uri==DecorativeExtension&&entry.Name=="{"+Adec+"}decorative"||
                         uri==CreationExtension&&entry.Name=="{"+A16+"}creationId")))
                        Issue("LAYOUT_IMAGE_EXTENSION_UNQUALIFIED",n,"Extension de dessin non interprétée.");
                }
                else if(n.Name=="{"+A16+"}creationId")
                {
                    Plain(n,"id");
                    if(ParentName(n)!="{"+D+"}ext"||nodes[n.ParentId!] is not {} ext||
                        !Guid.TryParse(ext.Attributes.GetValueOrDefault("{}uri"),out var uri)||uri!=CreationExtension||
                        ext.ParentId is not {} listId||!PropertyList(nodes[listId])||Elements(n).Any()||
                        !Guid.TryParseExact(n.Attributes.GetValueOrDefault("{}id"),"B",out _)||
                        !creationProperties.Add(nodes[listId].ParentId!))
                        Issue("LAYOUT_IMAGE_EXTENSION_UNQUALIFIED",n,"Identifiant de création graphique invalide ou dupliqué.");
                    else Issue("LAYOUT_IMAGE_CREATION_METADATA",n,"Identifiant de création du dessin conservé dans l’inventaire.",false);
                }
                else if(n.Name=="{"+Adec+"}decorative")
                {
                    Plain(n,"val");
                    if(ParentName(n)!="{"+D+"}ext"||nodes[n.ParentId!] is not {} ext||
                        !Guid.TryParse(ext.Attributes.GetValueOrDefault("{}uri"),out var uri)||uri!=DecorativeExtension||
                        ext.ParentId is not {} listId||!PropertyList(nodes[listId])||Elements(n).Any()||
                        !Flag(n,"val",out bool value)||!decoratedProperties.Add(nodes[listId].ParentId!)||
                        decorative is {} previous&&previous!=value)
                        Issue("LAYOUT_IMAGE_DECORATIVE_UNQUALIFIED",n,"Déclaration d’accessibilité graphique ambiguë ou invalide.");
                    else decorative=value;
                }
                else if(n.Name=="{http://schemas.microsoft.com/office/drawing/2010/main}useLocalDpi")
                {Plain(n,"val");if(ParentName(n)!="{"+D+"}ext"||n.Attributes.GetValueOrDefault("{}val") is not ("0" or "1" or "true" or "false"))Issue("LAYOUT_IMAGE_DPI_UNQUALIFIED",n,"Indicateur de compression local invalide.");else Issue("LAYOUT_IMAGE_COMPRESSION_METADATA",n,"Indicateur de compression conservé ; dimensions EMU explicites utilisées, sans recompression.",false);}
                else if(n.Name=="{"+WP+"}effectExtent")
                {
                    Plain(n,"l","t","r","b");
                    if(hasEffects||!Insets(n,nativeComposition?1_828_800:0,12_700,out effects))
                        Issue("LAYOUT_IMAGE_EFFECT_UNQUALIFIED",n,"Étendue supplémentaire du dessin non qualifiée.");
                    hasEffects=true;
                }
                else if(n.Name=="{"+WP+"}docPr"||n.Name=="{"+PIC+"}cNvPr")
                {Plain(n,"id","name","descr","title","hidden");if(n.Attributes.GetValueOrDefault("{}hidden") is "1" or "true")Issue("LAYOUT_IMAGE_HIDDEN_UNQUALIFIED",n,"Dessin masqué non qualifié.");}
                else if(n.Name=="{"+WP+"}cNvGraphicFramePr"||n.Name=="{"+D+"}graphic"||n.Name=="{"+PIC+"}pic"||n.Name=="{"+PIC+"}nvPicPr"||n.Name=="{"+D+"}stretch"||n.Name=="{"+D+"}fillRect"||n.Name=="{"+D+"}avLst"||n.Name=="{"+D+"}effectLst")Plain(n);
                else if(n.Name=="{"+PIC+"}spPr")
                {
                    if(nativeComposition&&Value(n,"bwMode")=="auto")Plain(n,"bwMode");
                    else Plain(n);
                }
                else if(nativeComposition&&n.Name=="{"+D+"}ln"&&ParentName(n)=="{"+PIC+"}spPr"&&
                    Elements(n).Take(2).ToArray() is [var fill]&&fill.Name=="{"+D+"}noFill")Plain(n);
                else if(nativeComposition&&n.Name=="{"+D+"}noFill"&&
                    (ParentName(n)=="{"+PIC+"}spPr"||ParentName(n)=="{"+D+"}ln")&&!Elements(n).Any())Plain(n);
                else if(nativeComposition&&n.Name=="{"+D+"}srcRect")
                {
                    Plain(n,"l","t","r","b");
                    if(cropped||ParentName(n)!="{"+PIC+"}blipFill"||!Insets(n,99_999,100_000,out crop)||
                        crop.Left+crop.Right>=1||crop.Top+crop.Bottom>=1)
                        Issue("LAYOUT_IMAGE_CROP_UNQUALIFIED",n,"Rectangle source non intérieur, vide ou ambigu.");
                    cropped=true;
                }
                else if(n.Name=="{"+D+"}graphicData"){Plain(n,"uri");if(n.Attributes.GetValueOrDefault("{}uri")!=PIC)Issue("LAYOUT_GRAPHIC_TYPE_UNQUALIFIED",n,"Objet graphique autre qu’une image.");}
                else if(n.Name=="{"+D+"}graphicFrameLocks"||n.Name=="{"+D+"}picLocks")Plain(n,"noChangeAspect","noChangeArrowheads","noChangeShapeType","noMove","noResize","noRot","noEditPoints","noGrp","noSelect");
                else if(n.Name=="{"+PIC+"}cNvPicPr")Plain(n,"preferRelativeResize");
                else if(n.Name=="{"+PIC+"}blipFill")Plain(n,"dpi","rotWithShape");
                else if(n.Name=="{"+D+"}blip")Attributes(n,"{"+R+"}embed","{"+R+"}link","{}cstate");
                else if(n.Name=="{"+D+"}xfrm")
                {
                    Plain(n,"rot","flipH","flipV");
                    string? angle=Value(n,"rot");
                    bool halfTurn=angle is "10800000" or "-10800000";
                    if(transformed||!ValidFlag(n,"flipH")||!ValidFlag(n,"flipV")||
                        angle is not (null or "0")&&!(nativeComposition&&halfTurn)||
                        !nativeComposition&&(Bool(n,"flipH",true)||Bool(n,"flipV",true)))
                        Issue("LAYOUT_IMAGE_TRANSFORM_UNQUALIFIED",n,"Transformation modifiant la boîte ou ambiguë.");
                    flipX=Bool(n,"flipH",true)^halfTurn;
                    flipY=Bool(n,"flipV",true)^halfTurn;
                    transformed=true;
                }
                else if(n.Name=="{"+D+"}off"){Plain(n,"x","y");if(n.Attributes.Any(a=>a.Key.StartsWith("{}",StringComparison.Ordinal)&&a.Value!="0"))Issue("LAYOUT_IMAGE_OFFSET_UNQUALIFIED",n,"Décalage interne non qualifié.");}
                else if(n.Name=="{"+D+"}prstGeom"){Plain(n,"prst");if(n.Attributes.GetValueOrDefault("{}prst")!="rect")Issue("LAYOUT_IMAGE_GEOMETRY_UNQUALIFIED",n,"Géométrie d’image hors profil.");}
                else Issue("LAYOUT_IMAGE_PROPERTY_UNQUALIFIED",n,"Propriété de dessin non qualifiée : "+n.Name);
            }
            var extents=all.Where(n=>n.Name=="{"+WP+"}extent").ToArray();var blips=all.Where(n=>n.Name=="{"+D+"}blip").ToArray();
            if(extents.Length!=1||blips.Length!=1||all.Count(n=>n.Name=="{"+PIC+"}pic")!=1){Issue("LAYOUT_IMAGE_STRUCTURE_INVALID",drawing,"Une seule image et une taille explicite sont requises.");return null;}
            long Dimension(InventoryNode n,string name)
            {
                if(!long.TryParse(n.Attributes.GetValueOrDefault("{}"+name),NumberStyles.None,CultureInfo.InvariantCulture,out long value)||value is <1 or >18288000){Issue("LAYOUT_IMAGE_DIMENSION_INVALID",n,"Dimension EMU invalide ou hors profil.");return 0;}
                return value;
            }
            long width=Dimension(extents[0],"cx"),height=Dimension(extents[0],"cy");
            foreach(var e in all.Where(n=>n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}xfrm"))
            {
                long innerWidth=Dimension(e,"cx"),innerHeight=Dimension(e,"cy");
                if(Math.Abs(innerWidth-width)<=127&&Math.Abs(innerHeight-height)<=127)continue;
                // wp:extent is the final inline size. A uniform scale maps the
                // single rectangle to it without changing crop, orientation or ratio.
                // The Word-level effect extent is already in the final coordinate system.
                if(nativeComposition&&handledAnchorNodes is null&&
                    Math.Min(Math.Min(width,height),Math.Min(innerWidth,innerHeight))>0&&
                    Math.Abs(width*innerHeight-height*innerWidth)<=Math.Max(width,height))
                    Issue("LAYOUT_INLINE_PICTURE_RESCALED",e,
                        "Mise à l’échelle proportionnelle vers les dimensions wp:extent. Dimensions locales inventoriées.",false);
                else Issue("LAYOUT_IMAGE_SIZE_CONFLICT",e,"Tailles de dessin contradictoires.");
            }
            var blip=blips[0];string? id=blip.Attributes.GetValueOrDefault("{"+R+"}embed");
            if(blip.Attributes.ContainsKey("{"+R+"}link")){Issue("LAYOUT_LINKED_IMAGE_REFUSED",blip,"Image liée externe non chargée.");return null;}
            var image=ReadImageResource(drawing,blip,id,PageComposer.Points(width),PageComposer.Points(height),before);
            return image is null?null:image with
            {Decorative=decorative,Crop=crop,Effects=effects,FlipX=flipX,FlipY=flipY};

            bool Insets(InventoryNode node,int limit,float denominator,out ImageInsets result)
            {
                result=default;
                if(Elements(node).Any()||!Side("l",out int left)||!Side("t",out int top)||
                    !Side("r",out int right)||!Side("b",out int bottom))return false;
                result=new(left/denominator,top/denominator,right/denominator,bottom/denominator);
                return true;

                bool Side(string name,out int value)
                {
                    value=0;
                    return Value(node,name) is not {} raw||int.TryParse(raw,NumberStyles.Integer,
                        CultureInfo.InvariantCulture,out value)&&value>=0&&value<=limit;
                }
            }
        }

        InlineImage? ReadImageResource(InventoryNode drawing,InventoryNode reference,string? relationId,
            float width,float height,int before)
        {
            var relations=input.Manifest.Relationships.Where(r=>r.SourcePart==drawing.Source.PartUri&&r.Id==relationId&&r.Type==R+"/image"&&r.Mode=="Internal").ToArray();
            var media=relations.Length==1?input.Manifest.Media.FirstOrDefault(m=>m.PartUri==relations[0].ResolvedPart):null;
            if(nativeComposition&&media is not null&&MetafileReader.IsMetafile(media.ContentType))
                return ReadMetafile(drawing,reference,media,width,height,before);
            if(media is null||!input.SafeMedia.TryGetValue(media.Id,out var bytes)){Issue("LAYOUT_IMAGE_RESOURCE_REFUSED",reference,"Ressource d’image absente, invalide ou hors profil.");return null;}
            if(issues.Skip(before).Any(i=>i.BlocksRendering))return null;
            if(!renderImages.TryGetValue(media.Id,out var raster))
            {
                if(renderImages.Count>=64||renderPixels+(long)(media.Width??8192)*(media.Height??8192)>24_000_000){Issue("LAYOUT_IMAGE_BUDGET",reference,"Budget des images dépassé.");return null;}
                var admission=DocumentRasterCache.For(input).ReadAsync(media,bytes,token).GetAwaiter().GetResult();
                if(admission.Raster is not {} decoded)
                {
                    string code=admission.Failure??DocumentRasterCache.DecodeRefused;
                    Issue(code,reference,code switch
                    {
                        MediaDecodeBudget.CpuLimit=>"Budget CPU du décodage dépassé. Média source conservé.",
                        MediaDecodeBudget.WallLimit=>"Délai de traitement du raster dépassé. Média source conservé.",
                        DocumentRasterCache.QueueLimit=>"Capacité ou délai d’attente du traitement des images dépassé.",
                        DocumentRasterCache.HashMismatch=>"Empreinte du média incohérente.",
                        SafePng.ColorUnqualified or SafeJpeg.ColorUnqualified=>"Espace colorimétrique source hors profil de rendu.",
                        _=>"Structure, couleur ou profil de décodage PNG/JPEG non qualifié."
                    });
                    return null;
                }
                // InlineImage exposes its byte array to callers of this layout.
                raster=decoded with{Bytes=(byte[])decoded.Bytes.Clone()};
                if(raster.Notice.Length>0)Issue("LAYOUT_IMAGE_METADATA_OMITTED",reference,raster.Notice,false);
                renderImages[media.Id]=raster;renderPixels+=(long)raster.Width*raster.Height;
            }
            if(Math.Abs(width/height-(float)raster.Width/raster.Height)>.01f)Issue("LAYOUT_IMAGE_SOURCE_STRETCH",drawing,"Dimensions DOCX conservées ; proportion différente de celle des pixels source orientés.",false);
            return new(media.Id,drawing.Source,width,height,raster.Width,raster.Height,media.Sha256)
            {RenderBytes=raster.Bytes,OriginalPixelWidth=media.Width!.Value,OriginalPixelHeight=media.Height!.Value,
                Orientation=raster.Orientation};

        }

        InlineImage? ReadMetafile(InventoryNode drawing,InventoryNode reference,MediaResource media,float width,float height,int before)
        {
            if(issues.Skip(before).Any(i=>i.BlocksRendering))return null;
            if(!input.MetafileParts.TryGetValue(media.PartUri,out var bytes)||
                bytes.Length!=media.Bytes||Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=media.Sha256)
            {
                Issue("PAGE_METAFILE_SOURCE_UNQUALIFIED",reference,"Métafichier absent du budget binaire borné ou empreinte incohérente.");
                return null;
            }
            if(!renderMetafiles.TryGetValue(media.Id,out var vector))
            {
                if(renderMetafiles.Count>=16)
                {
                    Issue("PAGE_METAFILE_BUDGET",reference,"Budget de métafichiers vectoriels dépassé.");
                    return null;
                }
                try {vector=MetafileReader.Read(bytes,media.ContentType,token);}
                catch(InvalidDataException error)
                {
                    Issue("PAGE_METAFILE_PROFILE_UNQUALIFIED",reference,error.Message);
                    return null;
                }
                renderMetafiles.Add(media.Id,vector);
            }
            foreach(var font in vector.Texts.Select(t=>(t.RequestedFace,t.UsedFace,t.SimulatedItalic)).Distinct())
                if(!font.RequestedFace.Equals(font.UsedFace,StringComparison.OrdinalIgnoreCase)||font.SimulatedItalic)
                    Issue("PAGE_WMF_FONT_SUBSTITUTION",reference,
                        font.RequestedFace+" → "+font.UsedFace+(font.SimulatedItalic?" ; italique simulé.":"."),false);
            return new(media.Id,drawing.Source,width,height,0,0,media.Sha256){Metafile=vector};
        }
    }
}
