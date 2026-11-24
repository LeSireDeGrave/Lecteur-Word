using System.Globalization;
using System.Collections.Immutable;

namespace LecteurWord.Flow;

internal sealed record FootnoteRequest(SemanticSource Call,SemanticStory Story,int DestinationSection);
internal sealed record FootnoteRequests(ImmutableArray<FootnoteRequest> Notes,SemanticStory Separator,SemanticStory? Continuation,
    bool BeneathText,ImmutableHashSet<int> Sections,ImmutableDictionary<string,string> Numbers,
    SemanticSource? SeparatorRule,SemanticSource? ContinuationRule);
internal sealed record FootnoteRule(SemanticSource Source,float Width);
internal sealed record FootnoteBlock(SemanticSource Source,float Height,ImmutableArray<PageLine> Lines,ImmutableArray<int> Breaks)
{
    internal FootnoteSlice? Fit(int first,float available,bool split)
    {
        float top=first==0?0:Lines[first].NativeTop;
        if(Height-top<=available+.01f)return new(this,first,Lines.Length,top,Height-top);
        if(!split)return null;
        int lower=0,upper=Breaks.Length;
        while(lower<upper)
        {
            int middle=lower+(upper-lower)/2;
            if(Lines[Breaks[middle]].NativeTop-top<=available+.01f)lower=middle+1;
            else upper=middle;
        }
        for(int i=lower-1;i>=0&&Breaks[i]>first;i--)
        {
            int end=Breaks[i];
            if(end-first==1&&Lines[first].ParagraphId==Lines[end].ParagraphId&&
                Lines[first].Native!.Properties.WidowControl)continue;
            return new(this,first,end,top,Lines[end].NativeTop-top);
        }
        return null;
    }
}
internal readonly record struct FootnoteSlice(FootnoteBlock Block,int First,int End,float Top,float Height);
internal sealed record FootnoteLayout(FootnoteBlock Separator,FootnoteBlock? Continuation,IReadOnlyDictionary<string,FootnoteBlock> Notes,
    bool BeneathText,float Left);
internal sealed record EndnoteGroup(FootnoteLayout Layout,ImmutableArray<FootnoteRequest> Requests);
internal sealed record PageFootnote(SemanticSource Source,SemanticSource? Call,PageRegion Region,IReadOnlyList<PageLine> Lines,bool Continued)
{
    internal bool IsEndnote {get;init;}
}

internal static partial class PageComposer
{
    static FootnoteRequests? ReadFootnoteRequests(SemanticDocument model,IReadOnlyDictionary<string,TextParagraph> paragraphs,bool endnote=false)
    {
        string kind=endnote?"endnote":"footnote";
        var references=model.References.Where(r=>r.Kind==kind).Take(129).ToArray();
        if(references.Length is 0 or >128)return null;
        string? settings=model.RelatedRoot("settings","settings");
        string? properties=settings is null?null:model.ChildId(settings,kind+"Pr");
        if(properties is not null)
        {
            var options=model.Children(properties).Select(e=>model.Name(e.Id)).Where(n=>n!=Word(kind)&&
                n is not ("#Whitespace" or "#SignificantWhitespace" or "#Comment")).ToArray();
            if(options.Distinct().Count()!=options.Length||
                options.Any(n=>n!=Word("pos")&&n!=Word("numStart")&&n!=Word("numFmt")&&n!=Word("numRestart")))
                return null;
        }
        string? mode=Option(properties,"pos");
        if((endnote?mode is not (null or "docEnd" or "sectEnd"):mode is not (null or "pageBottom" or "beneathText" or "sectEnd"))||
            !ReadNumbering(properties,null,out var global))
            return null;
        string? body=model.Stories.FirstOrDefault(s=>s.Kind=="body")?.RootId;
        var stories=model.Stories.Where(s=>s.Kind==kind).ToDictionary(s=>s.RootId,StringComparer.Ordinal);
        var localNumbering=new Dictionary<int,(string Format,string Restart,int Start)>();
        if(endnote)
        foreach(var owner in model.Sections)
        {
            string? local=owner.PropertiesId is {} id?model.ChildId(id,"endnotePr"):null;
            if(!ReadNumbering(local,global,out var numbering))return null;
            localNumbering.Add(owner.Number,numbering);
        }
        var definitions=new HashSet<string>(StringComparer.Ordinal);
        var selected=ImmutableArray.CreateBuilder<FootnoteRequest>();
        var sections=ImmutableHashSet.CreateBuilder<int>();
        var numbers=ImmutableDictionary.CreateBuilder<string,string>(StringComparer.Ordinal);
        var bodyBlocks=model.BodyBlocks.Select((id,index)=>(id,index)).ToDictionary(b=>b.id,b=>b.index,StringComparer.Ordinal);
        int section=0,nextNumber=global.Start;
        foreach(var reference in references.OrderBy(r=>model.Element(r.SourceId).Order))
        {
            if(reference is not {Status:ReferenceStatus.Resolved,TargetId:{} target}||
                model.Element(reference.SourceId).StoryId!=body||model.Revision(reference.SourceId)!="unchanged"||
                TextStyleResolver.OnOff(model.Attribute(reference.SourceId,"customMarkFollows")??"0") is not {} custom||
                !stories.TryGetValue(target,out var story)||model.Attribute(target,"type") is not (null or "normal")||!definitions.Add(target)||
                Ancestor(model,reference.SourceId,SemanticKind.Paragraph) is not {} paragraph||
                !paragraphs.TryGetValue(paragraph,out var text)||text.RightToLeft||
                !model.BlocksOf(target).All(b=>b.Kind==SemanticKind.Paragraph))
                return null;
            string? bodyBlock=paragraph;
            while(bodyBlock is not null&&!bodyBlocks.ContainsKey(bodyBlock))
                bodyBlock=model.ParentId(bodyBlock);
            int block=bodyBlock is null?-1:bodyBlocks[bodyBlock];
            var owner=model.Sections.FirstOrDefault(s=>block>=s.StartBlock&&block<s.EndBlockExclusive);
            if(owner is null)return null;
            var numbering=endnote?localNumbering[owner.Number]:global;
            if(section==0||section!=owner.Number&&numbering.Restart=="eachSect")nextNumber=numbering.Start;
            section=owner.Number;
            sections.Add(section);
            var calls=text.Controls.Where(c=>c.Source.NodeId==reference.SourceId).ToArray();
            if(calls is not [var call]||model.BlocksOf(target).FirstOrDefault().Id is not {} first||
                !paragraphs.TryGetValue(first,out var note)||note.RightToLeft||note.Context.Length==0)
                return null;
            if(custom)
            {
                if(call.Offset+1>=text.ContextLength||text.Context[call.Offset+1] is not ('*' or '†' or '‡')||
                    note.Context[0]!=text.Context[call.Offset+1])
                    return null;
            }
            else
            {
                if(note.Controls.Where(c=>c.Kind=="NoteMark").ToArray() is not [{Offset:0} mark]||
                    FormatPageNumber(nextNumber++,numbering.Format,new("NOTE",false,false,null,0)) is not {} label)
                    return null;
                numbers.Add(call.Source.NodeId,label);
                numbers.Add(mark.Source.NodeId,label);
            }
            selected.Add(new(model.Source(reference.SourceId),story,endnote&&mode!="sectEnd"?model.Sections.Length:owner.Number));
        }
        var separators=stories.Values.Where(s=>model.Attribute(s.RootId,"type")=="separator").ToArray();
        if(separators is not [var separator]||
            model.Source(separator.RootId).Location.PartUri!=model.Source(selected[0].Story.RootId).Location.PartUri||
            model.BlocksOf(separator.RootId).Any(part=>part.Kind!=SemanticKind.Paragraph))
            return null;
        var continuations=stories.Values.Where(s=>model.Attribute(s.RootId,"type")=="continuationSeparator").ToArray();
        if(continuations.Length>1||stories.Values.Any(s=>model.Attribute(s.RootId,"type")=="continuationNotice"))
            return null;
        var continuation=continuations.SingleOrDefault();
        if(continuation is not null&&(model.Source(continuation.RootId).Location.PartUri!=model.Source(separator.RootId).Location.PartUri||
            model.BlocksOf(continuation.RootId).Any(part=>part.Kind!=SemanticKind.Paragraph)))
            return null;
        if(!ReadRule(separator,"NoteSeparatorRule",out var rule)||!ReadRule(continuation,"NoteContinuationRule",out var continuationRule))
            return null;
        if(properties is not null&&model.Children(properties).Any(e=>model.Name(e.Id)==Word(kind)&&
            model.Attribute(e.Id,"id")!=model.Attribute(separator.RootId,"id")&&
            (continuation is null||model.Attribute(e.Id,"id")!=model.Attribute(continuation.RootId,"id"))))
            return null;
        // MS-OI29500 §17.18.34: Word uses beneathText placement for sectEnd footnotes.
        return new(selected.ToImmutable(),separator,continuation,mode is "beneathText" or "sectEnd",sections.ToImmutable(),numbers.ToImmutable(),
            rule,continuationRule);

        bool ReadRule(SemanticStory? story,string controlKind,out SemanticSource? source)
        {
            source=null;
            if(story is null)return true;
            var blocks=model.BlocksOf(story.RootId).Select(p=>paragraphs.GetValueOrDefault(p.Id)).ToArray();
            if(!blocks.SelectMany(p=>p?.Controls??[]).Any(c=>c.Kind==controlKind))return true;
            if(blocks is not [{RightToLeft:false,ContextLength:1,Controls:[var control]}]||
                control.Kind!=controlKind||model.Revision(control.Source.NodeId)!="unchanged")return false;
            source=control.Source;
            return true;
        }

        static string Word(string name)=>SemanticIndex.QName(SemanticIndex.W,name);
        string? Option(string? scope,string name)=>scope is not null&&model.ChildId(scope,name) is {} child?model.Attribute(child,"val"):null;

        bool ReadNumbering(string? scope,(string Format,string Restart,int Start)? inherited,out (string Format,string Restart,int Start) value)
        {
            string format=Option(scope,"numFmt")??inherited?.Format??(endnote?"lowerRoman":"decimal");
            // MS-OI29500 17.11.17/18: Word uses decimal for bullets in note numbering.
            if(format=="bullet")format="decimal";
            string restart=Option(scope,"numRestart")??inherited?.Restart??"continuous";
            bool numeric=ushort.TryParse(Option(scope,"numStart")??(inherited?.Start??1).ToString(CultureInfo.InvariantCulture),
                NumberStyles.None,CultureInfo.InvariantCulture,out ushort first);
            value=(format,restart,first);
            return numeric&&first>0&&restart is "continuous" or "eachSect"&&
                format is "decimal" or "upperRoman" or "lowerRoman" or "upperLetter" or "lowerLetter"&&
                (scope is null||new[]{"pos","numStart","numFmt","numRestart"}.All(name=>model.ChildId(scope,name) is null||Option(scope,name) is not null)&&
                    (model.ChildId(scope,"numFmt") is not {} numFmt||model.Attribute(numFmt,"format") is null));
        }
    }

    static FootnoteLayout PrepareFootnoteLayout(SemanticDocument model,FootnoteRequests requests,PageSection section,LayoutReader.Reader reader,
        LineComposer painter,IReadOnlyDictionary<string,TextParagraph> text,Action check,CancellationToken token,
        bool endnote=false,Action<FlowIssue>? diagnose=null)
    {
        var geometry=section.Format with{Top=0,Bottom=0};
        var prepared=new Dictionary<string,FootnoteBlock>(StringComparer.Ordinal);
        bool allowSplit=requests.Continuation is not null&&(endnote||requests.Notes.Length==1);
        foreach(var request in requests.Notes)prepared.Add(request.Call.NodeId,Prepare(request.Story,allowSplit));
        return new(Prepare(requests.Separator,rule:requests.SeparatorRule),
            requests.Continuation is {} continuation?Prepare(continuation,rule:requests.ContinuationRule):null,
            prepared,requests.BeneathText,geometry.Left);

        FootnoteBlock Prepare(SemanticStory story,bool split=false,SemanticSource? rule=null)
        {
            check();
            var layout=reader.ReadStory(story,split?geometry with{Height=Math.Max(14_400,geometry.Height)}:geometry);
            foreach(var issue in layout.Issues.Where(i=>i.BlocksRendering))diagnose?.Invoke(issue);
            if(layout.Issues.Any(i=>i.BlocksRendering)||layout.Paragraphs.Count==0)
                throw new InvalidDataException("PAGE_FOOTNOTE_CONTENT_UNQUALIFIED");
            foreach(var p in layout.Paragraphs)
            {
                string? obstacle=p.List is not null?"list":p.Properties.PageBreakBefore?"pageBreakBefore":
                    split&&p.Properties.KeepNext?"keepNext":!text.ContainsKey(p.Id)?"text":null;
                if(obstacle is null)continue;
                diagnose?.Invoke(new("PAGE_NOTE_PARAGRAPH_UNQUALIFIED",p.Source,
                    "Contrainte de paragraphe de note hors profil ("+obstacle+").",true));
                throw new InvalidDataException("PAGE_FOOTNOTE_CONTENT_UNQUALIFIED");
            }
            var measured=painter.Compose(_=>new(story.RootId,0,section.Body.Width),token,
                orderedParagraphs:layout.Paragraphs.Select(p=>text[p.Id]));
            foreach(var issue in measured.Lines.SelectMany(l=>l.Diagnostics).Where(BlocksLine))diagnose?.Invoke(issue);
            if(measured.Lines.IsEmpty||measured.Lines.Any(l=>l.Cut is LineCutKind.Page or LineCutKind.Column||
                l.Occupied is null||l.Diagnostics.Any(BlocksLine)))
            {
                diagnose?.Invoke(new("PAGE_NOTE_LINE_UNQUALIFIED",model.Source(story.RootId).Location,
                    "L’histoire contient une ligne sans mesure admissible ou un saut interne explicite.",true));
                throw new InvalidDataException("PAGE_FOOTNOTE_CONTENT_UNQUALIFIED");
            }
            var block=Composer.PlaceNativeLayout(layout,measured.Lines.GroupBy(l=>l.ParagraphId)
                .ToDictionary(g=>g.Key,g=>g.ToImmutableArray(),StringComparer.Ordinal),painter,check,()=>{});
            foreach(var issue in block.Issues.Where(i=>i.BlocksRendering))diagnose?.Invoke(issue);
            if(!block.Available||block.Pages is not [var page])
            {
                diagnose?.Invoke(new("PAGE_NOTE_MEASURE_UNQUALIFIED",model.Source(story.RootId).Location,
                    "La mesure de l’histoire donne "+block.Pages.Count.ToString(CultureInfo.InvariantCulture)+" régions de travail.",true));
                throw new InvalidDataException("PAGE_FOOTNOTE_CONTENT_UNQUALIFIED");
            }
            float height=page.Lines.Max(l=>l.NativeTop+l.Height)+layout.Paragraphs[^1].Properties.After;
            var lines=page.Lines.ToImmutableArray();
            if(rule is {} source)
            {
                if(lines is not [var line]||layout.Paragraphs[0].Properties is not {Alignment:"left",Left:0,Right:0,FirstLine:0}||
                    line.Native!.Parts is not [{Control.Style:{Color:"auto" or "000000",BaselineShift:0,Underline:"none"}}]||
                    line.Baseline-line.NativeTop<.25f||line.NativeTop+line.Height-line.Baseline<.25f)
                    throw new InvalidDataException("PAGE_FOOTNOTE_RULE_UNQUALIFIED");
                float width=Points(section.Body.Width);
                // The normal separator spans part of the measure. A continuation spans it all.
                lines=[line with{NoteRule=new(source,Math.Min(source==requests.SeparatorRule?144:width,width))}];
            }
            var breaks=split?NoteBreaks(lines):[];
            return new(model.Source(story.RootId),height,lines,breaks);
        }
    }

    static ImmutableArray<int> NoteBreaks(ImmutableArray<PageLine> lines)
    {
        var breaks=ImmutableArray.CreateBuilder<int>();
        int start=0;
        while(start<lines.Length)
        {
            int end=start+1;
            while(end<lines.Length&&lines[end].ParagraphId==lines[start].ParagraphId)end++;
            var properties=lines[start].Native!.Properties;
            if(!properties.KeepLines)
            {
                int minimum=properties.WidowControl?2:1;
                for(int cut=start+minimum;cut<=end-minimum;cut++)breaks.Add(cut);
            }
            if(end<lines.Length)breaks.Add(end);
            start=end;
        }
        return breaks.ToImmutable();
    }
}
