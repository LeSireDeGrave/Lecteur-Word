using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    readonly Dictionary<string,PublicationRepresentation> representations=new(StringComparer.Ordinal);

    internal void Represent(string id,PublicationRepresentation representation=PublicationRepresentation.Native)
    {
        if(!representations.TryGetValue(id,out var previous)||representation>previous)
            representations[id]=representation;
    }

    internal void RepresentImage(Inline inline)
    {
        var image=inline.Image!;
        bool raster=image.ResourceId.StartsWith("publication:",StringComparison.Ordinal);
        Represent(inline.Source.NodeId,raster?PublicationRepresentation.Presentation:
            VectorImage(image)?PublicationRepresentation.Compatible:PublicationRepresentation.Native);
        if(!raster||image.ResourceId.EndsWith(":graphics",StringComparison.Ordinal)||
            floatingObjects.GetValueOrDefault(inline.Source.NodeId) is not {} drawing)return;
        foreach(var frame in drawing.Frames.Where(frame=>frame.Frame.StoryId.Length>0&&frame.Content.Lines.Count>0))
            foreach(var element in InventoriedContent([frame.Frame.StoryId],followNotes:false))
                Represent(element.Id,PublicationRepresentation.Presentation);
    }

    ImmutableArray<PublicationContent> ReviewConservation()
    {
        var roots=model.Stories.Where(s=>s.Kind=="body").Select(story=>story.RootId)
            .Concat(snapshot.StoryRegions.Where(r=>r.Status!="Inactive")
                .Select(r=>r.Selection?.Reference.TargetId).OfType<string>().Distinct(StringComparer.Ordinal)
                .Where(root=>HasAuxiliaryContent(root,ignoreFolios:!Odt)));
        var items=ImmutableArray.CreateBuilder<PublicationContent>();
        var diagnosed=Issues.Where(issue=>issue.Source is not null&&issue.BlocksExport).Select(issue=>issue.Source).ToHashSet();
        foreach(var element in InventoriedContent(roots,followNotes:true))
        {
            var origin=model.Source(element.Id);
            var representation=representations.GetValueOrDefault(element.Id,PublicationRepresentation.Residual);
            if(representation==PublicationRepresentation.Residual&&diagnosed.Add(origin.Location))
                Issue("EXPORT_CONTENT_RESIDUAL",origin,"Le contenu source ne figure pas dans la représentation produite.");
            items.Add(new(element.Kind,origin.Location,representation));
        }
        return items.ToImmutable();
    }

    IEnumerable<SemanticElement> InventoriedContent(IEnumerable<string> roots,bool followNotes)
    {
        var visited=new HashSet<string>(StringComparer.Ordinal);
        var pending=new Stack<string>(roots.Reverse());
        while(pending.TryPop(out string? id))
        {
            token.ThrowIfCancellationRequested();
            if(!visited.Add(id))continue;
            var element=model.Element(id);
            if(element.Coverage==SemanticCoverage.InactiveAlternative||element.Kind is SemanticKind.Property or SemanticKind.Section)
                continue;
            if(element.Kind is SemanticKind.Drawing or SemanticKind.NoteCall)
            {
                string? paragraph=ParagraphOf(model,id);
                if(paragraph is null||!tokens[paragraph].Any(t=>t.Source.NodeId==id&&Visible(t)))continue;
                if(!Odt&&IsDecorativeDrawing(id)&&runningStoryIds.Contains(element.StoryId??""))continue;
                if(element.Kind==SemanticKind.NoteCall&&followNotes&&references[id].FirstOrDefault() is {Status:ReferenceStatus.Resolved,TargetId:{} note})
                    pending.Push(note);
                yield return element;
            }
            else if(element.Kind is SemanticKind.Story or SemanticKind.Table||element.Kind==SemanticKind.Paragraph&&
                tokens[id].Any(t=>Visible(t)&&t.Role==TextRole.Source&&!string.IsNullOrWhiteSpace(t.Original)))
                yield return element;
            foreach(var child in model.Children(id).Reverse())pending.Push(child.Id);
        }
    }
}
