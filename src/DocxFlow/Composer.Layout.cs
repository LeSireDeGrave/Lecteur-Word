using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using SkiaSharp;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class Composer
{
    sealed class LayoutFailure(string code,SourceLocation source,string detail=""):Exception(code){public SourceLocation SourceLocation {get;}=source;public string Detail {get;}=detail;}
    sealed class LayoutFonts:IDisposable
    {
        readonly Dictionary<FontResolution,SKTypeface> faces=[];
        readonly Dictionary<(float Size,FontResolution Font),SKFont> fonts=[];
        public SKFont Get(LayoutStyle style)
        {
            var resolution=style.Font??throw new InvalidDataException("LAYOUT_FONT_UNRESOLVED");
            var key=(style.Size,resolution);if(fonts.TryGetValue(key,out var font))return font;
            if(fonts.Count>=512)throw new InvalidDataException("LAYOUT_FONT_LIMIT");
            if(!faces.TryGetValue(resolution,out var face)){face=FontResolver.Open(resolution);faces[resolution]=face;}
            font=new SKFont(face,style.Size);fonts[key]=font;return font;
        }
        public void Dispose(){foreach(var f in fonts.Values)f.Dispose();foreach(var f in faces.Values)f.Dispose();}
    }
    sealed record Glyph(LayoutRun Run,int Offset,string Text);
    sealed record MeasuredLine(IReadOnlyList<PageFragment> Fragments,string Text,float Width,float Height,float Ascent,string SourceBreak,bool PageBreak)
    {
        public IReadOnlyList<PageImage> Images {get;init;}=[];
        public IReadOnlyList<PageTab> Tabs {get;init;}=[];
        public ComposedLine? Native {get;init;}
        public bool ClipToLineBox {get;init;}
    }
    sealed record MeasuredParagraph(LayoutParagraph Paragraph,IReadOnlyList<MeasuredLine> Lines)
    {
        public float Height=>Lines.Sum(l=>l.Height);
    }
    public static Composition Compose(InspectionResult source,CancellationToken token=default)=>Compose(LayoutReader.Read(source,token),token);
    public static Composition Compose(PaginatedDocument document,CancellationToken token=default)
    {
        document=FontResolver.Resolve(document,token);
        try{return ComposeLayout(document,token) with{Profile=PaginatedDocument.Profile,Fonts=document.Fonts};}
        catch(LayoutFailure e){return new Composition([],[..document.Issues,new(e.Message,e.SourceLocation,"Composition refusée à cette provenance : contrainte du profil paginé. "+e.Detail,true)]){Profile=PaginatedDocument.Profile,Fonts=document.Fonts};}
        catch(InvalidDataException e){return new Composition([],[..document.Issues,new(e.Message,null,"Composition refusée : contrainte ou budget du profil paginé.",true)]){Profile=PaginatedDocument.Profile,Fonts=document.Fonts};}
    }
    static Composition ComposeLayout(PaginatedDocument document,CancellationToken token)
    {
        if(!document.Available)return new([],document.Issues);
        document=ApplyContextualSpacing(document);
        using var fonts=new LayoutFonts();var watch=Stopwatch.StartNew();
        void Check(){token.ThrowIfCancellationRequested();if(watch.ElapsedMilliseconds>15000)throw new InvalidDataException("LAYOUT_TIME_LIMIT");}
        document=FitTables(document,fonts,Check);
        var measured=new List<MeasuredParagraph>();int totalLines=0;
        foreach(var p in document.Paragraphs)
        {
            Check();IReadOnlyList<MeasuredLine> lines;
            try{lines=p.TableId is null?Measure(p,fonts,Check):[];}
            catch(InvalidDataException e){throw new LayoutFailure(e.Message,p.Source);}
            totalLines+=lines.Count;if(totalLines>50000)throw new InvalidDataException("LAYOUT_LINE_LIMIT");
            if((p.Properties.KeepLines||p.Properties.KeepNext)&&lines.Any(l=>l.PageBreak))throw new InvalidDataException("LAYOUT_KEEP_BREAK_CONFLICT");
            measured.Add(new(p,lines));
        }
        return PlaceMeasured(document,measured,fonts,Check);
    }

    static Composition PlaceMeasured(PaginatedDocument document,List<MeasuredParagraph> measured,LayoutFonts fonts,Action Check,
        bool native=false,Action? firstPage=null,IReadOnlyList<PageSection>? paper=null,
        Func<LayoutParagraph,LineRegion,LineResume,IReadOnlyList<MeasuredLine>>? reflow=null,BandTrial? bandTrial=null,
        FootnoteLayout? footnotes=null,PageRegionReplay? replay=null,List<PageRegionCheckpoint>? checkpoints=null,
        IReadOnlySet<string>? regionalTables=null,Func<string,long,MeasuredTable>? tableAt=null,
        IReadOnlyDictionary<int,EndnoteGroup>? endnotes=null,bool completeBody=false,bool textBoxBorders=false)
    {
        var allMeasured=measured.ToDictionary(m=>m.Paragraph.Id);
        var measuredTables=document.Tables.ToDictionary(t=>t.Id,t=>MeasureTable(t,allMeasured,Check,reflow));
        measured=measured.Where(m=>m.Paragraph.CellOwner is null).ToList();
        if(measured.Count==0)return new([], [..document.Issues,new("LAYOUT_NO_PARAGRAPHS",null,"Aucun paragraphe qualifié à composer.",true)]);
        var keep=native&&measured.Any(p=>p.Paragraph.Properties.KeepNext)?PlanNativeKeeps(measured,measuredTables,Check,
            footnotes is null?null:NoteHeight):null;
        var placementIssues=new List<FlowIssue>();
        int oversizedThrough=-1;
        var pages=replay is null?new List<ComposedPage>():new(replay.Prefix.Take(replay.Checkpoint.Sheets));
        var current=new List<PageLine>();var currentCells=new List<PageCell>();
        int startBlock=replay?.Checkpoint.BlockIndex??0;
        if(replay is not null&&(startBlock>=measured.Count||measured[startBlock].Paragraph.Id!=replay.Checkpoint.BlockId||
            pages.Count!=replay.Checkpoint.Sheets))
            throw new InvalidDataException("PAGE_REGION_CHECKPOINT_INVALID");
        var format=measured[startBlock].Paragraph.Geometry;
        int section=measured[startBlock].Paragraph.Section,pageSection=section;
        int previousPageSection=replay?.Checkpoint.PreviousSection??0;
        int? lastPageNumber=replay is null?0:replay.Checkpoint.PrintedNumber;
        PageGeometry geometry=GeometryOnNextPage();
        float cursor=geometry.Top,after=0;
        int balancedSection=0,balancedSheet=-1;
        float balancedHeight=0;
        float bandTop=geometry.Top,bandBottom=PageBandBottom(geometry.Top),bandUsed=bandTop;
        int column=0,band=0,firstLine=0,firstCell=0;
        bool finalColumnBreak=false;
        var notesOnPage=new List<(SemanticSource Call,FootnoteSlice Slice)>();
        var endnotesOnPage=new List<PageFootnote>();
        (SemanticSource Call,FootnoteBlock Block,int First)? noteCarry=null;
        float reservedNotes=0;
        float sideBottomOnPage=0;
        float borderBottomOnPage=0;
        var sideOnRegion=new List<(SideExclusion Shape,float Top,float Bottom)>();
        var alternateLines=new Dictionary<(string,LineRegion,LineResume),IReadOnlyList<MeasuredLine>>();
        var alternateTables=new Dictionary<(string,long),MeasuredTable>();
        FlowColumns? Columns()=>paper?[section-1].ColumnLayout;
        float RegionHeight()=>bandBottom-bandTop;
        float RegionLeft()=>Columns() is {} plan?geometry.Left+PageComposer.Points(plan.Columns[column].Offset):geometry.Left;
        float RegionRight()=>Columns() is {} plan?RegionLeft()+PageComposer.Points(plan.Columns[column].Width):geometry.Width-geometry.Right;
        (float Left,float Right) InkBounds()
        {
            if(Columns() is not {} plan)return (0,geometry.Width);
            float left=column==0?0:(RegionLeft()+geometry.Left+
                PageComposer.Points(plan.Columns[column-1].Offset+plan.Columns[column-1].Width))/2;
            float right=column+1==plan.Columns.Length?geometry.Width:(RegionRight()+geometry.Left+
                PageComposer.Points(plan.Columns[column+1].Offset))/2;
            return (left,right);
        }
        bool HasContent()=>current.Count>0||currentCells.Count>0||endnotesOnPage.Count>0;
        bool RegionHasContent()=>current.Count>firstLine||currentCells.Count>firstCell;
        PageGeometry GeometryOnNextPage()
        {
            if(paper is null)return format;
            var page=paper[section-1];
            bool first=section!=previousPageSection;
            return page.GeometryOn(pages.Count+1,first,first&&page.PageNumberStart is {} restart?restart:lastPageNumber+1);
        }
        float PageBandBottom(float top)
        {
            if(bandTrial is {} trial)
            {
                if(pages.Count==trial.FinalSheet)return top+trial.FinalHeight;
                if(pages.Count==0)return top+trial.FirstHeight;
            }
            if(section==balancedSection&&pages.Count==balancedSheet)return top+balancedHeight;
            return geometry.Height-geometry.Bottom;
        }
        PlacedFlowRegion? RegionOccurrence()=>Columns() is {} layout?new(section,band,column,
            new(PageComposer.Emu(RegionLeft()),PageComposer.Emu(bandTop),layout.Columns[column].Width,PageComposer.Emu(RegionHeight())),layout.Source):null;
        void ResetRegion()
        {
            firstLine=current.Count;firstCell=currentCells.Count;
            cursor=bandTop;after=0;
            sideOnRegion.Clear();
        }
        void ResetPage()
        {
            pageSection=section;
            geometry=GeometryOnNextPage();
            band=column=0;
            bandTop=bandUsed=geometry.Top;
            bandBottom=PageBandBottom(bandTop);
            ResetRegion();
        }
        bool NextRegion(bool force=false)
        {
            if(Columns() is {} layout&&column+1<layout.Columns.Length)
            {
                bandUsed=Math.Max(bandUsed,cursor);
                column++;ResetRegion();
                return false;
            }
            int before=pages.Count;
            Page(force);
            return pages.Count>before;
        }
        void Page(bool force=false,SemanticSource? intercalary=null)
        {
            Check();if(!HasContent()&&notesOnPage.Count==0&&!force)return;
            if(bandTrial is {} trial&&pages.Count>=trial.SheetLimit)
                throw new InvalidDataException("PAGE_BALANCE_TRIAL_EXHAUSTED");
            if(pages.Count>=500)throw new InvalidDataException("LAYOUT_PAGE_LIMIT");
            ValidateCellBorders(currentCells,Check);
            if(paper is not null)
            {
                var settings=paper[pageSection-1];
                lastPageNumber=pageSection!=previousPageSection&&settings.PageNumberStart is {} restart?
                    restart:lastPageNumber+1;
                if(settings.RunningIssues.Any(i=>i.Code is "PAGE_NUMBERING_INVALID" or "PAGE_STORY_SETTING_AMBIGUOUS"))
                    lastPageNumber=null;
                previousPageSection=pageSection;
            }
            pages.Add(new(pages.Count+1,current.ToImmutableArray())
            {
                Geometry=geometry,Section=pageSection,Cells=currentCells.ToImmutableArray(),IntercalarySectionBreak=intercalary,
                PrintedPageNumber=paper is not null&&intercalary is null?lastPageNumber:null,
                Footnotes=endnotesOnPage.Count==0?PositionNotes():[..endnotesOnPage,..PositionNotes()]
            });
            current.Clear();currentCells.Clear();notesOnPage.Clear();endnotesOnPage.Clear();
            reservedNotes=sideBottomOnPage=borderBottomOnPage=0;ResetPage();
            ContinueNote();
            if(pages.Count==1)firstPage?.Invoke();
        }

        IEnumerable<(SemanticSource Call,FootnoteBlock Block)> NotesIn(MeasuredLine line)
        {
            if(footnotes is null||line.Native is not {} nativeLine)yield break;
            foreach(var part in nativeLine.Parts)
                if(part.Control is {Kind:"NoteCall"} call&&footnotes.Notes.TryGetValue(call.Source.NodeId,out var block))
                    yield return (call.Source,block);
        }
        float NoteHeight(MeasuredLine line)=>NotesIn(line).Sum(n=>n.Block.Height);
        float NoteHeightIn(MeasuredParagraph paragraph,int first,int bound)
        {
            if(footnotes is null)return 0;
            float height=0;
            for(int i=first;i<bound;i++){Check();height+=NoteHeight(paragraph.Lines[i]);}
            return height;
        }
        float NoteReservation(float height,float existing)=>
            height>0&&existing==0?height+footnotes!.Separator.Height:height+existing;

        float ContentBottom()=>Math.Max(Math.Max(
            current.Count==0?geometry.Top:current.Max(l=>l.NativeTop+l.Height),
            currentCells.Count==0?geometry.Top:currentCells.Max(c=>c.Y+c.Height+(c.Borders.Bottom?.Width??0)/2)),
            Math.Max(sideBottomOnPage,borderBottomOnPage));

        IReadOnlyList<PageFootnote> PositionNotes()
        {
            if(notesOnPage.Count==0)return [];
            float bodyEnd=ContentBottom();
            if(endnotesOnPage.LastOrDefault() is {} tail)
                bodyEnd=Math.Max(bodyEnd,PageComposer.Points(tail.Region.Top+tail.Region.Height));
            float y=footnotes!.BeneathText?bodyEnd:geometry.Height-geometry.Bottom-reservedNotes;
            if(y<bodyEnd-.01f||y+reservedNotes>geometry.Height-geometry.Bottom+.01f)
                throw new LayoutFailure("PAGE_FOOTNOTE_RESERVATION_INVALID",notesOnPage[0].Call.Location);
            var positioned=new List<PageFootnote>(notesOnPage.Count+1);
            var separator=notesOnPage[0].Slice.First==0?footnotes.Separator:footnotes.Continuation!;
            Add(new(separator,0,separator.Lines.Length,0,separator.Height),null);
            foreach(var (call,slice) in notesOnPage)Add(slice,call);
            return positioned.ToImmutableArray();

            void Add(FootnoteSlice slice,SemanticSource? call)
            {
                positioned.Add(PositionNote(slice,call,y,footnotes!.Left,false));
                y+=slice.Height;
            }
        }

        PageFootnote PositionNote(FootnoteSlice slice,SemanticSource? call,float top,float measuredLeft,bool endnote)
        {
            float dx=geometry.Left-measuredLeft;
            float dy=top-slice.Top;
            bool repeated=call is null&&pages.Any(p=>p.Footnotes.Any(note=>note.Source==slice.Block.Source));
            return new(slice.Block.Source,call,new(PageComposer.Emu(geometry.Left),PageComposer.Emu(top),
                PageComposer.Emu(geometry.Width-geometry.Left-geometry.Right),PageComposer.Emu(slice.Height)),
                slice.Block.Lines.Skip(slice.First).Take(slice.End-slice.First).Select(l=>l with
                {
                    Section=pageSection,X=l.X+dx,NativeOffsetX=l.NativeOffsetX+dx,
                    Baseline=l.Baseline+dy,NativeTop=l.NativeTop+dy,IsRepeated=repeated,
                    Images=l.Images.Select(i=>i with{X=i.X+dx,Y=i.Y+dy}).ToImmutableArray(),
                    Tabs=l.Tabs.Select(t=>t with{From=t.From+dx,To=t.To+dx}).ToImmutableArray(),
                    ParagraphBorder=l.ParagraphBorder is {} border?
                        border with{Left=border.Left+dx,Right=border.Right+dx,Top=border.Top+dy}:null
                }).ToImmutableArray(),slice.First!=0){IsEndnote=endnote};
        }

        bool FlushEndnotes()
        {
            if(endnotes?.GetValueOrDefault(section) is not {} group)return false;
            float y=ContentBottom();
            foreach(var request in group.Requests)
            {
                var block=group.Layout.Notes[request.Call.NodeId];
                int first=0;
                while(first<block.Lines.Length)
                {
                    Check();
                    if(notesOnPage.Count>0&&footnotes!.BeneathText)
                        throw new LayoutFailure("PAGE_NOTE_ORDER_UNQUALIFIED",request.Call.Location);
                    var separator=endnotesOnPage.Count!=0?null:first==0?group.Layout.Separator:group.Layout.Continuation;
                    if(first!=0&&separator is null)
                        throw new LayoutFailure("PAGE_ENDNOTE_CONTINUATION_MISSING",request.Call.Location);
                    float separatorHeight=separator?.Height??0;
                    var fitted=block.Fit(first,geometry.Height-geometry.Bottom-reservedNotes-y-separatorHeight,
                        split:group.Layout.Continuation is not null);
                    if(fitted is not {} slice)
                    {
                        if(!HasContent()&&notesOnPage.Count==0)
                            throw new LayoutFailure("PAGE_ENDNOTE_TOO_TALL",request.Call.Location);
                        Page();y=geometry.Top;
                        continue;
                    }
                    if(separator is not null)
                    {
                        endnotesOnPage.Add(PositionNote(new(separator,0,separator.Lines.Length,0,separator.Height),null,y,
                            group.Layout.Left,true));
                        y+=separator.Height;
                    }
                    endnotesOnPage.Add(PositionNote(slice,request.Call,y,group.Layout.Left,true));
                    y+=slice.Height;
                    first=slice.End;
                    if(first<block.Lines.Length){Page();y=geometry.Top;}
                }
            }
            return true;
        }
        FootnoteSlice? FitNote(FootnoteBlock block,float available,float bodyLineHeight)
        {
            float separator=footnotes!.Separator.Height;
            bool split=footnotes.Continuation is not null&&footnotes.Notes.Count==1&&
                block.Height+separator+bodyLineHeight>RegionHeight()+.01f;
            return block.Fit(0,available,split);
        }
        void CommitNote(SemanticSource call,FootnoteSlice slice)
        {
            notesOnPage.Add((call,slice));
            reservedNotes+=slice.Height;
            noteCarry=slice.End<slice.Block.Lines.Length?(call,slice.Block,slice.End):null;
        }
        void ContinueNote()
        {
            if(noteCarry is not {} carry)return;
            reservedNotes=footnotes!.Continuation!.Height;
            var slice=carry.Block.Fit(carry.First,RegionHeight()-reservedNotes,split:true)??
                throw new LayoutFailure("PAGE_FOOTNOTE_CONTINUATION_TOO_TALL",carry.Call.Location);
            CommitNote(carry.Call,slice);
        }

        void BalanceBand(int index)
        {
            if(bandTrial is not null||Columns() is not {Balance:true,Columns.Length:>1} layout)return;
            int end=index;
            while(end<measured.Count&&measured[end].Paragraph.Section==section){Check();end++;}
            if(end<measured.Count&&measured[end].Paragraph.SectionStart=="nextColumn")
                throw new LayoutFailure("PAGE_COLUMN_BALANCE_UNQUALIFIED",layout.Source.Location);

            var inputs=measured.GetRange(index,end-index);
            var ids=inputs.Where(p=>p.Paragraph.TableId is not null).Select(p=>p.Paragraph.TableId!).ToHashSet(StringComparer.Ordinal);
            var tables=document.Tables.Where(t=>ids.Contains(t.Id)).ToArray();
            foreach(string id in tables.SelectMany(t=>t.Cells).SelectMany(c=>c.ParagraphIds).Distinct(StringComparer.Ordinal))
                if(allMeasured.TryGetValue(id,out var cell))inputs.Add(cell);
            var trialDocument=document with{Tables=tables};
            float firstHeight=RegionHeight();
            const int sheetLimit=16;
            int sheets=CountSheets(new(firstHeight,-1,0,sheetLimit));
            if(sheets==0)
                throw new LayoutFailure("PAGE_COLUMN_BALANCE_TRIAL_UNQUALIFIED",layout.Source.Location);
            if(sheets>1&&(measured[end-1].Lines.LastOrDefault() is {PageBreak:true}||
                measured[end-1].Lines.LastOrDefault()?.Native?.Cut==LineCutKind.Column))
                throw new LayoutFailure("PAGE_COLUMN_BALANCE_FINAL_BREAK_UNQUALIFIED",layout.Source.Location);
            int finalSheet=sheets-1;
            float lower=0,upper=sheets==1?firstHeight:geometry.BodyHeight;
            for(int pass=0;pass<16&&upper-lower>.05f;pass++)
            {
                Check();float middle=(upper+lower)/2;
                if(CountSheets(new(firstHeight,finalSheet,middle,sheets))==sheets)upper=middle;
                else lower=middle;
            }
            balancedSheet=pages.Count+finalSheet;
            balancedSection=section;
            balancedHeight=upper;
            if(finalSheet==0)bandBottom=bandTop+upper;

            int CountSheets(BandTrial trial)
            {
                try
                {
                    return PlaceMeasured(trialDocument,inputs,fonts,Check,native:true,paper:paper,reflow:reflow,bandTrial:trial,
                        regionalTables:regionalTables,tableAt:tableAt).Pages.Count;
                }
                catch(LayoutFailure error) when(error.Message is "LAYOUT_LINE_TOO_TALL" or "LAYOUT_KEEP_LINES_TOO_LARGE" or
                    "PAGE_ROW_FIRST_FRAGMENT_TOO_TALL" or "PAGE_ROW_FRAGMENT_TOO_TALL" or "PAGE_ROW_CANT_SPLIT_OVERFLOW"){return 0;}
                catch(InvalidDataException error) when(error.Message is "PAGE_BALANCE_TRIAL_EXHAUSTED" or
                    "LAYOUT_WIDOW_UNSATISFIABLE" or "LAYOUT_TABLE_HEADER_TOO_TALL" or
                    "LAYOUT_TABLE_HEADER_BODY_TOO_TALL" or "LAYOUT_TABLE_ROW_GROUP_TOO_TALL"){return 0;}
            }
        }

        BalanceBand(startBlock);
        for(int pi=startBlock;pi<measured.Count;pi++)
        {
            Check();var item=measured[pi];var p=item.Paragraph;var props=p.Properties;
            if(p.Section!=section)
            {
                string start=p.SectionStart;
                bool tailPlaced=FlushEndnotes();
                var nextColumns=paper?[p.Section-1].ColumnLayout;
                bool newBand=start=="continuous"&&(!PageComposer.SameColumns(Columns(),nextColumns)||
                    Columns() is {Balance:true,Columns.Length:>1}||nextColumns is {Balance:true,Columns.Length:>1});
                if(tailPlaced||start=="nextPage"||paper is not null&&(start is "oddPage" or "evenPage"))
                {
                    Page(native&&!HasContent()&&measured[pi-1].Lines.LastOrDefault()?.PageBreak==true);
                    while(notesOnPage.Count>0)Page();
                    if(start is "oddPage" or "evenPage")
                    {
                        var source=paper![p.Section-1].Source??throw new LayoutFailure("PAGE_SECTION_BREAK_SOURCE_MISSING",p.Source);
                        if(lastPageNumber is not {} number)throw new LayoutFailure("PAGE_SECTION_PARITY_UNRESOLVED",source.Location);
                        if((number+1)%2!=(start=="oddPage"?1:0))Page(true,source);
                    }
                }
                else if(paper is not null&&start=="nextColumn")
                    NextRegion(force:true);
                else if(start!="continuous"||p.Geometry!=format)
                    throw new LayoutFailure("LAYOUT_CONTINUOUS_GEOMETRY_UNQUALIFIED",p.Source);
                float nextTop=Math.Max(bandUsed,cursor);
                if(newBand&&nextTop>=geometry.Height-geometry.Bottom-.01f)Page();
                format=p.Geometry;section=p.Section;
                if(!HasContent())ResetPage();
                else if(newBand)
                {
                    band++;column=0;
                    bandTop=bandUsed=nextTop;bandBottom=geometry.Height-geometry.Bottom;
                    ResetRegion();
                }
                if(!HasContent()||newBand)BalanceBand(pi);
            }
            finalColumnBreak=false;
            if(p.TableId is null&&props.PageBreakBefore&&!(replay is not null&&pi==startBlock))
                Page();
            if(checkpoints is not null&&pages.Count>0&&!HasContent()&&notesOnPage.Count==0&&
                placementIssues.Count==0&&oversizedThrough<pi&&band==0&&column==0&&cursor==geometry.Top&&
                (checkpoints.Count==0||checkpoints[^1].Sheets<pages.Count))
                checkpoints.Add(new(p.Id,pi,pages.Count,previousPageSection,lastPageNumber));
            if(p.TableId is {} tableId)
            {
                var table=measuredTables[tableId];
                float width=table.Table.Columns.Sum();
                float edge=paper is null?0:AllBorders(table.Table.Borders)
                    .Concat(table.Table.Cells.SelectMany(c=>AllBorders(c.Borders))).Max(b=>b?.Width??0)/2;
                int headers=table.Table.Rows.TakeWhile(r=>r.Header).Count();float headerHeight=table.Heights.Take(headers).Sum();
                if(footnotes is not null&&headers>0&&TableLines(table,0,headers-1).SelectMany(NotesIn).Any())
                    throw new LayoutFailure("PAGE_FOOTNOTE_REPEAT_HEADER_UNQUALIFIED",table.Table.Source);
                if(regionalTables?.Contains(tableId)==true)
                {
                    if(keep?[pi] is not null)throw new LayoutFailure("PAGE_TABLE_KEEP_REGION_UNQUALIFIED",p.Source);
                    PlaceRegionalGroups();
                    after=0;
                    continue;
                }
                ClearTableExclusion(ref after);
                if(headerHeight>RegionHeight()+.01f)throw new InvalidDataException("LAYOUT_TABLE_HEADER_TOO_TALL");
                var firstBody=table.Groups.FirstOrDefault(g=>g.First>=headers);
                var firstFlow=native&&paper is not null&&headers<table.Table.Rows.Count&&CanFragmentRow(table,firstBody)?
                    PrepareRowFlow(table,firstBody.First,Check):null;
                float fullFirstBody=headers<table.Table.Rows.Count?
                    table.Heights.Skip(firstBody.First).Take(firstBody.Last-firstBody.First+1).Sum():0;
                float firstBodyHeight=firstFlow is not null?MinimumRowStart(firstFlow):
                    CanSplitMerge(firstBody,fullFirstBody)?FirstMergeHeight(firstBody):fullFirstBody;
                var openingNotes=NotesForRows(firstBody.First,firstBody.Last);
                if(openingNotes.Length>0)firstBodyHeight=fullFirstBody;
                float openingEnd=cursor+after+headerHeight+firstBodyHeight;
                if(headers>0&&headerHeight+firstBodyHeight>RegionHeight()+.01f)
                {
                    var first=table.Table.Rows[firstBody.First];
                    if(first.CantSplit&&native&&paper is not null&&firstBodyHeight>RegionHeight()+.01f)
                        throw new LayoutFailure("PAGE_ROW_CANT_SPLIT_OVERFLOW",first.Source??table.Table.Source);
                    throw new InvalidDataException("LAYOUT_TABLE_HEADER_BODY_TOO_TALL");
                }
                if(cursor+after+headerHeight+firstBodyHeight>TableBottom()+.01f||
                    openingNotes.Length>0&&!FitTableNotes(openingNotes,openingEnd,firstBodyHeight,out _))
                {
                    NextRegion();
                    while(reservedNotes>0&&cursor+headerHeight+firstBodyHeight>TableBottom()+.01f)NextRegion();
                    if(openingNotes.Length>0)
                        while(!FitTableNotes(openingNotes,cursor+headerHeight+firstBodyHeight,firstBodyHeight,out _))
                        {
                            if(!RegionHasContent()&&notesOnPage.Count==0)
                                throw new LayoutFailure("PAGE_FOOTNOTE_TABLE_ROWS_TOO_TALL",openingNotes[0].Call.Location);
                            NextRegion();
                        }
                }
                else cursor+=after;
                after=0;
                foreach(var group in table.Groups)
                {
                    Check();float height=table.Heights.Skip(group.First).Take(group.Last-group.First+1).Sum();
                    float following=0;
                    if(group.Last==table.Table.Rows.Count-1&&keep?[pi] is {} rowKeep)
                    {
                        following=rowKeep.Following;
                        if(height+headerHeight+following>RegionHeight()+.01f)
                            throw new LayoutFailure("PAGE_TABLE_KEEP_NEXT_OVERFLOW",table.Table.Source);
                    }
                    var rowNotes=NotesForRows(group.First,group.Last);
                    if(rowNotes.Length>0)
                    {
                        float repeat=0;
                        List<(SemanticSource Call,FootnoteSlice Slice)>? pending;
                        while(!FitTableNotes(rowNotes,cursor+repeat+height+following,height,out pending))
                        {
                            if(!RegionHasContent()&&notesOnPage.Count==0)
                                throw new LayoutFailure("PAGE_FOOTNOTE_TABLE_ROWS_TOO_TALL",rowNotes[0].Call.Location);
                            NextRegion();
                            repeat=group.First>=headers?headerHeight:0;
                        }
                        if(repeat>0){Rows(0,headers-1,true);cursor+=repeat;}
                        Rows(group.First,group.Last,false);cursor+=height;
                        if(notesOnPage.Count==0)reservedNotes=footnotes!.Separator.Height;
                        foreach(var note in pending!)CommitNote(note.Call,note.Slice);
                        continue;
                    }
                    if(cursor+height>TableBottom()+.01f&&native&&paper is not null&&CanFragmentRow(table,group))
                    {
                        var flow=group==firstBody&&firstFlow is not null?firstFlow:PrepareRowFlow(table,group.First,Check);
                        if(MinimumRowStart(flow)>RegionHeight()-headerHeight+.01f)
                            throw new LayoutFailure("PAGE_ROW_FIRST_FRAGMENT_TOO_TALL",table.Table.Rows[group.First].Source??table.Table.Source);
                        SplitRow(group.First,flow);
                        continue;
                    }
                    if(CanSplitMerge(group,height))
                    {
                        SplitMergedGroup(group);
                        continue;
                    }
                    if(height>RegionHeight()+.01f)
                    {
                        var row=table.Table.Rows[group.First];
                        if(row.CantSplit&&native&&paper is not null)
                            throw new LayoutFailure("PAGE_ROW_CANT_SPLIT_OVERFLOW",row.Source??table.Table.Source);
                        throw new InvalidDataException("LAYOUT_TABLE_ROW_GROUP_TOO_TALL");
                    }
                    if(cursor+height+following>TableBottom()+.01f)
                    {
                        NextRegion();
                        float repeat=group.First>=headers?headerHeight:0;
                        while(reservedNotes>0&&cursor+height+repeat+following>TableBottom()+.01f)NextRegion();
                        if(group.First>=headers&&headers>0)
                        {
                            if(headerHeight+height>RegionHeight()+.01f)throw new InvalidDataException("LAYOUT_TABLE_HEADER_BODY_TOO_TALL");
                            Rows(0,headers-1,true);cursor+=headerHeight;
                        }
                    }
                    Rows(group.First,group.Last,false);cursor+=height;
                }
                continue;

                float GroupHeight(int first,int last)=>table.Offsets[last+1]-table.Offsets[first];
                (SemanticSource Call,FootnoteBlock Block)[] NotesForRows(int first,int last)=>
                    footnotes is null?[]:TableLines(table,first,last).SelectMany(NotesIn).ToArray();

                bool FitTableNotes((SemanticSource Call,FootnoteBlock Block)[] calls,float bodyEnd,float height,
                    out List<(SemanticSource Call,FootnoteSlice Slice)>? pending)
                {
                    pending=null;
                    float end=Math.Max(Math.Max(sideBottomOnPage,borderBottomOnPage),bodyEnd+edge);
                    float reserve=reservedNotes==0?footnotes!.Separator.Height:reservedNotes;
                    foreach(var call in calls)
                    {
                        Check();
                        if(FitNote(call.Block,bandBottom-end-reserve,height+edge) is not {} slice)return false;
                        (pending??=[]).Add((call.Call,slice));
                        reserve+=slice.Height;
                    }
                    return true;
                }
                bool CanSplitMerge((int First,int Last) group,float height)=>
                    native&&paper is not null&&group.First>=headers&&group.First<group.Last&&
                    height>RegionHeight()-headerHeight+.01f&&
                    table.Cells.Values.Any(c=>c.Cell.Row>=group.First&&c.Cell.RowSpan>1&&c.Cell.Row<=group.Last);
                float FirstMergeHeight((int First,int Last) group)
                {
                    for(int cut=group.First+1;cut<=group.Last;cut++)
                        if(CanCutMergedCells(table,cut,Check))return GroupHeight(group.First,cut-1);
                    return GroupHeight(group.First,group.Last);
                }
                void SplitMergedGroup((int First,int Last) group)
                {
                    int first=group.First;
                    bool repeat=false;
                    while(first<=group.Last)
                    {
                        Check();
                        float available=TableBottom()-cursor-(repeat?headerHeight:0);
                        int last=first-1;
                        for(int row=first;row<=group.Last&&GroupHeight(first,row)<=available+.01f;row++)
                        {
                            Check();
                            if(row==group.Last||CanCutMergedCells(table,row+1,Check))last=row;
                        }
                        if(last<first)
                        {
                            if(!RegionHasContent()&&notesOnPage.Count==0)
                                throw new LayoutFailure("PAGE_CELL_FRAGMENT_NO_CUT",
                                    table.Table.Rows[first].Source??table.Table.Source);
                            NextRegion();repeat=headers>0;
                            continue;
                        }
                        if(repeat){Rows(0,headers-1,true);cursor+=headerHeight;}
                        Rows(first,last,false,splitMerges:true);
                        cursor+=GroupHeight(first,last);
                        first=last+1;
                        if(first<=group.Last){NextRegion();repeat=headers>0;}
                    }
                }

                void SplitRow(int row,IReadOnlyList<RowCellFlow> flow)
                {
                    var indices=new int[flow.Count];int fragment=0;
                    bool repeatHeader=false;
                    while(true)
                    {
                        Check();
                        float available=TableBottom()-cursor-(repeatHeader?headerHeight:0);
                        if(SliceRow(flow,indices,available,Check) is not {} slice)
                        {
                            if(!RegionHasContent()&&notesOnPage.Count==0)
                                throw new LayoutFailure("PAGE_ROW_FRAGMENT_TOO_TALL",table.Table.Rows[row].Source??table.Table.Source);
                            NextRegion();repeatHeader=headers>0;
                            continue;
                        }
                        if(repeatHeader){Rows(0,headers-1,true);cursor+=headerHeight;}
                        PlaceRowFragment(table,row,fragment++,slice,TableX(),cursor,RegionOccurrence(),current,currentCells,Check);
                        cursor+=slice.Height;
                        if(slice.Last)return;
                        for(int i=0;i<indices.Length;i++)indices[i]=slice.Cells[i].End;
                        NextRegion();repeatHeader=headers>0;
                    }
                }

                float TableBottom()=>reservedNotes==0?bandBottom:bandBottom-reservedNotes-edge;
                void ClearTableExclusion(ref float spacing)
                {
                    if(sideOnRegion.Count==0)return;
                    long left=PageComposer.Emu(TableX()-RegionLeft()-edge);
                    long right=left+PageComposer.Emu(width+2*edge);
                    float start=cursor+spacing,clear=start;
                    foreach(var active in sideOnRegion)
                    {
                        Check();
                        if(active.Shape.Left<right&&left<active.Shape.Right)clear=Math.Max(clear,active.Bottom);
                    }
                    if(clear>start){cursor=clear;spacing=0;}
                }

                void PlaceRegionalGroups()
                {
                    var columns=Columns()!;
                    var groups=table.Groups;
                    for(int i=0;i<groups.Count;i++)
                    {
                        var group=groups[i];
                        bool repeat=false;
                        float spacing=i==0?after:0;
                        int tried=0;
                        while(true)
                        {
                            Check();
                            long regionWidth=columns.Columns[column].Width;
                            if(regionWidth==columns.Narrowest)
                                table=measuredTables[tableId];
                            else if(alternateTables.TryGetValue((tableId,regionWidth),out var cached))
                                table=cached;
                            else
                                alternateTables.Add((tableId,regionWidth),table=tableAt!(tableId,regionWidth));
                            width=table.Table.Columns.Sum();
                            headerHeight=table.Heights.Take(headers).Sum();
                            ClearTableExclusion(ref spacing);
                            float height=table.Heights.Skip(group.First).Take(group.Last-group.First+1).Sum();
                            float required=height+(repeat?headerHeight:0);
                            if(i==0&&headers>0&&groups.Count>1)
                            {
                                var body=groups[1];
                                required+=table.Heights.Skip(body.First).Take(body.Last-body.First+1).Sum();
                            }
                            if(cursor+spacing+required<=TableBottom()+.01f)
                            {
                                cursor+=spacing;
                                if(repeat){Rows(0,headers-1,true);cursor+=headerHeight;}
                                Rows(group.First,group.Last,false);cursor+=height;
                                break;
                            }
                            if(!RegionHasContent()&&++tried>=columns.Columns.Length)
                                throw new LayoutFailure("PAGE_TABLE_REGIONAL_ROW_TOO_TALL",
                                    table.Table.Rows[group.First].Source??table.Table.Source);
                            NextRegion();
                            spacing=0;
                            repeat=group.First>=headers&&headers>0;
                        }
                    }
                }

                float TableX()
                {
                    var region=RegionOccurrence();
                    var tableGeometry=region is null?geometry:geometry with{Left=RegionLeft(),Right=geometry.Width-RegionRight()};
                    float x=TableLeft(table.Table,tableGeometry,width);
                    float left=region is null?0:RegionLeft(),right=region is null?geometry.Width:RegionRight();
                    if(paper is not null&&(x-edge<left-.01f||x+width+edge>right+.01f))
                        throw new LayoutFailure(region is null?"PAGE_TABLE_OUTSIDE_PAPER":"PAGE_TABLE_OUTSIDE_COLUMN",table.Table.Source);
                    return x;
                }

                void Rows(int start,int end,bool repeat,bool splitMerges=false)
                {
                    var region=RegionOccurrence();
                    int linesBefore=current.Count,cellsBefore=currentCells.Count;
                    PlaceTableRows(table,start,end,TableX(),cursor,repeat,current,currentCells,fonts,Check,splitMerges);
                    if(region is null)return;
                    for(int i=linesBefore;i<current.Count;i++)current[i]=current[i] with{FlowRegion=region};
                    for(int i=cellsBefore;i<currentCells.Count;i++)currentCells[i]=currentCells[i] with{FlowRegion=region};
                }
            }
            float reserve=p.FlowExclusion is {} exclusion?PageComposer.Points(exclusion.Height):0;
            var paragraphBorder=props.BottomBorder;
            if(!textBoxBorders&&paragraphBorder is not null&&pi+1<measured.Count&&measured[pi+1].Paragraph is {} successor&&
                successor.TableId is null&&successor.Section==p.Section&&!successor.Properties.PageBreakBefore&&
                successor.Properties.BottomBorder is {} adjacent&&(adjacent with{Source=paragraphBorder.Source})==paragraphBorder&&
                successor.Properties.Left==props.Left&&successor.Properties.Right==props.Right)
                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_GROUP_UNQUALIFIED",paragraphBorder.Source);
            bool afterStandaloneBreak=native&&p.SuppressBeforeAfterStandaloneBreak&&!RegionHasContent()&&pi>0&&
                !props.PageBreakBefore&&measured[pi-1] is {Paragraph:var previous,Lines:[{PageBreak:true}]}&&
                previous.Section==p.Section&&previous.Runs.Count(r=>r.Kind=="pageBreak")==1&&
                previous.Runs.All(r=>r.Kind is "pageBreak" or "paragraphMark");
            float beforeParagraph=afterStandaloneBreak?0:props.Before;
            float atRegionStart=beforeParagraph+reserve;
            float before=(afterStandaloneBreak?0:Math.Max(after,beforeParagraph))+reserve;
            if(!native&&props.KeepNext&&CanKeepWithNext(pi))
            {
                float chain=item.Height;int next=pi;
                while(measured[next].Paragraph.Properties.KeepNext&&CanKeepWithNext(next))
                {
                    Check();var following=measured[next+1];float followingHeight=following.Paragraph.TableId is {} id?TableStartHeight(measuredTables[id]):following.Height;
                    chain+=Math.Max(measured[next].Paragraph.Properties.After,following.Paragraph.TableId is null?following.Paragraph.Properties.Before:0)+followingHeight;next++;
                }
                if(chain>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_KEEP_CHAIN_TOO_LARGE");
                if(cursor+before+chain>geometry.Height-geometry.Bottom&&current.Count>0){Page();before=props.Before;}
            }
            if(!native&&props.KeepLines)
            {
                if(item.Height>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_KEEP_LINES_TOO_LARGE");
                if(cursor+before+item.Height>geometry.Height-geometry.Bottom&&current.Count>0){Page();before=props.Before;}
            }
            int line=0,segmentStart=0,segmentEnd=native?0:item.Lines.Count;float segmentHeight=0,followingMinimum=0;
            if(!native)cursor+=before;
            while(line<item.Lines.Count)
            {
                bool newSegment=line==segmentEnd;
                var square=line==0?p.SideExclusion:null;
                float top=cursor+(newSegment&&line==0?before:0);
                sideOnRegion.RemoveAll(active=>top>=active.Bottom);
                bool fullSheetBand=DrawsAcrossColumn(square);
                float bandShift=fullSheetBand?PageComposer.Points(square!.Height):0;
                if(square is not null)
                {
                    float clear=top;
                    foreach(var active in sideOnRegion)
                    {
                        Check();
                        if((!active.Shape.AllowOverlap||!square.AllowOverlap)&&
                            active.Shape.CollisionRight>square.CollisionLeft&&square.CollisionRight>active.Shape.CollisionLeft&&
                            active.Top+PageComposer.Points(active.Shape.Top)<top+PageComposer.Points(square.CollisionHeight))
                            clear=Math.Max(clear,active.Top+PageComposer.Points(active.Shape.CollisionHeight-square.Top));
                    }
                    cursor+=clear-top;
                    top=clear;
                    sideOnRegion.RemoveAll(active=>top>=active.Bottom);
                    if(top+PageComposer.Points(square.Height)>bandBottom-reservedNotes+.01f)
                    {
                        if(!RegionHasContent()&&notesOnPage.Count==0)
                            throw new LayoutFailure("PAGE_FLOW_SIDE_TOO_TALL",p.Source);
                        NextRegion();
                        if(newSegment)before=atRegionStart;
                        else cursor+=atRegionStart;
                        continue;
                    }
                }
                bool widthChanged=OtherWidth();
                if(widthChanged)
                {
                    var checkpoint=item.Lines[line].Native!;
                    var target=TargetRegion();
                    var variant=Recompose(target,checkpoint.Resume);
                    if(square is null&&sideOnRegion.Count>0&&variant[0].Native is {} narrowed&&
                        narrowed.Diagnostics.Any(d=>d.Code=="LINE_OVERFLOW")&&
                        !checkpoint.Diagnostics.Any(d=>d.Code=="LINE_OVERFLOW"))
                    {
                        cursor+=sideOnRegion.Min(active=>active.Bottom)-top;
                        continue;
                    }
                    var remaining=new List<MeasuredLine>(line+variant.Count);
                    for(int i=0;i<line;i++){Check();remaining.Add(item.Lines[i]);}
                    remaining.AddRange(variant);
                    item=item with{Lines=remaining};
                }
                if(native&&(newSegment||widthChanged))
                {
                    if(newSegment)segmentStart=line;
                    segmentEnd=line;segmentHeight=0;
                    while(segmentEnd<item.Lines.Count)
                    {
                        Check();var entry=item.Lines[segmentEnd++];segmentHeight+=entry.Height;
                        if(entry.PageBreak||entry.Native?.Cut==LineCutKind.Column)break;
                    }
                    float spacing=newSegment&&line==0?before:0;
                    followingMinimum=0;
                    if(keep?[pi] is {} continuation&&segmentEnd==item.Lines.Count&&!item.Lines[segmentEnd-1].PageBreak&&
                        item.Lines[segmentEnd-1].Native?.Cut!=LineCutKind.Column)
                    {
                        float chain=bandShift+segmentHeight+continuation.Following;
                        float atTop=segmentStart==0?atRegionStart:0;
                        float notes=NoteHeightIn(item,segmentStart,segmentEnd)+continuation.Notes;
                        if(pi>oversizedThrough)
                        {
                            if(notes>0&&atTop+chain+NoteReservation(notes,0)>RegionHeight()+.01f)
                                throw new LayoutFailure("PAGE_FOOTNOTE_KEEP_CHAIN_TOO_TALL",p.Source);
                            if(atTop+chain>RegionHeight()+.01f)
                            {
                                oversizedThrough=continuation.Through;
                                placementIssues.Add(new("PAGE_KEEP_NEXT_SPANS_PAGES",p.Source,
                                    "Chaîne plus haute qu’une page. Début en haut de page et coupures selon les contraintes des lignes.",false));
                                if(RegionHasContent())NextRegion();
                                spacing=atTop;
                            }
                            else if(cursor+spacing+chain+NoteReservation(notes,reservedNotes)>bandBottom+.01f)
                            {
                                do{NextRegion();spacing=atTop;}
                                while(notes>0&&notesOnPage.Count>0&&cursor+spacing+chain+NoteReservation(notes,reservedNotes)>bandBottom+.01f);
                            }
                        }
                        if(pi<=oversizedThrough)
                        {
                            followingMinimum=continuation.Minimum;
                            int tailCount=props.Widow?2:1;
                            int segmentCount=segmentEnd-segmentStart;
                            bool canSplitTail=!props.KeepLines&&segmentCount>=2*tailCount;
                            float tailHeight=canSplitTail?TailHeight(item,segmentEnd,tailCount):atTop+segmentHeight;
                            if(tailHeight+followingMinimum>RegionHeight()+.01f)
                                placementIssues.Add(new("PAGE_KEEP_NEXT_LINK_EXCEEDS_PAGE",p.Source,
                                    "La dernière portion admissible et le début du bloc suivant excèdent ensemble la hauteur de page.",false));
                        }
                    }
                    cursor+=spacing;
                    if(OtherWidth())continue;
                }
                if(native&&props.KeepLines&&line==segmentStart)
                {
                    float keptHeight=bandShift+segmentHeight+(segmentEnd==item.Lines.Count?paragraphBorder?.Height??0:0);
                    float notes=NoteHeightIn(item,segmentStart,segmentEnd);
                    if(keptHeight+NoteReservation(notes,0)>RegionHeight()+.01f)
                        throw new LayoutFailure(notes>0?"PAGE_FOOTNOTE_KEEP_LINES_TOO_TALL":"LAYOUT_KEEP_LINES_TOO_LARGE",p.Source);
                    if(cursor+keptHeight+NoteReservation(notes,reservedNotes)>bandBottom+.01f)
                    {
                        if(!RegionHasContent()&&notesOnPage.Count==0)throw new LayoutFailure("LAYOUT_KEEP_LINES_TOO_LARGE",p.Source);
                        do{NextRegion();cursor+=line==0?atRegionStart:0;}
                        while(notesOnPage.Count>0&&cursor+keptHeight+NoteReservation(notes,reservedNotes)>bandBottom+.01f);
                        if(OtherWidth())continue;
                    }
                }
                Check();int count=0;float height=0,trialReserve=reservedNotes;
                bool sideLine=square is not null||sideOnRegion.Count>0;
                if(sideLine&&(keep?[pi] is {} sideKeep&&!fullSheetBand&&!SideKeepFits(sideKeep)||
                    (props.Widow||props.KeepLines)&&cursor+bandShift+segmentHeight+reservedNotes>
                        bandBottom+.01f))
                    throw new LayoutFailure("PAGE_FLOW_SIDE_PARAGRAPH_CONSTRAINT_UNQUALIFIED",p.Source);
                bool noteDoesNotFit=false;
                float occupiedByShape=square is null?sideBottomOnPage:
                    Math.Max(sideBottomOnPage,cursor+PageComposer.Points(square.Height));
                while(line+count<segmentEnd&&(!sideLine||count==0))
                {
                    var candidate=item.Lines[line+count];
                    foreach(var note in NotesIn(candidate))
                    {
                        float separator=trialReserve==0?footnotes!.Separator.Height:0;
                        float end=Math.Max(Math.Max(occupiedByShape,borderBottomOnPage),cursor+bandShift+height+candidate.Height+
                            (line+count==item.Lines.Count-1?paragraphBorder?.Height??0:0));
                        var slice=FitNote(note.Block,bandBottom-end-trialReserve-separator,candidate.Height);
                        if(slice is null){noteDoesNotFit=true;break;}
                        trialReserve+=separator+slice.Value.Height;
                    }
                    if(noteDoesNotFit)break;
                    float bottomBorder=line+count==item.Lines.Count-1?paragraphBorder?.Height??0:0;
                    if(cursor+bandShift+height+candidate.Height+bottomBorder+trialReserve>bandBottom+.01f)break;
                    height+=candidate.Height;count++;
                    if(candidate.PageBreak||candidate.Native?.Cut==LineCutKind.Column)break;
                }
                if(count==0)
                {
                    if(!RegionHasContent()&&notesOnPage.Count==0)throw new LayoutFailure(
                        noteDoesNotFit?"PAGE_FOOTNOTE_TOO_TALL":"LAYOUT_LINE_TOO_TALL",p.Source);
                    NextRegion();if(native&&line==0)cursor+=atRegionStart;continue;
                }
                bool explicitBreak=item.Lines[line+count-1].PageBreak||item.Lines[line+count-1].Native?.Cut==LineCutKind.Column;
                if(props.Widow&&!sideLine&&!explicitBreak&&line+count<segmentEnd)
                {
                    if(!native&&line==0&&count==1&&current.Count>0){Page();continue;}
                    while(count>1&&LeavesWidow(line+count))count--;
                    if(native&&line==segmentStart&&count==1&&RegionHasContent())
                    {
                        NextRegion();if(line==0&&(reserve>0||Columns() is not null))cursor+=atRegionStart;
                        continue;
                    }
                    if(count==1&&segmentEnd-line>1&&!RegionHasContent())throw new InvalidDataException("LAYOUT_WIDOW_UNSATISFIABLE");
                }
                if(native&&followingMinimum>0&&line+count==segmentEnd&&
                    cursor+bandShift+height+followingMinimum+reservedNotes>bandBottom+.01f)
                {
                    int tail=props.Widow?2:1;
                    if(!props.KeepLines&&count-tail>=tail&&
                        TailHeight(item,segmentEnd,tail)+followingMinimum<=RegionHeight()+.01f)
                        count-=tail;
                    else if(RegionHasContent()&&height+followingMinimum+(line==0?atRegionStart:0)<=RegionHeight()+.01f)
                    {
                        NextRegion();if(line==0)cursor+=atRegionStart;continue;
                    }
                }
                for(int j=0;j<count;j++)
                {
                    var l=item.Lines[line];
                    if(l.Native is {} sourceLine)
                    {
                        if(sourceLine.Diagnostics.Any(d=>d.Code=="LINE_OVERFLOW"))
                            throw new LayoutFailure("LINE_OVERFLOW",sourceLine.ParagraphSource.Location);
                        var region=RegionOccurrence();
                        var (leftBound,rightBound)=InkBounds();
                        if(sideLine)
                        {
                            if(sourceLine.Region.Left>0)leftBound=RegionLeft()+PageComposer.Points(sourceLine.Region.Left);
                            long columnWidth=PageComposer.Emu(RegionRight()-RegionLeft());
                            if(sourceLine.Region.Left+sourceLine.Region.Width<columnWidth-PageComposer.Emu(.01f))
                                rightBound=RegionLeft()+PageComposer.Points(sourceLine.Region.Left+sourceLine.Region.Width);
                        }
                        float anchorTop=cursor;
                        cursor+=bandShift;
                        var placed=PositionNative(l,p,RegionLeft(),cursor,leftBound,0,rightBound,geometry.Height) with{FlowRegion=region};
                        if(square is not null)
                        {
                            long exclusionLeft=Math.Max(0,square.Left);
                            long exclusionRight=Math.Min(PageComposer.Emu(RegionRight()-RegionLeft()),square.Right);
                            placed=placed with{SideExclusion=new(PageComposer.Emu(RegionLeft())+exclusionLeft,
                                PageComposer.Emu(anchorTop)+square.Top,exclusionRight-exclusionLeft,square.Height-square.Top),
                                FloatingAnchorTop=fullSheetBand?anchorTop:null};
                        }
                        if(paragraphBorder is not null&&line==item.Lines.Count-1)
                        {
                            float borderLeft=RegionLeft()+props.Left,borderRight=RegionRight()-props.Right;
                            float borderTop=cursor+l.Height+paragraphBorder.Space;
                            if(borderLeft<0||borderRight>geometry.Width||borderRight<=borderLeft||
                                sideOnRegion.Any(active=>active.Bottom>borderTop&&active.Top<borderTop+paragraphBorder.Width))
                                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",paragraphBorder.Source);
                            placed=placed with{ParagraphBorder=new(paragraphBorder,borderLeft,borderRight,borderTop)};
                            borderBottomOnPage=Math.Max(borderBottomOnPage,borderTop+paragraphBorder.Width);
                        }
                        if(props.LeftBorder is not null||props.RightBorder is not null)
                        {
                            if(sideLine||Columns() is not null||sourceLine.Properties.RightToLeft)
                                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",p.Source);
                            if(!textBoxBorders&&pi+1<measured.Count&&CanJoinNative(p,measured[pi+1].Paragraph)&&
                                (SameSide(props.LeftBorder,measured[pi+1].Paragraph.Properties.LeftBorder)||
                                 SameSide(props.RightBorder,measured[pi+1].Paragraph.Properties.RightBorder)))
                                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_GROUP_UNQUALIFIED",p.Source);
                            float extendingTop=props.ContinuesSideBorders&&line==0&&current.LastOrDefault() is {} previousLine?
                                Math.Max(0,cursor-previousLine.NativeTop-previousLine.Height-
                                    (previousLine.ParagraphBorder?.Border.Height??0)):0;
                            if(props.LeftBorder is {} leftStroke)
                            {
                                float x=RegionLeft()+Math.Min(props.Left,props.Left+props.FirstLine)-leftStroke.Space-leftStroke.Width/2;
                                if(x-leftStroke.Width/2<0)throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",leftStroke.Source);
                                placed=placed with{LeftBorder=new(leftStroke,x,line==item.Lines.Count-1?paragraphBorder?.Height??0:0,extendingTop)};
                            }
                            if(props.RightBorder is {} right)
                            {
                                float x=RegionRight()-props.Right+right.Space+right.Width/2;
                                if(x+right.Width/2>geometry.Width)throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",right.Source);
                                placed=placed with{RightBorder=new(right,x,line==item.Lines.Count-1?paragraphBorder?.Height??0:0,extendingTop)};
                            }
                        }
                        if(props.TopBorder is not null&&(sideLine||Columns() is not null||sourceLine.Properties.RightToLeft))
                            throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",props.TopBorder.Source);
                        if(line==0&&props.TopBorder is {} topStroke)
                        {
                            float start=placed.LeftBorder?.X??RegionLeft()+props.Left;
                            float end=placed.RightBorder?.X??RegionRight()-props.Right;
                            if(start<0||end>geometry.Width||end<=start)
                                throw new LayoutFailure("PAGE_PARAGRAPH_BORDER_REGION_UNQUALIFIED",topStroke.Source);
                            placed=placed with{ParagraphTopBorder=new(topStroke,start,end,cursor)};
                        }
                        if(placed.ParagraphBorder is {} bottomStroke)
                            placed=placed with{ParagraphBorder=bottomStroke with
                                {Left=placed.LeftBorder?.X??bottomStroke.Left,Right=placed.RightBorder?.X??bottomStroke.Right}};
                        current.Add(placed);
                        if(square is not null)
                        {
                            float bottom=anchorTop+PageComposer.Points(square.Height);
                            if(!fullSheetBand)sideOnRegion.Add((square,anchorTop,bottom));
                            sideBottomOnPage=Math.Max(sideBottomOnPage,bottom);
                        }
                        foreach(var note in NotesIn(l))
                        {
                            if(notesOnPage.Count==0)reservedNotes=footnotes!.Separator.Height;
                            var slice=FitNote(note.Block,bandBottom-Math.Max(Math.Max(sideBottomOnPage,borderBottomOnPage),cursor+l.Height)-reservedNotes,l.Height)??
                                throw new LayoutFailure("PAGE_FOOTNOTE_RESERVATION_INVALID",note.Call.Location);
                            CommitNote(note.Call,slice);
                        }
                        cursor+=l.Height;line++;
                        if(l.PageBreak)
                        {
                            Page(true);
                            if(line<item.Lines.Count&&pi==oversizedThrough)oversizedThrough=-1;
                        }
                        else if(l.Native?.Cut==LineCutKind.Column)
                        {
                            bool advancedPage=NextRegion(force:true);
                            finalColumnBreak=advancedPage&&line==item.Lines.Count;
                        }
                        continue;
                    }
                    float left=geometry.Left+props.Left+(line==0&&p.List is null?props.FirstLine:0);float width=geometry.Width-geometry.Right-props.Right-left;
                    float shift=props.Alignment switch{"center"=>(width-l.Width)/2,"right"=>width-l.Width,_=>0};
                    var fragments=Justify(l,width,props.Alignment=="both"&&line<item.Lines.Count-1,fonts).Select(f=>f with{X=f.X+left+shift}).ToArray();
                    float baseline=cursor+l.Ascent;
                    foreach(var fragment in fragments)
                    {
                        fonts.Get(fragment.Style).MeasureText(fragment.Text,out var ink);
                        if(fragment.X+ink.Left<-.01f||fragment.X+ink.Right>geometry.Width+.01f||baseline+ink.Top<-.01f||baseline+ink.Bottom>geometry.Height+.01f)throw new InvalidDataException("LAYOUT_GLYPH_OUTSIDE_PAGE");
                    }
                    current.Add(new(p.Id,p.Source,l.Text,left+shift,baseline){Fragments=fragments,Height=l.Height,SourceBreak=l.SourceBreak,HasExplicitPageBreak=l.PageBreak,Section=p.Section,Images=l.Images.Select(i=>i with{X=i.X+left+shift,Y=baseline-i.Image.Height}).ToArray(),Tabs=l.Tabs.Select(t=>t with{From=t.From+left+shift,To=t.To+left+shift}).ToArray()});
                    if(current[^1].Images.Any(i=>i.X<-.01f||i.X+i.Image.Width>geometry.Width+.01f||i.Y<-.01f||i.Y+i.Image.Height>geometry.Height+.01f))throw new InvalidDataException("LAYOUT_IMAGE_OUTSIDE_PAGE");
                    cursor+=l.Height;line++;
                    if(l.PageBreak){Page(true);}
                }
                if(!sideLine&&line<item.Lines.Count&&RegionHasContent())NextRegion();
            }
            after=SpacingAfter(props);

            bool OtherWidth()=>item.Lines[line].Native is {} nextLine&&TargetRegion()!=nextLine.Region;
            bool DrawsAcrossColumn(SideExclusion? shape)=>
                shape is {ClipToSheet:true,WrapText:"bothSides",Top:0,Left:<=0}&&
                shape.Right>=PageComposer.Emu(RegionRight()-RegionLeft())&&Columns() is null&&
                sideOnRegion.Count==0&&item.Lines[0].Native is {} anchor&&!anchor.Parts.IsEmpty&&
                anchor.Parts[0].Control?.Kind=="Drawing"&&
                props.TopBorder is null&&props.LeftBorder is null&&props.RightBorder is null&&paragraphBorder is null;
            bool SideKeepFits(KeepContinuation continuation)
            {
                if(p.SideExclusion is not null||sideOnRegion.Count!=1||continuation.Notes>0||
                    !sideOnRegion[0].Shape.ThroughPolygon.IsEmpty||item.Lines[line].Native is not {} start)
                    return false;
                var target=start.Region;
                var shape=sideOnRegion[0].Shape;
                // Once a single rectangle narrows this line, later lines can
                // regain width. Measure each successor in that narrower band.
                if(target.Left==0&&target.Width==measured[pi].Lines[0].Native!.Region.Width&&
                    shape.Top>PageComposer.Emu(cursor-sideOnRegion[0].Top))return false;
                float bottom=cursor+segmentHeight+reservedNotes+(props.BottomBorder?.Height??0);
                float trailing=SpacingAfter(props);
                for(int i=pi+1;i<=continuation.Through;i++)
                {
                    Check();
                    var successor=measured[i];
                    var paragraph=successor.Paragraph;
                    if(paragraph.TableId is not null||paragraph.SideExclusion is not null||paragraph.FlowExclusion is not null||
                        successor.Lines[0].Native is not {} first)return false;
                    try
                    {
                        var lines=RecomposeParagraph(paragraph,target with{Id=first.Region.Id},first.Resume);
                        if(lines.Any(l=>l.PageBreak||l.Native?.Cut==LineCutKind.Column))return false;
                        bottom+=Math.Max(trailing,paragraph.Properties.Before)+lines.Sum(l=>l.Height)+
                            (paragraph.Properties.BottomBorder?.Height??0);
                    }
                    catch(LayoutFailure){return false;}
                    trailing=SpacingAfter(paragraph.Properties);
                }
                return bottom<=bandBottom+.01f;
            }
            bool LeavesWidow(int cut)
            {
                if(native&&Columns() is {} columns&&item.Lines[cut].Native is {} tail)
                {
                    long width=columns.Columns[(column+1)%columns.Columns.Length].Width;
                    if(tail.Region.Width!=width)
                    {
                        var next=Recompose(tail.Region with{Width=width},tail.Resume);
                        return next.Count==1||next[0].PageBreak||next[0].Native?.Cut==LineCutKind.Column;
                    }
                }
                return segmentEnd-cut==1;
            }
            IReadOnlyList<MeasuredLine> Recompose(LineRegion region,LineResume checkpoint)=>RecomposeParagraph(p,region,checkpoint);
            IReadOnlyList<MeasuredLine> RecomposeParagraph(LayoutParagraph paragraph,LineRegion region,LineResume checkpoint)
            {
                var key=(paragraph.Id,region,checkpoint);
                if(alternateLines.TryGetValue(key,out var lines))return lines;
                try{alternateLines.Add(key,lines=reflow!(paragraph,region,checkpoint));}
                catch(InvalidDataException error){throw new LayoutFailure(error.Message,paragraph.Source);}
                return lines;
            }
            LineRegion TargetRegion()
            {
                var initial=measured[pi].Lines[0].Native!.Region;
                long width=Columns() is {} columns?columns.Columns[column].Width:initial.Width;
                float top=cursor+(line==0&&segmentEnd==0?before:0);
                long availableLeft=0,availableRight=width;
                foreach(var active in sideOnRegion)
                    if(top<active.Bottom)Exclude(active.Shape,active.Top);
                if(line==0&&p.SideExclusion is {} pending&&!DrawsAcrossColumn(pending))Exclude(pending,top);
                var region=initial with{Left=availableLeft,Width=availableRight-availableLeft,
                    RightMargin=availableLeft==0&&availableRight==width?initial.RightMargin:0,
                    CenteredImageOverhang=availableLeft==0&&availableRight==width?initial.CenteredImageOverhang:0,
                    TabOrigin=availableLeft!=initial.Left?initial.TabOrigin??initial.Left:initial.TabOrigin};
                if(region.Width<12*TypographySession.EmuPerPoint)
                    throw new LayoutFailure("PAGE_FLOW_SIDE_TOO_NARROW",p.Source);
                return region;

                void Exclude(SideExclusion shape,float anchorTop)
                {
                    Check();
                    if(shape.Left>=width||shape.Right<=0||shape.Left>=shape.Right||
                        shape.CollisionLeft < -PageComposer.Emu(RegionLeft())||
                        shape.CollisionRight>PageComposer.Emu(geometry.Width-RegionLeft())||
                        Columns() is not null&&(shape.Left<0||shape.Right>width))
                        throw new LayoutFailure("PAGE_FLOW_SIDE_OUTSIDE_REGION",p.Source);
                    long relativeTop=PageComposer.Emu(top-anchorTop);
                    long relativeBottom=relativeTop+PageComposer.Emu(item.Lines[line].Height);
                    if(relativeBottom<=shape.Top)return;
                    var (left,right)=shape.Project(relativeTop,relativeBottom);
                    if(shape.WrapText=="bothSides"&&left>0&&right<width)
                        throw new LayoutFailure("PAGE_FLOW_SIDE_TWO_CORRIDORS_UNQUALIFIED",p.Source);
                    bool useLeft=shape.WrapText=="left"||shape.WrapText=="bothSides"&&left>0||
                        shape.WrapText=="largest"&&left>=width-right;
                    if(useLeft)availableRight=Math.Min(availableRight,left);
                    else availableLeft=Math.Max(availableLeft,right);
                }
            }
        }
        // A final manual break creates a deliberate, source-backed blank page.
        bool finalBreak=measured[^1].Lines.LastOrDefault()?.PageBreak==true;
        if(completeBody)FlushEndnotes();
        Page(finalBreak||finalColumnBreak||pages.Count==0);
        while(notesOnPage.Count>0)Page();
        return new(pages,placementIssues.Count==0?document.Issues:[..document.Issues,..placementIssues]);

        static bool SameSide(ParagraphBorderStroke? first,ParagraphBorderStroke? second)=>
            first is not null&&second is not null&&first==(second with{Source=first.Source});

        bool CanKeepWithNext(int index)
        {
            if(index+1>=measured.Count)return false;
            var first=measured[index].Paragraph;
            var next=measured[index+1].Paragraph;
            return !next.Properties.PageBreakBefore&&first.Section==next.Section;
        }
    }

    internal static bool CanJoinNative(LayoutParagraph first,LayoutParagraph next) =>
        !next.Properties.PageBreakBefore&&(first.Section==next.Section||
            next.SectionStart=="continuous"&&next.Geometry==first.Geometry&&
            first.FlowColumns is not {Balance:true,Columns.Length:>1}&&
            PageComposer.SameColumns(first.FlowColumns,next.FlowColumns));

    static float TailHeight(MeasuredParagraph paragraph,int end,int length)
    {
        float result=0;
        for(int i=end-length;i<end;i++)result+=paragraph.Lines[i].Height;
        return result;
    }

    static float SpacingAfter(ParagraphLayout properties)=>Math.Max(properties.After,properties.BottomBorder?.Height??0);

    readonly record struct KeepContinuation(float Following,float Minimum,int Through,float Notes);
    readonly record struct BandTrial(float FirstHeight,int FinalSheet,float FinalHeight,int SheetLimit);

    static KeepContinuation?[] PlanNativeKeeps(IReadOnlyList<MeasuredParagraph> paragraphs,
        IReadOnlyDictionary<string,MeasuredTable> tables,Action check,Func<MeasuredLine,float>? noteHeight=null)
    {
        var plan=new KeepContinuation?[paragraphs.Count];
        float firstHeight=0,firstMinimum=0,firstNotes=0;
        bool firstHasBreak=false;
        for(int i=paragraphs.Count-1;i>=0;i--)
        {
            check();
            var current=paragraphs[i];var p=current.Paragraph;
            if(i+1<paragraphs.Count&&p.Properties.KeepNext&&CanJoinNative(p,paragraphs[i+1].Paragraph)&&
                current.Lines.LastOrDefault() is not {PageBreak:true} and not {Native.Cut:LineCutKind.Column})
            {
                var following=paragraphs[i+1].Paragraph;
                float spacing=Math.Max(SpacingAfter(p.Properties),following.Properties.Before)+
                    (following.FlowExclusion is {} exclusion?PageComposer.Points(exclusion.Height):0);
                var continuation=firstHasBreak?null:plan[i+1];
                plan[i]=new(spacing+firstHeight+(continuation?.Following??0),spacing+firstMinimum,continuation?.Through??i+1,
                    firstNotes+(continuation?.Notes??0));
            }
            if(p.TableId is {} table)
            {
                var measuredTable=tables[table];
                firstHeight=firstMinimum=TableStartHeight(measuredTable);firstHasBreak=true;
                int headers=measuredTable.Table.Rows.TakeWhile(r=>r.Header).Count();
                int last=headers==measuredTable.Table.Rows.Count?headers-1:measuredTable.Groups.First(g=>g.First>=headers).Last;
                firstNotes=noteHeight is null?0:TableLines(measuredTable,0,last).Sum(noteHeight);
                continue;
            }
            firstHeight=firstMinimum=firstNotes=0;firstHasBreak=false;
            int count=0;
            foreach(var line in current.Lines)
            {
                check();
                firstHeight+=line.Height;
                firstNotes+=noteHeight?.Invoke(line)??0;
                if(p.Properties.KeepLines||count<(p.Properties.Widow?2:1))firstMinimum+=line.Height;
                count++;
                if(line.PageBreak||line.Native?.Cut==LineCutKind.Column){firstHasBreak=true;break;}
            }
            if(p.Properties.Widow&&count==3)firstMinimum=firstHeight;
            if(!firstHasBreak&&plan[i] is null&&p.Properties.BottomBorder is {} border)
            {
                firstHeight+=border.Height;
                if(p.Properties.KeepLines||count<=(p.Properties.Widow?3:1))firstMinimum+=border.Height;
            }
        }
        return plan;
    }
    static IReadOnlyList<MeasuredLine> Measure(LayoutParagraph p,LayoutFonts fonts,Action check)
    {
        if(p.TableId is not null)return [];
        if(p.Properties.Alignment=="both"&&p.Runs.Any(r=>r.Kind=="lineBreak"))throw new InvalidDataException("LAYOUT_JUSTIFY_MANUAL_BREAK_UNQUALIFIED");
        var result=new List<MeasuredLine>();var glyphs=new List<Glyph>();
        float First()=>result.Count==0&&p.List is null?p.Properties.FirstLine:0;
        float Available()=>p.Geometry.Width-p.Geometry.Right-p.Properties.Right-(p.Geometry.Left+p.Properties.Left+First());
        float Width(IEnumerable<Glyph> data)
        {
            float x=0;foreach(var group in data.ToArray().GroupAdjacent())
                x+=group[0].Run.Kind=="image"?group[0].Run.Image!.Width:fonts.Get(group[0].Run.Style).MeasureText(string.Concat(group.Select(g=>g.Text)));
            return x;
        }
        MeasuredLine Build(IReadOnlyList<Glyph> data,string sourceBreak="",bool pageBreak=false)
        {
            var fragments=new List<PageFragment>();var images=new List<PageImage>();var tabs=new List<PageTab>();float x=0,ascent=0,descent=0;
            for(int i=0;i<data.Count;)
            {
                var first=data[i];var run=first.Run;
                if(run.Kind=="image")
                {images.Add(new(run.Id,run.Image!,x,0));ascent=Math.Max(ascent,run.Image!.Height);x+=run.Image.Width;i++;continue;}
                if(run.Kind=="tab")
                {
                    int end=i+1;while(end<data.Count&&data[end].Run.Kind!="tab")end++;
                    float origin=p.Properties.Left+First(),absolute=origin+x;
                    var stop=p.Properties.Tabs.FirstOrDefault(t=>t.Alignment!="numbering"&&t.Position>absolute+.01f)??new TabStop((MathF.Floor(absolute/p.Properties.DefaultTab)+1)*p.Properties.DefaultTab,"left");
                    float following=Width(data.Skip(i+1).Take(end-i-1));
                    float anchor=stop.Position-origin;
                    float position=stop.Alignment switch{"right"=>anchor-following,"center"=>anchor-following/2,"decimal"=>anchor,_=>anchor};
                    if(stop.Alignment=="decimal")
                    {
                        var followingData=data.Skip(i+1).Take(end-i-1).ToArray();var separators=followingData.Where(g=>g.Text is "." or ",").Select(g=>g.Text).Distinct().ToArray();
                        if(separators.Length>1)throw new InvalidDataException("LAYOUT_DECIMAL_TAB_AMBIGUOUS");
                        int separator=Array.FindIndex(followingData,g=>g.Text is "." or ",");position=anchor-(separator<0?following:Width(followingData.Take(separator)));
                    }
                    if(position<x-.01f)throw new InvalidDataException("LAYOUT_TAB_OVERLAP_UNQUALIFIED");
                    tabs.Add(new(run.Id,run.Source,x,position,stop.Alignment));x=position;i++;continue;
                }
                int last=i+1;while(last<data.Count&&data[last].Run.Kind=="text"&&data[last].Run.Id==run.Id&&data[last].Run.Style==run.Style)last++;
                string text=string.Concat(data.Skip(i).Take(last-i).Select(g=>g.Text));var font=fonts.Get(run.Style);var metric=font.Metrics;
                ascent=Math.Max(ascent,-metric.Ascent);descent=Math.Max(descent,metric.Descent+Math.Max(0,metric.Leading));
                fragments.Add(new(run.Id,run.Source,first.Offset,text,x,run.Style));x+=font.MeasureText(text);i=last;
            }
            if(p.Runs.FirstOrDefault(r=>r.Kind=="paragraphMark") is {} mark){var metrics=fonts.Get(mark.Style).Metrics;ascent=Math.Max(ascent,-metrics.Ascent);descent=Math.Max(descent,metrics.Descent+Math.Max(0,metrics.Leading));}
            if(data.Count==0||ascent==0&&descent==0||data.All(g=>g.Run.Kind is "tab" or "image")){var metrics=fonts.Get(p.Runs.First(r=>r.Style.Font is not null).Style).Metrics;ascent=Math.Max(ascent,-metrics.Ascent);descent=Math.Max(descent,metrics.Descent+Math.Max(0,metrics.Leading));}
            if(result.Count==0&&p.List is {} label)
            {
                var marker=p.Runs.First(r=>r.Kind=="listMarker");var font=fonts.Get(marker.Style);float width=font.MeasureText(marker.Text);
                float markerX=p.Properties.FirstLine-(label.Alignment=="right"?width:label.Alignment=="center"?width/2:0);
                float gap=label.Suffix=="space"?font.MeasureText(" "):label.Suffix=="tab"?.5f:0;
                if(marker.Text.Length>0&&markerX+width+gap>0.01f)throw new InvalidDataException("LAYOUT_LIST_MARKER_OVERLAP");
                fragments.Insert(0,new(marker.Id,marker.Source,0,marker.Text,markerX,marker.Style){Generated=true});
                ascent=Math.Max(ascent,-font.Metrics.Ascent);descent=Math.Max(descent,font.Metrics.Descent+Math.Max(0,font.Metrics.Leading));
            }
            float natural=ascent+descent;float height=p.Properties.LineRule switch{"exact"=>p.Properties.Line/20,"atLeast"=>Math.Max(natural,p.Properties.Line/20),_=>natural*p.Properties.Line/240};
            if(height<natural-.01f)throw new InvalidDataException("LAYOUT_LINE_CLIP_UNQUALIFIED");
            return new(fragments,string.Concat(data.Select(g=>g.Text)),x,height,ascent+(height-natural)/2,sourceBreak,pageBreak){Images=images,Tabs=tabs};
        }
        foreach(var run in p.Runs.Where(r=>r.Kind!="listMarker"))
        {
            check();if(run.Kind is "lineBreak" or "pageBreak"){result.Add(Build(glyphs,run.Text,run.Kind=="pageBreak"));glyphs.Clear();continue;}
            if(run.Kind is "image" or "tab")
            {
                var glyph=new Glyph(run,run.Offset,run.Text);glyphs.Add(glyph);
                if(Build(glyphs).Width>Available()+.01f)
                {
                    glyphs.RemoveAt(glyphs.Count-1);if(glyphs.Count>0){if(Build(glyphs).Tabs.Any(t=>t.Alignment!="left"))throw new InvalidDataException("LAYOUT_TAB_WRAP_UNQUALIFIED");result.Add(Build(glyphs));glyphs=[];}
                    glyphs.Add(glyph);if(Build(glyphs).Width>Available()+.01f)throw new InvalidDataException("LAYOUT_INLINE_TOO_WIDE");
                }
                continue;
            }
            if(!SupportedText(run.Text)||run.Text.Contains('\t')||run.Text.Contains('\r')||run.Text.Contains('\n'))throw new InvalidDataException("LAYOUT_SCRIPT_OR_CONTROL_UNQUALIFIED");
            if(!fonts.Get(run.Style).ContainsGlyphs(run.Text))throw new InvalidDataException("LAYOUT_GLYPH_MISSING");
            var enumerator=StringInfo.GetTextElementEnumerator(run.Text);
            while(enumerator.MoveNext())
            {
                check();var glyph=new Glyph(run,run.Offset+enumerator.ElementIndex,enumerator.GetTextElement());
                if(Available()<12)throw new InvalidDataException("LAYOUT_TEXT_WIDTH_INVALID");
                glyphs.Add(glyph);
                if(Build(glyphs).Width>Available()+.01f)
                {
                    if(Build(glyphs).Tabs.Any(t=>t.Alignment!="left"))throw new InvalidDataException("LAYOUT_TAB_WRAP_UNQUALIFIED");
                    glyphs.RemoveAt(glyphs.Count-1);if(glyphs.Count==0)throw new InvalidDataException("LAYOUT_GLYPH_TOO_WIDE");
                    int split=glyphs.FindLastIndex(g=>g.Text==" ");
                    if(split>=0){result.Add(Build(glyphs.Take(split+1).ToArray()));glyphs=glyphs.Skip(split+1).ToList();}
                    else{result.Add(Build(glyphs));glyphs=[];}
                    glyphs.Add(glyph);if(Build(glyphs).Width>Available()+.01f)throw new InvalidDataException("LAYOUT_WRAP_UNQUALIFIED");
                }
            }
        }
        if(glyphs.Count>0||result.Count==0||p.Runs.LastOrDefault(r=>r.Kind!="paragraphMark")?.Kind=="lineBreak")result.Add(Build(glyphs));
        return result;
    }
    static PaginatedDocument ApplyContextualSpacing(PaginatedDocument document)
    {
        var updates=new Dictionary<string,LayoutParagraph>();
        foreach(var group in document.Paragraphs.GroupBy(p=>p.CellOwner))
        {
            var list=group.ToArray();
            for(int i=0;i<list.Length;i++)
            {
                var p=list[i];if(!p.Properties.ContextualSpacing||p.TableId is not null)continue;
                bool Same(int index)=>index>=0&&index<list.Length&&list[index].TableId is null&&list[index].Section==p.Section&&list[index].StyleId==p.StyleId;
                updates[p.Id]=p with{Properties=p.Properties with{Before=Same(i-1)?0:p.Properties.Before,After=Same(i+1)?0:p.Properties.After}};
            }
        }
        return document with{Paragraphs=document.Paragraphs.Select(p=>updates.GetValueOrDefault(p.Id)??p).ToArray()};
    }
    static IReadOnlyList<PageFragment> Justify(MeasuredLine line,float available,bool eligible,LayoutFonts fonts)
    {
        if(!eligible||line.SourceBreak.Length>0||line.PageBreak)return line.Fragments;
        if(line.Tabs.Count>0||line.Images.Count>0)throw new InvalidDataException("LAYOUT_JUSTIFY_INLINE_UNQUALIFIED");
        var pieces=new List<PageFragment>();
        foreach(var f in line.Fragments)
        {
            if(f.Generated){pieces.Add(f);continue;}
            int start=0;
            for(int i=0;i<f.Text.Length;i++)if(f.Text[i]==' ')
            {
                if(i>start)pieces.Add(f with{Offset=f.Offset+start,Text=f.Text[start..i]});
                pieces.Add(f with{Offset=f.Offset+i,Text=" "});start=i+1;
            }
            if(start<f.Text.Length)pieces.Add(f with{Offset=f.Offset+start,Text=f.Text[start..]});
        }
        int first=pieces.FindIndex(f=>!f.Generated&&f.Text.Trim().Length>0),last=pieces.FindLastIndex(f=>!f.Generated&&f.Text.Trim().Length>0);
        int gaps=pieces.Where((f,i)=>i>first&&i<last&&!f.Generated&&f.Text==" ").Count();
        if(gaps==0)return line.Fragments;
        float natural=pieces.Take(last+1).Where(f=>!f.Generated).Sum(f=>fonts.Get(f.Style).MeasureText(f.Text));
        float extra=(available-natural)/gaps;if(extra<-.01f)throw new InvalidDataException("LAYOUT_JUSTIFY_WIDTH_INVALID");
        float x=0;var result=new List<PageFragment>();
        for(int i=0;i<pieces.Count;i++)
        {
            var f=pieces[i];if(f.Generated){result.Add(f);continue;}
            result.Add(f with{X=x});x+=fonts.Get(f.Style).MeasureText(f.Text);
            if(i>first&&i<last&&f.Text==" ")x+=Math.Max(0,extra);
        }
        return result;
    }
    static IEnumerable<List<Glyph>> GroupAdjacent(this IReadOnlyList<Glyph> source)
    {
        var group=new List<Glyph>();
        foreach(var glyph in source)
        {
            if(group.Count>0&&(group[^1].Run.Id!=glyph.Run.Id||group[^1].Run.Style!=glyph.Run.Style||group[^1].Run.Kind is "image" or "tab")){yield return group;group=[];}
            group.Add(glyph);
        }
        if(group.Count>0)yield return group;
    }
    static void DrawStyledPage(SKCanvas canvas,ComposedPage page,SKColor? paper=null,Func<SKColor,SKColor,SKColor>? ink=null)
    {
        DrawFloatingDrawings(canvas,page,behind:true,paper:paper,ink:ink);
        DrawCells(canvas,page);
        foreach(var line in page.MainDisplayLines)DrawParagraphShading(canvas,line);
        var bitmaps=new Dictionary<string,SKBitmap>();
        try
        {
            foreach(var line in page.MainDisplayLines)
            {
                if(line.Images.Count==0)continue;
                int save=BeginLineClip(line);
                try
                {
                    foreach(var image in line.Images)
                        DrawImage(canvas,bitmaps,image.Image,image.X,image.Y,page.NativePainter);
                }
                finally{if(save>=0)canvas.RestoreToCount(save);}
            }
        }
        finally{foreach(var bitmap in bitmaps.Values)bitmap.Dispose();}
        using var fonts=new LayoutFonts();using var paint=new SKPaint{IsAntialias=true};
        foreach(var line in page.MainDisplayLines)
        {
            SKColor background=paper??SKColors.White;
            if(ink is not null)
            {
                var cell=page.Cells.LastOrDefault(c=>c.Fill is not null && line.X>=c.X && line.X<c.X+c.Width &&
                    line.Baseline>=c.Y && line.Baseline<c.Y+c.Height);
                if(cell?.Fill is {} fill)background=SKColor.Parse("#"+fill);
                if(line.ParagraphShading is {} shade)background=SKColor.Parse("#"+shade.Shading.Color);
            }
            if(line.Native is {} native)
            {
                int save=BeginLineClip(line);
                try
                {
                    (page.NativePainter??throw new InvalidDataException("PAGE_DRAW_LEASE")).Paint(canvas,native,line.NativeOffsetX,line.Baseline,SKColors.Black,background,ink);
                }
                finally{if(save>=0)canvas.RestoreToCount(save);}
            }
            if(line.NoteRule is {} rule)
            {
                paint.Color=SKColors.Black;
                paint.StrokeWidth=.5f;
                canvas.DrawLine(line.X,line.Baseline,line.X+rule.Width,line.Baseline,paint);
            }
            if(line.ParagraphTopBorder is {} top)DrawHorizontal(top);
            if(line.ParagraphBorder is {} placed)DrawHorizontal(placed);
            void DrawHorizontal(PlacedParagraphBorder stroke)
            {
                paint.Color=SKColor.Parse("#"+stroke.Border.Color);
                paint.StrokeWidth=stroke.Border.Width;
                float y=stroke.Top+stroke.Border.Width/2;
                canvas.DrawLine(stroke.Left,y,stroke.Right,y,paint);
            }
            if(line.LeftBorder is {} left)DrawSide(left);
            if(line.RightBorder is {} right)DrawSide(right);
            void DrawSide(PlacedParagraphSideBorder side)
            {
                paint.Color=SKColor.Parse("#"+side.Border.Color);
                paint.StrokeWidth=side.Border.Width;
                canvas.DrawLine(side.X,line.NativeTop-side.ExtendedTop,side.X,line.NativeTop+line.Height+side.ExtendedBottom,paint);
            }
            foreach(var f in line.Fragments)
            {
                var font=fonts.Get(f.Style);paint.Color=SKColor.Parse("#"+f.Style.Color);
                if(ink is not null)paint.Color=ink(paint.Color,background);
                canvas.DrawText(f.Text,f.X,line.Baseline,font,paint);
                if(f.Style.Underline)
                {
                    if(f.Style.UnderlineColor is {} color)paint.Color=SKColor.Parse("#"+color);
                    paint.StrokeWidth=Math.Max(.5f,font.Metrics.UnderlineThickness??.5f);
                    float y=line.Baseline+(font.Metrics.UnderlinePosition??1);
                    canvas.DrawLine(f.X,y,f.X+font.MeasureText(f.Text),y,paint);
                }
            }
        }
        DrawCellBorders(canvas,page);
        DrawFloatingDrawings(canvas,page,behind:false,paper:paper,ink:ink);

        int BeginLineClip(PageLine line)
        {
            if(!line.ClipToLineBox)return -1;
            int save=canvas.Save();
            canvas.ClipRect(new(0,line.NativeTop,page.Geometry.Width,line.NativeTop+line.Height));
            return save;
        }
    }

    static void DrawParagraphShading(SKCanvas canvas,PageLine line)
    {
        if(line.ParagraphShading is not {} shading)return;
        using var paint=new SKPaint{Color=SKColor.Parse("#"+shading.Shading.Color)};
        canvas.DrawRect(new(line.NativeOffsetX+shading.Left,line.NativeTop,line.NativeOffsetX+shading.Right,
            line.NativeTop+line.Height),paint);
    }

    static void DrawFloatingDrawings(SKCanvas canvas,ComposedPage page,bool behind,bool clipToSheet=true,SKColor? paper=null,Func<SKColor,SKColor,SKColor>? ink=null)
    {
        if(page.FloatingPictures.Count==0&&page.FloatingTextFrames.Count==0)return;
        int save=canvas.Save();
        var images=new Dictionary<string,SKBitmap>(StringComparer.Ordinal);
        try
        {
            if(clipToSheet)canvas.ClipRect(new(0,0,page.Geometry.Width,page.Geometry.Height));
            var drawing=page.FloatingPictures.Where(p=>p.Picture.BehindText==behind)
                .Select(p=>(Order:p.Picture.Order,ChildOrder:0,Picture:(PlacedFloatingPicture?)p,Frame:(PlacedFloatingTextFrame?)null));
            drawing=drawing.Concat(page.FloatingTextFrames.Where(f=>f.Frame.BehindText==behind)
                .Select(f=>(f.Frame.Order,f.Frame.ChildOrder,(PlacedFloatingPicture?)null,(PlacedFloatingTextFrame?)f)));
            foreach(var (_,_,picture,frame) in drawing.OrderBy(p=>p.Order)
                .ThenBy(p=>p.Picture?.Picture.Id??p.Frame?.Frame.Id,StringComparer.Ordinal).ThenBy(p=>p.ChildOrder))
            {
                if(picture is {} placement)
                {
                    DrawImage(canvas,images,placement.Picture.Image,placement.X,placement.Y,page.NativePainter);
                }
                else if(frame is not null)
                {
                    var bounds=frame.Frame;
                    if(bounds.Vector is {} vector)
                    {
                        DrawImage(canvas,images,vector,bounds.X,bounds.Y,page.NativePainter);
                        continue;
                    }
                    if(bounds.Fill is {} rgb)
                    {
                        using var fill=new SKPaint{Color=new((byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb)};
                        canvas.DrawRect(bounds.X,bounds.Y,bounds.Width,bounds.Height,fill);
                    }
                    if(bounds.Stroke is {} border)
                    {
                        uint color=border.Color;
                        using var pen=new SKPaint{Color=new((byte)(color>>16),(byte)(color>>8),(byte)color),
                            IsStroke=true,IsAntialias=true,StrokeWidth=border.Width};
                        canvas.DrawRect(bounds.X,bounds.Y,bounds.Width,bounds.Height,pen);
                    }
                    int frameSave=canvas.Save();
                    try
                    {
                        if(frame.Transform is {} transform)
                        {
                            canvas.Translate(transform.X,transform.Y);
                            canvas.RotateDegrees(transform.Degrees);
                        }
                        DrawStyledPage(canvas,frame.Content,bounds.Fill is uint rgbFill ?
                            new SKColor((byte)(rgbFill>>16),(byte)(rgbFill>>8),(byte)rgbFill) : paper,ink);
                    }
                    finally{canvas.RestoreToCount(frameSave);}
                }
            }
        }
        finally
        {
            foreach(var image in images.Values)image.Dispose();
            canvas.RestoreToCount(save);
        }
    }

    static void DrawImage(SKCanvas canvas,Dictionary<string,SKBitmap> bitmaps,InlineImage image,float x,float y,LineComposer? painter)
    {
        if(image.Diagram is {} diagram)
        {
            DrawDiagram(canvas,diagram,x,y,painter??throw new InvalidDataException("PAGE_DRAW_LEASE"));
            return;
        }
        if(image.VmlVector is {} vector)
        {
            foreach(var polygon in vector.Polygons)
            {
                using var path=new SKPath();
                var first=polygon.Vertices[0];
                path.MoveTo(x+first.X,y+first.Y);
                foreach(var vertex in polygon.Vertices.AsSpan()[1..])path.LineTo(x+vertex.X,y+vertex.Y);
                path.Close();
                using var fill=new SKPaint{Color=new SKColor(0xff000000|polygon.Rgb),IsAntialias=true};
                canvas.DrawPath(path,fill);
            }
            return;
        }
        if(image.Metafile is not {} picture)
        {
            if(!bitmaps.TryGetValue(image.ResourceId,out var bitmap))
                bitmaps[image.ResourceId]=bitmap=SKBitmap.Decode(image.RenderBytes)??throw new InvalidDataException("LAYOUT_IMAGE_NATIVE_DECODE");
            DrawRaster(canvas,bitmap,image,x,y);
            return;
        }
        int save=canvas.Save();
        try
        {
            var target=new SKRect(x,y,x+image.Width,y+image.Height);
            canvas.ClipRect(target);
            if(image.FlipX||image.FlipY)canvas.Scale(image.FlipX?-1:1,image.FlipY?-1:1,target.MidX,target.MidY);
            var crop=image.Crop;
            canvas.Translate(x,y);
            canvas.Scale(image.Width/((1-crop.Left-crop.Right)*picture.Width),
                image.Height/((1-crop.Top-crop.Bottom)*picture.Height));
            canvas.Translate(-picture.Width*crop.Left,-picture.Height*crop.Top);
            using var paint=new SKPaint{Color=SKColors.Black,Style=SKPaintStyle.Stroke,StrokeWidth=1,IsAntialias=true};
            using var path=new SKPath();
            int textIndex=0;
            foreach(var stroke in picture.Strokes)
            {
                while(textIndex<picture.Texts.Length&&picture.Texts[textIndex].Order<stroke.Order)
                    DrawTextOutline(picture.Texts[textIndex++]);
                var first=stroke.Points[0];
                paint.Color=new((byte)(stroke.Pen.Rgb>>16),(byte)(stroke.Pen.Rgb>>8),(byte)stroke.Pen.Rgb);
                paint.StrokeWidth=stroke.Pen.Width;
                path.MoveTo((float)((long)first.X-picture.Left)+.5f,(float)((long)first.Y-picture.Top)+.5f);
                foreach(var point in stroke.Points.AsSpan()[1..])
                    path.LineTo((float)((long)point.X-picture.Left)+.5f,(float)((long)point.Y-picture.Top)+.5f);
                canvas.DrawPath(path,paint);
                path.Rewind();
            }
            while(textIndex<picture.Texts.Length)DrawTextOutline(picture.Texts[textIndex++]);
            if(!picture.Fills.IsEmpty)
            {
                paint.Style=SKPaintStyle.Fill;
                int clipSave=canvas.Save();
                MetafilePath? activeClip=null;
                foreach(var fill in picture.Fills)
                {
                    if(!ReferenceEquals(fill.Clip,activeClip))
                    {
                        canvas.RestoreToCount(clipSave);
                        clipSave=canvas.Save();
                        activeClip=fill.Clip;
                        if(activeClip is not null)
                        {
                            MakePath(path,activeClip);
                            canvas.ClipPath(path,SKClipOperation.Intersect,antialias:true);
                        }
                    }
                    MakePath(path,fill.Path);
                    paint.Color=new((byte)(fill.Rgb>>16),(byte)(fill.Rgb>>8),(byte)fill.Rgb);
                    canvas.DrawPath(path,paint);
                }
                canvas.RestoreToCount(clipSave);
            }

            void DrawTextOutline(MetafileText text)
            {
                using var outline=SKPath.ParseSvgPathData(text.Outline)??throw new InvalidDataException("PAGE_WMF_OUTLINE");
                using var fill=new SKPaint{IsAntialias=true,Style=SKPaintStyle.Fill,
                    Color=new((byte)(text.Rgb>>16),(byte)(text.Rgb>>8),(byte)text.Rgb)};
                canvas.DrawPath(outline,fill);
            }
        }
        finally{canvas.RestoreToCount(save);}

        static void MakePath(SKPath target,MetafilePath source)
        {
            target.Rewind();
            target.FillType=source.Winding?SKPathFillType.Winding:SKPathFillType.EvenOdd;
            foreach(var command in source.Commands)
                switch(command.Verb)
                {
                    case MetafilePathVerb.Move: target.MoveTo(command.Point.X,command.Point.Y);break;
                    case MetafilePathVerb.Line: target.LineTo(command.Point.X,command.Point.Y);break;
                    case MetafilePathVerb.Cubic:
                        target.CubicTo(command.Point.X,command.Point.Y,command.Second.X,command.Second.Y,command.Third.X,command.Third.Y);
                        break;
                    case MetafilePathVerb.Close: target.Close();break;
                }
        }
    }

    static void DrawRaster(SKCanvas canvas,SKBitmap bitmap,InlineImage image,float x,float y)
    {
        var target=new SKRect(x,y,x+image.Width,y+image.Height);
        int save=image.FlipX||image.FlipY?canvas.Save():-1;
        try
        {
            if(save>=0)canvas.Scale(image.FlipX?-1:1,image.FlipY?-1:1,target.MidX,target.MidY);
            if(image.Crop==default)canvas.DrawBitmap(bitmap,target);
            else
            {
                var crop=image.Crop;
                var source=new SKRect(bitmap.Width*crop.Left,bitmap.Height*crop.Top,
                    bitmap.Width*(1-crop.Right),bitmap.Height*(1-crop.Bottom));
                canvas.DrawBitmap(bitmap,source,target);
            }
        }
        finally{if(save>=0)canvas.RestoreToCount(save);}
    }
}
