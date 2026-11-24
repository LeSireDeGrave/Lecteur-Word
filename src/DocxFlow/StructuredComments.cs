using System.Globalization;
using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    static readonly XNamespace CommentDc="http://purl.org/dc/elements/1.1/";
    readonly Dictionary<string,(string Target,bool End)> odtCommentMarkers=new(StringComparer.Ordinal);
    readonly HashSet<string> handledComments=new(StringComparer.Ordinal);
    ILookup<string,SemanticElement>? commentMarkersByParagraph;

    void PrepareOdtComments()
    {
        if(!Odt)return;
        foreach(var group in model.References.Where(r=>r is {Kind:"comment",Status:ReferenceStatus.Resolved,TargetId:not null})
            .GroupBy(r=>r.TargetId!,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string target=group.Key;
            if(!model.Stories.Any(s=>s.RootId==target)||
                !model.BlocksOf(target).Any()||model.BlocksOf(target).Any(b=>b.Kind!=SemanticKind.Paragraph))
                continue;
            var references=group.Where(r=>model.Element(r.SourceId).Coverage!=SemanticCoverage.InactiveAlternative&&
                model.Stories.Any(s=>s.RootId==model.Element(r.SourceId).StoryId&&s.Kind=="body")&&
                ParagraphOf(model,r.SourceId) is not null).ToArray();
            var points=OfKind("commentReference");
            var starts=OfKind("commentRangeStart");
            var ends=OfKind("commentRangeEnd");
            if(points is not [var point])continue;
            if(starts is [var start]&&ends is [var end]&&model.Element(start.SourceId).StoryId==model.Element(end.SourceId).StoryId&&
                model.Element(point.SourceId).StoryId==model.Element(start.SourceId).StoryId&&
                model.Element(start.SourceId).Order<model.Element(end.SourceId).Order)
            {
                odtCommentMarkers.Add(start.SourceId,(target,false));
                odtCommentMarkers.Add(end.SourceId,(target,true));
            }
            else if(starts.Length==0&&ends.Length==0)
                odtCommentMarkers.Add(point.SourceId,(target,false));
            else continue;
            handledComments.UnionWith(references.Select(r=>r.SourceId));
            LoadStoryLayout(target,snapshot.Sections.FirstOrDefault()?.Format??new());

            SemanticReference[] OfKind(string name)=>references.Where(r=>model.Name(r.SourceId)==SemanticIndex.QName(SemanticIndex.W,name)).ToArray();
        }
        commentMarkersByParagraph=odtCommentMarkers.Keys.Select(id=>model.Element(id))
            .ToLookup(element=>ParagraphOf(model,element.Id)!,StringComparer.Ordinal);
    }

    void ReadCommentMarkers(string paragraph,List<(int Index,Inline Item)> parts)
    {
        if(commentMarkersByParagraph is null)return;
        foreach(var marker in commentMarkersByParagraph[paragraph])
        {
            var (target,end)=odtCommentMarkers[marker.Id];
            parts.Add((marker.Order,new(model.Source(marker.Id),null,null,null,
                Field:end?"COMMENT-END":"COMMENT",Target:target)));
        }
    }

    XElement WriteOdtComment(Inline item)
    {
        string target=item.Target!;
        string name="LwComment"+Id(target);
        if(item.Field=="COMMENT-END")
            return new(Office+"annotation-end",new XAttribute(Office+"name",name));
        string? author=model.Attribute(target,"author");
        var date=DateTimeOffset.TryParse(model.Attribute(target,"date"),CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,out var parsed)?parsed.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'",CultureInfo.InvariantCulture):null;
        var body=Blocks(model.BlocksOf(target).Select(b=>b.Id));
        Feature("comments",PublicationRetention.Native,item.Source.Location,"Commentaire Writer avec auteur, corps et portée lorsqu’elle est établie.");
        return new(Office+"annotation",new XAttribute(Office+"name",name),
            author is null?null:new XElement(CommentDc+"creator",author),
            date is null?null:new XElement(CommentDc+"date",date),
            body.Select(b=>b.Name==Text+"h"?new XElement(Text+"p",b.Nodes()):b));
    }
}
