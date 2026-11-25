using System.Globalization;
using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    sealed record OdtContents(string Field,int First,int Last,int Levels,bool Marks,bool Links);
    OdtContents? nativeContents;
    internal sealed record NovaContents(string Field,int First,int Last);
    internal NovaContents? ActiveNovaContents {get;private set;}

    XElement[] BuildOdtBody()
    {
        nativeContents=FindOdtContents();
        if(nativeContents is not {} contents)return Blocks(model.BodyBlocks);
        var blocks=model.BodyBlocks;
        var before=Blocks(blocks.Take(contents.First));
        var cached=Blocks(blocks.Skip(contents.First).Take(contents.Last-contents.First+1));
        var after=Blocks(blocks.Skip(contents.Last+1));
        const string entryStyle="LwContentsEntry";
        OdtNamedStyles.Add(new XElement(Style+"style",new XAttribute(Style+"name",entryStyle),
            new XAttribute(Style+"family","paragraph")));
        var source=new XElement(Text+"table-of-content-source",new XAttribute(Text+"outline-level",contents.Levels),
            new XAttribute(Text+"use-outline-level",contents.Marks?"false":"true"),
            new XAttribute(Text+"use-index-marks",contents.Marks?"true":"false"),
            Enumerable.Range(1,contents.Levels).Select(level=>new XElement(Text+"table-of-content-entry-template",
                new XAttribute(Text+"outline-level",level),new XAttribute(Text+"style-name",entryStyle),
                contents.Links?new XElement(Text+"index-entry-link-start"):null,
                new XElement(Text+"index-entry-text"),
                new XElement(Text+"index-entry-tab-stop",new XAttribute(Style+"type","right"),new XAttribute(Style+"leader-char",".")),
                new XElement(Text+"index-entry-page-number"),
                contents.Links?new XElement(Text+"index-entry-link-end"):null)));
        var index=new XElement(Text+"table-of-content",new XAttribute(Text+"name","LwContents"+Id(contents.Field)),
            source,new XElement(Text+"index-body",cached));
        Feature("editable-contents",PublicationRetention.Native,model.Source(contents.Field).Location,
            "Sommaire ODF actualisable à partir des titres ou des marques reconnues.");
        return [..before,index,..after];
    }

    OdtContents? FindOdtContents()
    {
        var fields=model.Fields.Where(f=>f.Command=="TOC").Take(2).ToArray();
        if(fields is not [{StructureVerified:true,Locked:false,EndId:{} end} field]||
            model.Element(field.Id).StoryId is not {} storyId||model.Stories.All(s=>s.RootId!=storyId||s.Kind!="body")||
            PageFieldSyntax.Instruction(model,field) is not {} instruction||
            ParagraphOf(model,field.Id) is not {} first||ParagraphOf(model,end) is not {} last)
            return null;
        int left=model.BodyBlocks.IndexOf(first),right=model.BodyBlocks.IndexOf(last);
        if(left<0||right<left||right-left>=512)return null;
        int begin=model.Element(field.Id).Order,finish=model.Element(end).Order;
        foreach(string block in model.BodyBlocks.Skip(left).Take(right-left+1))
        {
            token.ThrowIfCancellationRequested();
            if(model.Element(block).Kind!=SemanticKind.Paragraph||
                tokens[block].Where(Visible).Any(t=>model.Element(t.Source.NodeId).Order<begin||model.Element(t.Source.NodeId).Order>finish))
                return null;
        }

        string[] words=instruction.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        bool outline=false,marks=false,links=false;
        int levels=0;
        for(int i=1;i<words.Length;i++)
        {
            switch(words[i].ToLowerInvariant())
            {
                case "\\h" when !links: links=true;break;
                case "\\o" when !outline&&!marks&&i+1<words.Length:
                    string[] range=words[++i].Trim('"').Split('-');
                    if(range is not ["1",var maximum]||!int.TryParse(maximum,NumberStyles.None,CultureInfo.InvariantCulture,out levels)||
                        levels is <1 or >9)return null;
                    outline=true;
                    break;
                case "\\f" when !outline&&!marks&&i+1<words.Length:
                    string group=words[++i].Trim('"');
                    var entries=model.TocEntries.Where(e=>e.SelectingTocIds.Contains(field.Id)).ToArray();
                    if(entries.Length==0||entries.Length!=model.TocEntries.Length||
                        entries.Any(e=>!string.Equals(e.Group,group,StringComparison.OrdinalIgnoreCase)||e.OmitPageNumber))
                        return null;
                    levels=entries.Max(e=>e.Level);
                    marks=true;
                    break;
                default:return null;
            }
        }
        return outline||marks?new(field.Id,left,right,levels,marks,links):null;
    }

    internal NovaContents? FindNovaContents(Func<LayoutParagraph,bool> isChapter)
    {
        if(snapshot.FieldStability!="Stable"||FindOdtContents() is not {Marks:true,Links:true,Levels:1} contents)
            return null;
        if(model.Sections.Skip(1).Any(section=>section.StartBlock>contents.First&&section.StartBlock<=contents.Last))return null;
        var chapters=model.BodyBlocks.Where(id=>Paragraph(id) is {} paragraph&&isChapter(paragraph)).ToArray();
        var entries=model.TocEntries.Where(entry=>entry.SelectingTocIds.Contains(contents.Field)).ToArray();
        if(chapters.Length==0||chapters.Length!=entries.Length||!chapters.SequenceEqual(entries.Select(entry=>entry.ParagraphId)))
            return null;
        var rows=model.BodyBlocks.Skip(contents.First).Take(contents.Last-contents.First+1).ToArray();
        if(rows.Length!=chapters.Length)return null;
        for(int i=0;i<rows.Length;i++)
        {
            token.ThrowIfCancellationRequested();
            string row=rows[i],chapter=chapters[i];
            var fields=fieldsByParagraph[row].Where(field=>field.Id!=contents.Field).Take(2).ToArray();
            if(fields is not [{Command:"PAGEREF",Locked:false,StructureVerified:true,BookmarkId:{} target} folio]||
                ParagraphOf(model,target)!=chapter||marks[row].Any()||bookmarkEnds[row].Any()||
                Paragraph(row)?.List is not null||
                tokens[row].Any(item=>Visible(item)&&item.Role==TextRole.Control&&item.Control!="Tab"))return null;
            var excluded=folio.StoredResultIds.ToHashSet(StringComparer.Ordinal);
            var rowTokens=tokens[row].Where(item=>Visible(item)&&!excluded.Contains(item.Source.NodeId)).ToArray();
            if(rowTokens.Any(item=>LinkFor(item.Source.NodeId) is {} link&&link.TargetId!=target))return null;
            string label=Label(entries[i].Label), title=Label(TextOf(tokens[chapter].Where(Visible)));
            string ordinal=(i+1).ToString(CultureInfo.InvariantCulture);
            bool redundantOrdinal=label==ordinal+" "+title&&
                (title.StartsWith("Chapitre "+ordinal+" ",StringComparison.OrdinalIgnoreCase)||
                    title.StartsWith("Chapter "+ordinal+" ",StringComparison.OrdinalIgnoreCase));
            if(label.Length==0||label!=Label(TextOf(rowTokens))||label!=title&&!redundantOrdinal)
                return null;
        }
        return ActiveNovaContents=new(contents.Field,contents.First,contents.Last);

        static string TextOf(IEnumerable<TextToken> items)=>string.Concat(items.Select(item=>
            item.Role==TextRole.Control&&item.Control=="Tab"?" ":item.Original));
        static string Label(string text)=>string.Join(' ',text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    }
}
