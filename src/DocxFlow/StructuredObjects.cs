namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    sealed record PlacedObject(ComposedPage Page,PlacedFloatingPicture[] Pictures,PlacedFloatingTextFrame[] Frames);
    readonly Dictionary<string,PlacedObject> floatingObjects=PlacedObjects(source);
    readonly Dictionary<string,InlineImage> storyImages=new(StringComparer.Ordinal);
    readonly Dictionary<string,InlineImage?> convertedObjects=new(StringComparer.Ordinal);
    readonly Dictionary<string,string> objectLabels=new(StringComparer.Ordinal);

    static Dictionary<string,PlacedObject> PlacedObjects(PublicationSource source)
    {
        var result=new Dictionary<string,PlacedObject>(StringComparer.Ordinal);
        var measured=source.Pagination!.StoryRegions.Where(r=>r.FrameTemplate is not null)
            .Select(r=>PageComposer.PlaceMeasuredStoryFrames(r,source.Pagination.Sections[r.Section-1])).OfType<ComposedPage>();
        foreach(var page in source.Pages.Concat(measured))
        {
            var pictures=page.FloatingPictures.ToLookup(p=>p.Picture.Id,StringComparer.Ordinal);
            var frames=page.FloatingTextFrames.ToLookup(p=>p.Frame.Id,StringComparer.Ordinal);
            foreach(var id in pictures.Select(p=>p.Key).Concat(frames.Select(p=>p.Key)).Distinct(StringComparer.Ordinal))
                result.TryAdd(id,new(page,pictures[id].ToArray(),frames[id].ToArray()));
        }
        return result;
    }

    PlacedFloatingTextFrame[] NativeOdtFrames(string id)
    {
        var frames=activeOdtFrames is {} active
            ?active.Where(frame=>frame.Frame.Id==id).ToArray()
            :floatingObjects.GetValueOrDefault(id)?.Frames??[];
        return frames.All(frame=>frame.Frame is {Rotation:0,Width:>0 and <=1584,Height:>0 and <=1584}&&
            Math.Abs(frame.Frame.X)<=16_000&&Math.Abs(frame.Frame.Y)<=16_000)?frames:[];
    }

    internal string[] TextFrameBlocks(FloatingTextFrame frame)
    {
        LoadStoryLayout(frame.StoryId,frame.Geometry);
        return model.BlocksOf(frame.StoryId).Select(block=>block.Id).ToArray();
    }

    string? RecoverTextBox(string drawing)
    {
        string? story=null;
        var pending=new Stack<SemanticElement>(model.Children(drawing).Reverse());
        while(pending.TryPop(out var element))
        {
            token.ThrowIfCancellationRequested();
            if(element.Kind==SemanticKind.ResourceReference)return null;
            if(element.Kind==SemanticKind.Story)
            {
                if(story is not null||!model.Name(element.Id).EndsWith("}txbxContent",StringComparison.Ordinal))return null;
                story=element.Id;
            }
            foreach(var child in model.Children(element.Id).Reverse())pending.Push(child);
        }
        if(story is null)return null;
        var blocks=model.BlocksOf(story).ToArray();
        if(blocks.Length==0||blocks.Any(block=>block.Kind!=SemanticKind.Paragraph)||
            !blocks.SelectMany(block=>tokens[block.Id]).Any(item=>Visible(item)&&item.Role is TextRole.Source or TextRole.StoredField&&
                !string.IsNullOrWhiteSpace(item.Original)))return null;
        LoadStoryLayout(story,snapshot.Sections.FirstOrDefault()?.Format??new());
        return blocks.All(block=>paragraphs.ContainsKey(block.Id))?story:null;
    }

    Inline[] ReflowDrawing(SemanticSource origin,SemanticReference? link,PlacedObject drawing)
    {
        var result=new List<Inline>();
        var graphicFrames=drawing.Frames.Where(f=>f.Frame.StoryId.Length==0).ToArray();
        if(drawing.Pictures.Length>0||graphicFrames.Length>0)
        {
            if(Composer.PublicationDrawing(drawing.Page,origin.NodeId+":graphics",drawing.Pictures,graphicFrames,token) is {} image&&
                AssetFor(image) is not null)
            {
                result.Add(new(origin,null,null,null,Link:link,Image:image));
                Reserve("EXPORT_OBJECT_RASTER",origin,"Les composants graphiques du groupe sont intégrés sous forme d’image.");
            }
            else Issue("EXPORT_DRAWING_UNSUPPORTED",origin,"Les composants graphiques du groupe ne peuvent être représentés.");
        }
        foreach(var frame in drawing.Frames.Select(f=>f.Frame).Where(f=>f.StoryId.Length>0).OrderBy(f=>f.ChildOrder))
            result.Add(new(origin,null,null,null,TextFrame:frame));
        Reserve("EXPORT_TEXT_FRAME_FLOW",origin,"Le texte et ses blocs restent éditables dans l’ordre de la zone de texte. L’ancrage et le cadre de page sont adaptés au flux.");
        return result.ToArray();
    }

    InlineImage? ConvertObject(string id)
    {
        if(convertedObjects.TryGetValue(id,out var previous))return previous;
        InlineImage? result=null;
        if((placedImages.GetValueOrDefault(id)??storyImages.GetValueOrDefault(id)) is {} inline)
        {
            if(NativeImage(inline)||format!=ExportFormat.Nova&&VectorImage(inline))result=inline;
            else result=Render(inline,source.Pages.FirstOrDefault()?.NativePainter);
            RememberLabel(id,inline);
        }
        else if(floatingObjects.GetValueOrDefault(id) is {} floating)
        {
            if(floating.Pictures is [var picture]&&floating.Frames.Length==0)
            {
                var image=picture.Picture.Image;
                result=NativeImage(image)||format!=ExportFormat.Nova&&VectorImage(image)?image:Render(image,floating.Page.NativePainter);
                RememberLabel(id,image);
            }
            else if(floating.Frames is [var frame]&&frame.Frame.Vector is {} vector&&floating.Pictures.Length==0)
            {
                result=format!=ExportFormat.Nova&&VectorImage(vector)?vector:Render(vector,floating.Page.NativePainter);
                RememberLabel(id,vector);
            }
            else
            {
                result=Composer.PublicationDrawing(floating.Page,id,floating.Pictures,floating.Frames,token);
                var labels=floating.Frames.SelectMany(f=>f.Content.DisplayLines.Select(l=>l.Text))
                    .Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).Take(256).ToArray();
                if(labels.Length!=0)objectLabels.TryAdd(id,string.Join(" ",labels));
            }
        }
        convertedObjects.Add(id,result);
        return result;

        InlineImage? Render(InlineImage image,LineComposer? painter)=>
            Composer.PublicationImage(image,painter,token);
    }

    void RememberLabel(string id,InlineImage image)
    {
        if(image.Diagram is not {} diagram)return;
        var labels=diagram.Shapes.SelectMany(s=>s.Text.Select(line=>line.Fragment.Text)).Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray();
        if(labels.Length!=0)objectLabels.TryAdd(id,string.Join(" ",labels));
    }

    internal static bool VectorImage(InlineImage image)=>image.VmlVector is not null||image.Diagram is not null||
        image.Metafile is not null&&image.Crop==default&&!image.FlipX&&!image.FlipY;
    internal static bool NativeImage(InlineImage image)=>image.RenderBytes.Length>0&&image.Crop==default&&image.Effects==default&&
        !image.FlipX&&!image.FlipY&&image.Orientation==1;

    internal string? ObjectText(string id)
    {
        if(AlternativeText(id) is {} description)return description;
        return objectLabels.GetValueOrDefault(id);
    }
}
