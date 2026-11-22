using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        readonly Dictionary<string,byte[]> renderImages=[];long renderPixels;
        readonly Dictionary<string,(int Width,int Height,int Orientation)> imageSizes=[];
        const string WP="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",D="http://schemas.openxmlformats.org/drawingml/2006/main",PIC="http://schemas.openxmlformats.org/drawingml/2006/picture";
        IEnumerable<InventoryNode> Descendants(InventoryNode node)
        {
            var stack=new Stack<InventoryNode>();stack.Push(node);
            while(stack.Count>0){token.ThrowIfCancellationRequested();var current=stack.Pop();yield return current;foreach(var child in children[current.Id].Reverse())stack.Push(child);}
        }
        InlineImage? ReadInlineImage(InventoryNode drawing)
        {
            int before=issues.Count;WAttributes(drawing);var all=Descendants(drawing).ToArray();
            var inline=Elements(drawing).ToArray();if(inline.Length!=1||inline[0].Name!="{"+WP+"}inline")
            {Issue("LAYOUT_FLOATING_IMAGE_UNQUALIFIED",drawing,"Image flottante ou dessin hors profil ; aucune conversion silencieuse en image intégrée.");return null;}
            string ParentName(InventoryNode n)=>n.ParentId is {} id&&nodes.TryGetValue(id,out var parent)?parent.Name:"";
            void Plain(InventoryNode n,params string[] attrs)=>Attributes(n,attrs.Select(a=>"{}"+a).ToArray());
            foreach(var n in all.Where(n=>n.Id!=drawing.Id))
            {
                if(n.Kind=="textValue"){if(!string.IsNullOrWhiteSpace(n.Text))Issue("LAYOUT_DRAWING_TEXT_UNQUALIFIED",n,"Texte ou objet spécialisé dans le dessin non qualifié.");continue;}
                if(n.Name.StartsWith('#'))continue;
                if(n.Name=="{"+WP+"}inline"){Attributes(n,"{}distT","{}distB","{}distL","{}distR","{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}anchorId","{http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing}editId");if(n.Attributes.Any(a=>a.Key.StartsWith("{}dist",StringComparison.Ordinal)&&a.Value!="0"))Issue("LAYOUT_INLINE_DISTANCE_UNQUALIFIED",n,"Distance externe d’image non qualifiée.");}
                else if(n.Name=="{"+WP+"}extent"||n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}xfrm")Plain(n,"cx","cy");
                else if(n.Name=="{"+D+"}extLst")
                {Plain(n);if(ParentName(n)!="{"+D+"}blip")Issue("LAYOUT_IMAGE_EXTENSION_UNQUALIFIED",n,"Extension de dessin hors profil.");}
                else if(n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}extLst")
                {Plain(n,"uri");if(!Guid.TryParse(n.Attributes.GetValueOrDefault("{}uri"),out var uri)||uri!=new Guid("28A0092B-C50C-407E-A947-70E740481C1C"))Issue("LAYOUT_IMAGE_EXTENSION_UNQUALIFIED",n,"Extension de dessin non interprétée.");}
                else if(n.Name=="{http://schemas.microsoft.com/office/drawing/2010/main}useLocalDpi")
                {Plain(n,"val");if(ParentName(n)!="{"+D+"}ext"||n.Attributes.GetValueOrDefault("{}val") is not ("0" or "1" or "true" or "false"))Issue("LAYOUT_IMAGE_DPI_UNQUALIFIED",n,"Indicateur de compression local invalide.");else Issue("LAYOUT_IMAGE_COMPRESSION_METADATA",n,"Indicateur de compression conservé ; dimensions EMU explicites utilisées, sans recompression.",false);}
                else if(n.Name=="{"+WP+"}effectExtent"){Plain(n,"l","t","r","b");if(n.Attributes.Any(a=>a.Key.StartsWith("{}",StringComparison.Ordinal)&&a.Value!="0"))Issue("LAYOUT_IMAGE_EFFECT_UNQUALIFIED",n,"Débordement d’effet non qualifié.");}
                else if(n.Name=="{"+WP+"}docPr"||n.Name=="{"+PIC+"}cNvPr")
                {Plain(n,"id","name","descr","title","hidden");if(n.Attributes.GetValueOrDefault("{}hidden") is "1" or "true")Issue("LAYOUT_IMAGE_HIDDEN_UNQUALIFIED",n,"Dessin masqué non qualifié.");}
                else if(n.Name=="{"+WP+"}cNvGraphicFramePr"||n.Name=="{"+D+"}graphic"||n.Name=="{"+PIC+"}pic"||n.Name=="{"+PIC+"}nvPicPr"||n.Name=="{"+PIC+"}spPr"||n.Name=="{"+D+"}stretch"||n.Name=="{"+D+"}fillRect"||n.Name=="{"+D+"}avLst"||n.Name=="{"+D+"}effectLst")Plain(n);
                else if(n.Name=="{"+D+"}graphicData"){Plain(n,"uri");if(n.Attributes.GetValueOrDefault("{}uri")!=PIC)Issue("LAYOUT_GRAPHIC_TYPE_UNQUALIFIED",n,"Objet graphique autre qu’une image.");}
                else if(n.Name=="{"+D+"}graphicFrameLocks"||n.Name=="{"+D+"}picLocks")Plain(n,"noChangeAspect","noChangeArrowheads","noChangeShapeType","noMove","noResize","noRot","noEditPoints","noGrp","noSelect");
                else if(n.Name=="{"+PIC+"}cNvPicPr")Plain(n,"preferRelativeResize");
                else if(n.Name=="{"+PIC+"}blipFill")Plain(n,"dpi","rotWithShape");
                else if(n.Name=="{"+D+"}blip")Attributes(n,"{"+R+"}embed","{"+R+"}link","{}cstate");
                else if(n.Name=="{"+D+"}xfrm"){Plain(n,"rot","flipH","flipV");if(n.Attributes.Any(a=>a.Key.StartsWith("{}",StringComparison.Ordinal)&&a.Value is not ("0" or "false")))Issue("LAYOUT_IMAGE_TRANSFORM_UNQUALIFIED",n,"Rotation ou retournement non qualifié.");}
                else if(n.Name=="{"+D+"}off"){Plain(n,"x","y");if(n.Attributes.Any(a=>a.Key.StartsWith("{}",StringComparison.Ordinal)&&a.Value!="0"))Issue("LAYOUT_IMAGE_OFFSET_UNQUALIFIED",n,"Décalage interne non qualifié.");}
                else if(n.Name=="{"+D+"}prstGeom"){Plain(n,"prst");if(n.Attributes.GetValueOrDefault("{}prst")!="rect")Issue("LAYOUT_IMAGE_GEOMETRY_UNQUALIFIED",n,"Géométrie d’image hors profil.");}
                else Issue("LAYOUT_IMAGE_PROPERTY_UNQUALIFIED",n,"Propriété de dessin non qualifiée : "+n.Name);
            }
            var extents=all.Where(n=>n.Name=="{"+WP+"}extent").ToArray();var blips=all.Where(n=>n.Name=="{"+D+"}blip").ToArray();
            if(extents.Length!=1||blips.Length!=1||all.Count(n=>n.Name=="{"+PIC+"}pic")!=1){Issue("LAYOUT_IMAGE_STRUCTURE_INVALID",drawing,"Une seule image et une taille explicite sont requises.");return null;}
            float Dimension(InventoryNode n,string name)
            {
                if(!long.TryParse(n.Attributes.GetValueOrDefault("{}"+name),NumberStyles.None,CultureInfo.InvariantCulture,out long value)||value is <1 or >18288000){Issue("LAYOUT_IMAGE_DIMENSION_INVALID",n,"Dimension EMU invalide ou hors profil.");return 0;}
                return value/12700f;
            }
            float width=Dimension(extents[0],"cx"),height=Dimension(extents[0],"cy");
            foreach(var e in all.Where(n=>n.Name=="{"+D+"}ext"&&ParentName(n)=="{"+D+"}xfrm"))if(Math.Abs(Dimension(e,"cx")-width)>.01f||Math.Abs(Dimension(e,"cy")-height)>.01f)Issue("LAYOUT_IMAGE_SIZE_CONFLICT",e,"Tailles de dessin contradictoires.");
            var blip=blips[0];string? id=blip.Attributes.GetValueOrDefault("{"+R+"}embed");
            if(blip.Attributes.ContainsKey("{"+R+"}link")){Issue("LAYOUT_LINKED_IMAGE_REFUSED",blip,"Image liée externe non chargée.");return null;}
            var relations=input.Manifest.Relationships.Where(r=>r.SourcePart==drawing.Source.PartUri&&r.Id==id&&r.Type==R+"/image"&&r.Mode=="Internal").ToArray();
            var media=relations.Length==1?input.Manifest.Media.FirstOrDefault(m=>m.PartUri==relations[0].ResolvedPart):null;
            if(media is null||!input.SafeMedia.TryGetValue(media.Id,out var bytes)){Issue("LAYOUT_IMAGE_RESOURCE_REFUSED",blip,"Ressource d’image absente, invalide ou hors profil.");return null;}
            if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))!=media.Sha256)
            {Issue("LAYOUT_IMAGE_HASH_MISMATCH",blip,"Empreinte du média incohérente.");return null;}
            if(!renderImages.TryGetValue(media.Id,out var clean))
            {
                if(renderImages.Count>=64||renderPixels+(long)(media.Width??8192)*(media.Height??8192)>24_000_000){Issue("LAYOUT_IMAGE_BUDGET",blip,"Budget des images dépassé.");return null;}
                int pixelWidth=media.Width??0,pixelHeight=media.Height??0,orientation=1;string reason;
                bool decoded=media.ContentType=="image/jpeg"?SafeJpeg.TryGetRenderableBytes(bytes,out clean,out pixelWidth,out pixelHeight,out orientation,out reason,token):SafePng.TryGetRenderableBytes(bytes,out clean,out reason,token);
                if(!decoded){Issue("LAYOUT_IMAGE_DECODE_REFUSED",blip,"Image hors profil de décodage borné PNG/JPEG baseline.");return null;}
                if(reason.Length>0)Issue("LAYOUT_IMAGE_METADATA_OMITTED",blip,reason,false);
                renderImages[media.Id]=clean;imageSizes[media.Id]=(pixelWidth,pixelHeight,orientation);renderPixels+=(long)pixelWidth*pixelHeight;
            }
            if(issues.Skip(before).Any(i=>i.BlocksRendering))return null;
            var dimensions=imageSizes[media.Id];
            if(Math.Abs(width/height-(float)dimensions.Width/dimensions.Height)>.01f)Issue("LAYOUT_IMAGE_SOURCE_STRETCH",drawing,"Dimensions DOCX conservées ; proportion différente de celle des pixels source orientés.",false);
            return new(media.Id,drawing.Source,width,height,dimensions.Width,dimensions.Height,media.Sha256){RenderBytes=clean,OriginalPixelWidth=media.Width!.Value,OriginalPixelHeight=media.Height!.Value,Orientation=dimensions.Orientation};
        }
    }
}
