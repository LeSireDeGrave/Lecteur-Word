using System.Globalization;
using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    sealed record OdtContents(string Field,int First,int Last,int Levels,bool Marks,bool Links);
    OdtContents? nativeContents;

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
}
