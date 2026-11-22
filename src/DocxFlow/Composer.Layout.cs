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

    static Composition PlaceMeasured(PaginatedDocument document,List<MeasuredParagraph> measured,LayoutFonts fonts,Action Check,bool native=false,Action? firstPage=null)
    {
        var allMeasured=measured.ToDictionary(m=>m.Paragraph.Id);
        var measuredTables=document.Tables.ToDictionary(t=>t.Id,t=>MeasureTable(t,allMeasured,Check));
        measured=measured.Where(m=>m.Paragraph.CellOwner is null).ToList();
        if(measured.Count==0)return new([], [..document.Issues,new("LAYOUT_NO_PARAGRAPHS",null,"Aucun paragraphe qualifié à composer.",true)]);
        var keep=native&&measured.Any(p=>p.Paragraph.Properties.KeepNext)?PlanNativeKeeps(measured,measuredTables,Check):null;
        var placementIssues=new List<FlowIssue>();
        int oversizedThrough=-1;
        var pages=new List<ComposedPage>();var current=new List<PageLine>();var currentCells=new List<PageCell>();PageGeometry geometry=measured[0].Paragraph.Geometry;int section=measured[0].Paragraph.Section;float cursor=geometry.Top,after=0;
        void Page(bool force=false)
        {
            Check();if(current.Count==0&&currentCells.Count==0&&!force)return;
            if(pages.Count>=500)throw new InvalidDataException("LAYOUT_PAGE_LIMIT");
            ValidateCellBorders(currentCells,Check);
            pages.Add(new(pages.Count+1,current.ToImmutableArray()){Geometry=geometry,Section=current.FirstOrDefault()?.Section??section,Cells=currentCells.ToImmutableArray()});current.Clear();currentCells.Clear();cursor=geometry.Top;after=0;
            if(pages.Count==1)firstPage?.Invoke();
        }
        for(int pi=0;pi<measured.Count;pi++)
        {
            Check();var item=measured[pi];var p=item.Paragraph;var props=p.Properties;
            if(p.Section!=section)
            {
                if(p.SectionStart=="nextPage")Page(native&&current.Count==0&&currentCells.Count==0&&
                    pi>0&&measured[pi-1].Lines.LastOrDefault()?.PageBreak==true);
                else if(p.SectionStart!="continuous"||p.Geometry!=geometry)throw new LayoutFailure("LAYOUT_CONTINUOUS_GEOMETRY_UNQUALIFIED",p.Source);
                geometry=p.Geometry;section=p.Section;if(current.Count==0)cursor=geometry.Top;
            }
            if(p.TableId is {} tableId)
            {
                var table=measuredTables[tableId];
                float x=TableLeft(table.Table,geometry,table.Table.Columns.Sum());
                int headers=table.Table.Rows.TakeWhile(r=>r.Header).Count();float headerHeight=table.Heights.Take(headers).Sum();
                if(headerHeight>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_TABLE_HEADER_TOO_TALL");
                var firstBody=table.Groups.FirstOrDefault(g=>g.First>=headers);
                float firstBodyHeight=headers<table.Table.Rows.Count?table.Heights.Skip(firstBody.First).Take(firstBody.Last-firstBody.First+1).Sum():0;
                if(headers>0&&headerHeight+firstBodyHeight>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_TABLE_HEADER_BODY_TOO_TALL");
                if(cursor+after+headerHeight+firstBodyHeight>geometry.Height-geometry.Bottom+.01f)Page();else cursor+=after;
                after=0;
                foreach(var group in table.Groups)
                {
                    Check();float height=table.Heights.Skip(group.First).Take(group.Last-group.First+1).Sum();
                    if(height>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_TABLE_ROW_GROUP_TOO_TALL");
                    if(cursor+height>geometry.Height-geometry.Bottom+.01f)
                    {
                        Page();
                        if(group.First>=headers&&headers>0)
                        {
                            if(headerHeight+height>geometry.BodyHeight+.01f)throw new InvalidDataException("LAYOUT_TABLE_HEADER_BODY_TOO_TALL");
                            PlaceTableRows(table,0,headers-1,x,cursor,true,current,currentCells,fonts,Check);cursor+=headerHeight;
                        }
                    }
                    PlaceTableRows(table,group.First,group.Last,x,cursor,false,current,currentCells,fonts,Check);cursor+=height;
                }
                continue;
            }
            if(props.PageBreakBefore)Page();
            float before=Math.Max(after,props.Before);
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
                if(native&&line==segmentEnd)
                {
                    segmentStart=line;segmentHeight=0;
                    while(segmentEnd<item.Lines.Count)
                    {
                        Check();var entry=item.Lines[segmentEnd++];segmentHeight+=entry.Height;
                        if(entry.PageBreak)break;
                    }
                    float spacing=segmentStart==0?before:0;
                    followingMinimum=0;
                    if(keep?[pi] is {} continuation&&segmentEnd==item.Lines.Count&&!item.Lines[segmentEnd-1].PageBreak)
                    {
                        float chain=segmentHeight+continuation.Following;
                        float atTop=segmentStart==0?props.Before:0;
                        if(pi>oversizedThrough)
                        {
                            if(atTop+chain>geometry.BodyHeight+.01f)
                            {
                                oversizedThrough=continuation.Through;
                                placementIssues.Add(new("PAGE_KEEP_NEXT_SPANS_PAGES",p.Source,
                                    "Chaîne plus haute qu’une page. Début en haut de page et coupures selon les contraintes des lignes.",false));
                                Page();spacing=atTop;
                            }
                            else if(cursor+spacing+chain>geometry.Height-geometry.Bottom+.01f)
                            {Page();spacing=atTop;}
                        }
                        if(pi<=oversizedThrough)
                        {
                            followingMinimum=continuation.Minimum;
                            int tailCount=props.Widow?2:1;
                            int segmentCount=segmentEnd-segmentStart;
                            bool canSplitTail=!props.KeepLines&&segmentCount>=2*tailCount;
                            float tailHeight=canSplitTail?TailHeight(item,segmentEnd,tailCount):atTop+segmentHeight;
                            if(tailHeight+followingMinimum>geometry.BodyHeight+.01f)
                                placementIssues.Add(new("PAGE_KEEP_NEXT_LINK_EXCEEDS_PAGE",p.Source,
                                    "La dernière portion admissible et le début du bloc suivant excèdent ensemble la hauteur de page.",false));
                        }
                    }
                    cursor+=spacing;
                }
                if(native&&props.KeepLines&&line==segmentStart)
                {
                    if(segmentHeight>geometry.BodyHeight+.01f)throw new LayoutFailure("LAYOUT_KEEP_LINES_TOO_LARGE",p.Source);
                    if(cursor+segmentHeight>geometry.Height-geometry.Bottom+.01f)
                    {
                        if(current.Count==0&&currentCells.Count==0)throw new LayoutFailure("LAYOUT_KEEP_LINES_TOO_LARGE",p.Source);
                        Page();cursor+=line==0?props.Before:0;
                    }
                }
                Check();int count=0;float height=0;
                while(line+count<segmentEnd)
                {
                    var candidate=item.Lines[line+count];
                    if(cursor+height+candidate.Height>geometry.Height-geometry.Bottom+.01f)break;
                    height+=candidate.Height;count++;if(candidate.PageBreak)break;
                }
                if(count==0)
                {
                    if(current.Count==0&&currentCells.Count==0)throw new LayoutFailure("LAYOUT_LINE_TOO_TALL",p.Source);
                    Page();if(native&&line==0)cursor+=props.Before;continue;
                }
                bool explicitBreak=item.Lines[line+count-1].PageBreak;
                if(props.Widow&&!explicitBreak&&line+count<segmentEnd)
                {
                    if(!native&&line==0&&count==1&&current.Count>0){Page();continue;}
                    if(segmentEnd-(line+count)==1&&count>1)count--;
                    if(native&&line==segmentStart&&count==1&&current.Count>0){Page();continue;}
                    if(count==1&&segmentEnd-line>1&&current.Count==0)throw new InvalidDataException("LAYOUT_WIDOW_UNSATISFIABLE");
                }
                if(native&&followingMinimum>0&&line+count==segmentEnd&&
                    cursor+height+followingMinimum>geometry.Height-geometry.Bottom+.01f)
                {
                    int tail=props.Widow?2:1;
                    if(!props.KeepLines&&count-tail>=tail&&
                        TailHeight(item,segmentEnd,tail)+followingMinimum<=geometry.BodyHeight+.01f)
                        count-=tail;
                    else if((current.Count>0||currentCells.Count>0)&&height+followingMinimum+(line==0?props.Before:0)<=geometry.BodyHeight+.01f)
                    {
                        Page();if(line==0)cursor+=props.Before;continue;
                    }
                }
                for(int j=0;j<count;j++)
                {
                    var l=item.Lines[line];
                    if(l.Native is not null)
                    {
                        current.Add(PositionNative(l,p,geometry.Left,cursor,0,0,geometry.Width,geometry.Height));
                        cursor+=l.Height;line++;
                        if(l.PageBreak)
                        {
                            Page(true);
                            if(line<item.Lines.Count&&pi==oversizedThrough)oversizedThrough=-1;
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
                if(line<item.Lines.Count&&current.Count>0)Page();
            }
            after=props.After;
        }
        // A final manual break creates a deliberate, source-backed blank page.
        bool finalBreak=measured[^1].Lines.LastOrDefault()?.PageBreak==true;
        Page(finalBreak||pages.Count==0);
        return new(pages,placementIssues.Count==0?document.Issues:[..document.Issues,..placementIssues]);

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
            next.SectionStart=="continuous"&&next.Geometry==first.Geometry);

    static float TailHeight(MeasuredParagraph paragraph,int end,int length)
    {
        float result=0;
        for(int i=end-length;i<end;i++)result+=paragraph.Lines[i].Height;
        return result;
    }

    readonly record struct KeepContinuation(float Following,float Minimum,int Through);

    static KeepContinuation?[] PlanNativeKeeps(IReadOnlyList<MeasuredParagraph> paragraphs,
        IReadOnlyDictionary<string,MeasuredTable> tables,Action check)
    {
        var plan=new KeepContinuation?[paragraphs.Count];
        float firstHeight=0,firstMinimum=0;
        bool firstHasBreak=false;
        for(int i=paragraphs.Count-1;i>=0;i--)
        {
            check();
            var current=paragraphs[i];var p=current.Paragraph;
            if(i+1<paragraphs.Count&&p.Properties.KeepNext&&CanJoinNative(p,paragraphs[i+1].Paragraph)&&
                current.Lines.LastOrDefault()?.PageBreak!=true)
            {
                float spacing=Math.Max(p.Properties.After,paragraphs[i+1].Paragraph.Properties.Before);
                var continuation=firstHasBreak?null:plan[i+1];
                plan[i]=new(spacing+firstHeight+(continuation?.Following??0),spacing+firstMinimum,continuation?.Through??i+1);
            }
            if(p.TableId is {} table)
            {
                firstHeight=firstMinimum=TableStartHeight(tables[table]);firstHasBreak=true;
                continue;
            }
            firstHeight=firstMinimum=0;firstHasBreak=false;
            int count=0;
            foreach(var line in current.Lines)
            {
                check();
                firstHeight+=line.Height;
                if(p.Properties.KeepLines||count<(p.Properties.Widow?2:1))firstMinimum+=line.Height;
                count++;
                if(line.PageBreak){firstHasBreak=true;break;}
            }
            if(p.Properties.Widow&&count==3)firstMinimum=firstHeight;
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
    static void DrawStyledPage(SKCanvas canvas,ComposedPage page)
    {
        DrawCells(canvas,page);
        var bitmaps=new Dictionary<string,SKBitmap>();
        try
        {
            foreach(var image in page.DisplayLines.SelectMany(l=>l.Images))
            {
                if(!bitmaps.TryGetValue(image.Image.ResourceId,out var bitmap))bitmaps[image.Image.ResourceId]=bitmap=SKBitmap.Decode(image.Image.RenderBytes)??throw new InvalidDataException("LAYOUT_IMAGE_NATIVE_DECODE");
                canvas.DrawBitmap(bitmap,new SKRect(image.X,image.Y,image.X+image.Image.Width,image.Y+image.Image.Height));
            }
        }
        finally{foreach(var bitmap in bitmaps.Values)bitmap.Dispose();}
        using var fonts=new LayoutFonts();using var paint=new SKPaint{IsAntialias=true};
        foreach(var line in page.DisplayLines)
        {
            if(line.Native is {} native)
                (page.NativePainter??throw new InvalidDataException("PAGE_DRAW_LEASE")).Paint(canvas,native,line.NativeOffsetX,line.Baseline,SKColors.Black);
            foreach(var f in line.Fragments)
            {
                var font=fonts.Get(f.Style);paint.Color=SKColor.Parse("#"+f.Style.Color);canvas.DrawText(f.Text,f.X,line.Baseline,font,paint);
                if(f.Style.Underline){paint.StrokeWidth=Math.Max(.5f,font.Metrics.UnderlineThickness??.5f);float y=line.Baseline+(font.Metrics.UnderlinePosition??1);canvas.DrawLine(f.X,y,f.X+font.MeasureText(f.Text),y,paint);}
            }
        }
        DrawCellBorders(canvas,page);
    }
}
