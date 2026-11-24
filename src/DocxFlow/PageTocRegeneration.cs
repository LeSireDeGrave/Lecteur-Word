using System.Globalization;

namespace LecteurWord.Flow;

internal sealed record GeneratedTocRow(string Id,SemanticSource Paragraph,SemanticSource Field,SemanticSource Destination,
    int Level,string? StyleId,bool Hyperlink);

internal static partial class PageComposer
{
    sealed record TocSelector((int First,int Last)? Outline,bool Direct,Dictionary<string,int> Styles,bool Hyperlink);

    static Dictionary<string,GeneratedTocRow[]> ReadEmptyTocs(SemanticDocument model,
        IReadOnlyDictionary<string,TextParagraph> text,Action check,out Dictionary<string,string> continuations)
    {
        continuations=new(StringComparer.Ordinal);
        var plans=new Dictionary<string,GeneratedTocRow[]>(StringComparer.Ordinal);
        var fields=model.Fields.Where(f=>f is {Command:"TOC",StructureVerified:true,Locked:false,ParentFieldId:null,
            StoredResultIds.IsEmpty:true}).ToArray();
        if(fields.Length==0)return plans;
        var body=model.BodyBlocks.Select((id,index)=>(id,index)).ToDictionary(p=>p.id,p=>p.index,StringComparer.Ordinal);
        var otherFields=model.Fields.ToLookup(f=>Ancestor(model,f.Id,SemanticKind.Paragraph));
        var numbered=model.Numbering.Where(n=>!n.Suppressed).Select(n=>n.ParagraphId).ToHashSet(StringComparer.Ordinal);
        var parents=model.Fields.Where(f=>f.ParentFieldId is not null).Select(f=>f.ParentFieldId!).ToHashSet(StringComparer.Ordinal);
        var headings=new List<(TextParagraph Text,int? Level,int? Direct,string? Style)>();
        foreach(string id in model.BodyBlocks)
        {
            check();
            if(!text.TryGetValue(id,out var paragraph))continue;
            string? pp=model.ChildId(id,"pPr");
            string? style=pp is null?null:model.ChildId(pp,"pStyle");
            string? use=style??model.DefaultStyle("paragraph")?.Id, name=null;
            int? level=null;
            // Word's \o uses the outline level defined by the paragraph style.
            if(use is not null&&model.StyleUse(use) is {Resolved:true,BaseToDerivedIds.IsEmpty:false} chain)
            {
                foreach(string layer in chain.BaseToDerivedIds)
                    if(model.ChildId(layer,"pPr") is {} properties&&model.ChildId(properties,"outlineLvl") is not null)
                        level=OutlineLevel(properties);
                if(model.ChildId(chain.BaseToDerivedIds[^1],"name") is {} declaration)name=model.Attribute(declaration,"val");
            }
            int? direct=pp is null?null:OutlineLevel(pp);
            if(level is not null||direct is not null||name is not null)headings.Add((paragraph,level,direct,name));
        }

        int total=0;
        foreach(var field in fields)
        {
            check();
            if(!PageFieldSyntax.ValidFlags(model,field)||PageFieldSyntax.Instruction(model,field) is not {} code||
                ReadTocSelector(code) is not {} selector||
                Ancestor(model,field.Id,SemanticKind.Paragraph) is not {} paragraphId||!body.TryGetValue(paragraphId,out int first)||
                field.EndId is not {} end||Ancestor(model,end,SemanticKind.Paragraph) is not {} endParagraph||
                !body.TryGetValue(endParagraph,out int last)||last<first||last-first>=128||
                field.InstructionAttribute is null&&field.SeparatorId is null||
                parents.Contains(field.Id)||
                !model.Sections.Any(section=>first>=section.StartBlock&&last<section.EndBlockExclusive))continue;
            var placeholders=model.BodyBlocks[first..(last+1)];
            if(!IsPlaceholder(placeholders,field))continue;
            var rows=new List<GeneratedTocRow>();
            bool qualified=true;
            foreach(var (heading,outline,direct,style) in headings)
            {
                check();
                int? selectedOutline=selector.Outline is {} range&&outline>=range.First&&outline<=range.Last?outline:null;
                int? selectedDirect=selector.Direct?direct:null;
                int? named=style is not null&&selector.Styles.TryGetValue(style,out int mapped)?mapped:null;
                int? selected=named??selectedOutline??selectedDirect;
                if(selected is not {} level)continue;
                if(placeholders.Contains(heading.Id)||heading.RightToLeft||heading.Controls.Length!=0||
                    string.IsNullOrWhiteSpace(heading.Context)||heading.Fragments.Any(f=>f.Measure is null||f.BidiLevel%2!=0)||
                    numbered.Contains(heading.Id)||selectedOutline is not null&&selectedOutline!=level||
                    selectedDirect is not null&&selectedDirect!=level)
                {qualified=false;break;}
                string styleName="TOC"+level.ToString(CultureInfo.InvariantCulture);
                var styles=model.Styles.Where(s=>s.Type=="paragraph"&&
                    (s.DeclaredId.Equals(styleName,StringComparison.OrdinalIgnoreCase)||model.ChildId(s.Id,"name") is {} name&&
                        string.Equals(model.Attribute(name,"val"),"toc "+level.ToString(CultureInfo.InvariantCulture),StringComparison.OrdinalIgnoreCase)))
                    .Take(2).ToArray();
                string? styleId=styles.Length==1?styles[0].Id:model.DefaultStyle("paragraph")?.Id;
                if(styles.Length>1||styleId is not null&&model.StyleUse(styleId)?.Resolved!=true)
                {qualified=false;break;}
                rows.Add(new("toc:"+field.Id+":"+heading.Id,model.Source(paragraphId),model.Source(field.Id),
                    model.Source(heading.Id),level,styleId,selector.Hyperlink));
                if(total+rows.Count>MaxBodyPageFields)throw new InvalidDataException("PAGE_TOC_ENTRY_LIMIT");
            }
            if(qualified&&rows.Count>0)
            {
                plans.Add(paragraphId,rows.ToArray());
                foreach(string id in placeholders.Skip(1))continuations.Add(id,paragraphId);
                total+=rows.Count;
            }
        }
        return plans;

        bool IsPlaceholder(IEnumerable<string> paragraphs,SemanticField field)
        {
            var tokens=field.InstructionIds.Select(id=>model.ParentId(id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            tokens.Add(field.Id);
            if(field.EndId is {} end)tokens.Add(end);
            if(field.SeparatorId is {} separator)tokens.Add(separator);
            foreach(string paragraph in paragraphs)
            {
                check();
                if(!text.TryGetValue(paragraph,out var host)||host.ContextLength!=0||host.RightToLeft||
                    model.Revision(paragraph)!="unchanged"||otherFields[paragraph].Any(f=>f.Id!=field.Id))
                    return false;
                if(model.ChildId(paragraph,"pPr") is {} pp)
                    foreach(var p in model.Children(pp))
                        if(!model.Name(p.Id).StartsWith('#')&&model.Name(p.Id)!=SemanticIndex.QName(SemanticIndex.W,"pStyle"))return false;
                if(!model.BlocksOf(paragraph).All(p=>p.Id==field.Id&&p.Kind==SemanticKind.SimpleField?
                    model.BlocksOf(p.Id).All(EmptyRun):p.Kind==SemanticKind.Run&&model.BlocksOf(p.Id).All(r=>tokens.Contains(r.Id)||EmptyText(r))))
                    return false;
            }
            return true;

            bool EmptyRun(SemanticElement run)=>run.Kind==SemanticKind.Run&&model.BlocksOf(run.Id).All(EmptyText);
            bool EmptyText(SemanticElement e)=>model.Name(e.Id)==SemanticIndex.QName(SemanticIndex.W,"t")&&!model.Children(e.Id).Any();
        }

        int? OutlineLevel(string properties)=>model.ChildId(properties,"outlineLvl") is {} outline&&
            int.TryParse(model.Attribute(outline,"val"),NumberStyles.None,CultureInfo.InvariantCulture,out int value)&&value is >=0 and <=8?
            value+1:null;
    }

    static TocSelector? ReadTocSelector(string instruction)
    {
        var words=new List<string>();
        var remaining=instruction.AsSpan().TrimStart();
        while(!remaining.IsEmpty)
        {
            int end;
            if(remaining[0]=='"')
            {
                end=remaining[1..].IndexOf('"')+1;
                if(end==0||end+1<remaining.Length&&!char.IsWhiteSpace(remaining[end+1]))return null;
                words.Add(remaining[1..end].ToString());
                end++;
            }
            else
            {
                end=remaining.IndexOfAny(" \t\r\n");
                if(end<0)end=remaining.Length;
                if(remaining[..end].Contains('"'))return null;
                words.Add(remaining[..end].ToString());
            }
            remaining=remaining[end..].TrimStart();
        }
        if(words.Count==0||!words[0].Equals("TOC",StringComparison.OrdinalIgnoreCase))return null;
        bool direct=false,hyperlink=false,webOnlySuppression=false;
        (int First,int Last)? outline=null;
        var styles=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for(int i=1;i<words.Count;i++)
        {
            switch(words[i].ToLowerInvariant())
            {
                case "\\h" when !hyperlink:hyperlink=true;break;
                // Word hides the leaders and numbers for \z only in Web Layout.
                case "\\z" when !webOnlySuppression:webOnlySuppression=true;break;
                case "\\u" when !direct:direct=true;break;
                case "\\o" when outline is null:
                    if(i+1==words.Count||words[i+1].StartsWith('\\')){outline=(1,9);break;}
                    var range=words[++i].AsSpan();
                    if(range.Length!=3||range[1]!='-'||range[0] is <'1' or >'9'||range[2] is <'1' or >'9'||range[0]>range[2])return null;
                    outline=(range[0]-'0',range[2]-'0');
                    break;
                case "\\t" when styles.Count==0&&i+1<words.Count:
                    string declaration=words[++i];
                    if(declaration.Contains(',')&&declaration.Contains(';'))return null;
                    var pairs=declaration.Split(declaration.Contains(';')?';':',',StringSplitOptions.TrimEntries);
                    if(pairs.Length%2!=0)return null;
                    for(int j=0;j<pairs.Length;j+=2)
                        if(pairs[j].Length==0||
                            !int.TryParse(pairs[j+1],NumberStyles.None,CultureInfo.InvariantCulture,out int value)||value is <1 or >9||
                            !styles.TryAdd(pairs[j],value))return null;
                    break;
                default:return null;
            }
        }
        return outline is not null||direct||styles.Count>0?new(outline,direct,styles,hyperlink):null;
    }
}

internal sealed partial class TypographySession
{
    internal TextParagraph ShapeTocRow(GeneratedTocRow row,TextParagraph heading)
    {
        var style=styles.Resolve(row.Paragraph.NodeId,true,paragraphStyleOverride:row.StyleId);
        var sources=heading.Fragments.SelectMany(f=>f.Sources).Select(slice=>new TextToken(slice.Source,slice.RunId,
            row.Id,heading.StoryId,TextRole.Source,model.RawText(slice.Source.NodeId)![slice.Source.Span!.Value.Start..
                slice.Source.Span.Value.EndExclusive],null,style));
        return BuildParagraph(model.Element(row.Paragraph.NodeId),sources.Append(
            new(row.Field,null,row.Id,heading.StoryId,TextRole.Control,null,"Tab",style)),style,row);
    }
}
