using System.Collections.Immutable;
using System.Diagnostics;
using SkiaSharp;

namespace LecteurWord.Flow;

/// <summary>Document-owned line composition using the typography session that produced the snapshot.</summary>
internal sealed partial class LineComposer : IDisposable
{
    readonly SemanticDocument model;
    readonly TypographySession session;
    readonly TypographyDocument document;
    readonly LineBudget limits;
    readonly LinePropertiesReader styles;
    readonly bool diagnosticsLimited;
    readonly IReadOnlyDictionary<string,InlineImage>? images;
    readonly Dictionary<(string Id,int LineStart,int LineEnd,int Start,int End,byte Level),TextMeasure> shapes=[];
    readonly Dictionary<(TextStyle Style,string Text,byte Level),TextFragment?> decorations=[];
    long cachedGlyphs;
    bool disposed;
    int work,glyphs,lines,hits,reshaped;
    long segmentTicks,shapeTicks;
    Stopwatch clock=new();
    CancellationToken cancellation;
    bool composing;

    internal LineComposer(SemanticDocument model,TypographyDocument document,TypographySession session,LineBudget? limits=null,
        IReadOnlyDictionary<string,InlineImage>? images=null,TableStyleCascade? tableStyles=null)
    {
        session.CheckOwner(document,model);
        this.model=model;this.document=document;this.session=session;this.limits=limits??new();
        if(this.limits is not {MaxLines:>0 and <=30_000,MaxWork:>0 and <=5_000_000,MaxGlyphs:>0 and <=500_000,Ms:>0 and <=15_000})
            throw new ArgumentOutOfRangeException(nameof(limits));
        styles=new(model,tableStyles);
        this.images=images;
        diagnosticsLimited=document.Diagnostics.Any(d=>d.Code=="TEXT_DIAGNOSTIC_LIMIT");
    }

    internal ComposedLines Compose(Func<LineRequest,LineRegion> region,CancellationToken token=default,
        IReadOnlySet<string>? includedParagraphs=null,IReadOnlyDictionary<string,MeasuredListMarker>? markers=null,
        IEnumerable<TextParagraph>? orderedParagraphs=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(composing)throw new InvalidOperationException("LINE_REENTRANT");
        session.CheckOwner(document,model);
        composing=true;
        cancellation=token;
        clock.Restart();
        work=glyphs=lines=hits=reshaped=0;
        segmentTicks=shapeTicks=0;
        try
        {
            Check();
            var result=ImmutableArray.CreateBuilder<ComposedLine>();
            foreach(var p in orderedParagraphs??document.Paragraphs)
            {
                Check();
                if(includedParagraphs is not null&&!includedParagraphs.Contains(p.Id))continue;
                long start=Stopwatch.GetTimestamp();
                var marker=markers?.GetValueOrDefault(p.Id);
                var props=marker?.Properties??styles.Read(p,nativeNumbering:markers is not null);
                var atoms=Prepare(p);
                segmentTicks+=Stopwatch.GetTimestamp()-start;
                ComposeParagraph(p,props,atoms,region,result,marker);
            }
            Check();
            return new(document,result.ToImmutable(),new(Ms(segmentTicks),clock.Elapsed.TotalMilliseconds-Ms(segmentTicks),
                Ms(shapeTicks),reshaped,hits,shapes.Count,cachedGlyphs,work,glyphs));
        }
        finally{clock.Stop();cancellation=default;composing=false;}
    }

    void Check()
    {
        cancellation.ThrowIfCancellationRequested();
        if(++work>limits.MaxWork)throw new InvalidDataException("LINE_WORK_LIMIT");
        if(clock.ElapsedMilliseconds>=limits.Ms)throw new InvalidDataException("LINE_TIME_LIMIT");
    }

    void Count(int count)
    {
        if(count>limits.MaxGlyphs-glyphs)throw new InvalidDataException("LINE_GLYPH_LIMIT");
        glyphs+=count;
    }

    public void Dispose()
    {
        if(composing)throw new InvalidOperationException("LINE_REENTRANT");
        disposed=true;shapes.Clear();decorations.Clear();cachedGlyphs=0;
    }

    static double Ms(long value)=>value*1000d/Stopwatch.Frequency;

    readonly record struct Atom(int Start,int End,int Fragment,TextControl? Control,long? Width,bool Space,bool Soft,bool Hard);
    sealed record ParagraphAtoms(Atom[] Items,long[] Prefix,int[] VisibleEnd,int[] NextTab,HashSet<int> Cuts);
    readonly record struct Bounds(LineRegion Region,long Left,long Right)
    {
        internal long Width=>Right-Left;
    }

    ParagraphAtoms Prepare(TextParagraph p)
    {
        var items=new List<Atom>();
        var hard=p.Breaks.Where(b=>b.Mandatory).Select(b=>b.Offset).ToHashSet();
        for(int i=0;i<p.Fragments.Length;i++)
        {
            Check();
            var f=p.Fragments[i];int origin=f.ContextOffset;
            if(f.Measure is not {} measure)
            {
                items.Add(new(origin,origin+f.Text.Length,i,null,null,false,false,hard.Contains(origin+f.Text.Length)));
                continue;
            }
            foreach(var cluster in measure.Clusters)
            {
                Check();
                int start=origin+cluster.Range.Start,end=origin+cluster.Range.EndExclusive;
                var text=p.Context.AsSpan(start,end-start);
                long width=IsSoftHyphen(text)?0:GlyphAdvance(measure,cluster,false);
                items.Add(new(start,end,i,null,cluster.MissingGlyph?null:width,OnlySpaces(text),text[^1]=='\u00ad',hard.Contains(end)));
            }
        }
        foreach(var c in p.Controls)
        {
            Check();
            bool isBreak=c.Kind=="Break";
            long? width=c.Kind is "Tab" or "Break" or "SoftHyphen" or "NoBreakHyphen"?0:null;
            if(Image(c) is {} image)width=ImageEmu(image.Width);
            if(c.Symbol?.Measure is {} symbol)width=symbol.Metrics.Width;
            items.Add(new(c.Offset,c.Offset+1,-1,c,width,false,c.Kind=="SoftHyphen",isBreak||hard.Contains(c.Offset+1)));
        }
        var atoms=items.OrderBy(a=>a.Start).ToArray();
        var prefix=new long[atoms.Length+1];
        var visible=new int[atoms.Length+1];
        var nextTab=new int[atoms.Length+1];nextTab[^1]=atoms.Length;
        var cuts=p.Breaks.Select(b=>b.Offset).ToHashSet();
        var forbidden=p.Fragments.SelectMany(f=>f.Measure?.Clusters.Where(c=>c.UnsafeBefore)
            .Select(c=>f.ContextOffset+c.Range.Start)??[]).ToHashSet();
        for(int i=0;i<atoms.Length;i++)
        {
            Check();
            var a=atoms[i];
            if(a.Start!=(i==0?0:atoms[i-1].End))throw new InvalidDataException("LINE_CONTEXT_COVERAGE");
            if(!forbidden.Contains(a.End)&&(a.Hard||a.Soft))cuts.Add(a.End);
            prefix[i+1]=checked(prefix[i]+(a.Width??0));
            visible[i+1]=a.Space||a.Control?.Kind=="Break"?visible[i]:i+1;
        }
        for(int i=atoms.Length-1;i>=0;i--)nextTab[i]=atoms[i].Control?.Kind=="Tab"?i:nextTab[i+1];

        // Unsafe clusters always win. Explicit breaks have isolated context units.
        cuts.RemoveWhere(c=>forbidden.Contains(c)&&c!=p.ContextLength);
        for(int i=0;i<atoms.Length;i++)
            if(atoms[i] is { Hard:true, Control:null } a&&forbidden.Contains(a.End))
                atoms[i]=a with {Hard=false};
        if((atoms.Length==0?0:atoms[^1].End)!=p.ContextLength)throw new InvalidDataException("LINE_CONTEXT_COVERAGE");
        return new(atoms,prefix,visible,nextTab,cuts);
    }

    void ComposeParagraph(TextParagraph p,LineProperties props,ParagraphAtoms atoms,Func<LineRequest,LineRegion> regions,
        ImmutableArray<ComposedLine>.Builder result,MeasuredListMarker? marker)
    {
        var source=model.Source(p.Id);
        string bidiText=BidiText(p);
        using var bidi=bidiText.Length==0?null:TextUnicode.Instance.OpenLines(bidiText,p.RightToLeft);
        int from=0,ordinal=0;
        bool blank=atoms.Items.Length==0;
        while(from<atoms.Items.Length||blank)
        {
            Check();
            if(++lines>limits.MaxLines)throw new InvalidDataException("LINE_COUNT_LIMIT");
            int offset=from<atoms.Items.Length?atoms.Items[from].Start:p.ContextLength;
            var region=regions(new(p.Id,p.StoryId,ordinal,offset));
            if(string.IsNullOrEmpty(region.Id)||region.Left is < -1_000_000_000 or >1_000_000_000||
                region.Width is <=0 or >1_000_000_000)throw new InvalidDataException("LINE_REGION");
            long indent=ordinal==0?props.First:0;
            var firstMarker=ordinal==0?marker:null;
            var bounds=new Bounds(region,checked(region.Left+(firstMarker?.TextStart??(props.Left+(p.RightToLeft?0:indent)))),
                checked(region.Left+region.Width-props.Right-(p.RightToLeft?indent:0)));
            int end=blank?from:Choose(atoms,p,props,bounds,from);
            ComposedLine line;
            while(true)
            {
                Check();
                line=BuildLine(p,props,source,atoms,bidi,bounds,ordinal,from,end,firstMarker);
                bool overlap=line.Occupied is null&&line.Diagnostics.Any(d=>d.Code=="LINE_TAB_OVERLAP");
                if(!overlap&&(line.Occupied is not {} used||used<=bounds.Width))break;
                int previous=end-1;
                while(previous>from&&!atoms.Cuts.Contains(atoms.Items[previous-1].End)){Check();previous--;}
                if(previous<=from)break;
                end=previous;
            }
            result.Add(line);
            // A terminal forced line break leaves a paragraph mark on an empty logical line.
            blank=end>from&&end==atoms.Items.Length&&line.Cut==LineCutKind.Line;
            from=end;ordinal++;
        }
    }

    static string BidiText(TextParagraph p)
    {
        if(!p.Controls.Any(c=>c.Kind=="Break"))return p.Context;
        char[] text=p.Context.ToCharArray();
        foreach(var c in p.Controls.Where(c=>c.Kind=="Break"))text[c.Offset]='\u2028';
        return new(text);
    }

    int Choose(ParagraphAtoms data,TextParagraph p,LineProperties props,Bounds b,int from)
    {
        int lastFit=from;
        var atoms=data.Items;
        for(int i=from;i<atoms.Length;i++)
        {
            Check();
            var atom=atoms[i];
            int end=i+1;
            if(atom.Width is null||atom.Control?.Kind=="Tab"&&p.RightToLeft)
            {
                // Without an advance, the next soft boundary is unknown. Preserve the hard segment.
                while(end<atoms.Length&&!atoms[end-1].Hard){Check();end++;}
                return end;
            }
            bool terminal=end==atoms.Length||atom.Hard;
            if(!terminal&&!data.Cuts.Contains(atom.End))continue;
            long natural;
            if(data.NextTab[from]>=end)
                natural=data.Prefix[Math.Max(from,data.VisibleEnd[end])]-data.Prefix[from];
            else
                natural=EstimateTabs(data,p,props,b,from,end);
            if(natural<=b.Width)
            {
                lastFit=end;
                if(terminal)return end;
            }
            else return lastFit>from?lastFit:end;
        }
        return atoms.Length;
    }

    long EstimateTabs(ParagraphAtoms data,TextParagraph p,LineProperties props,Bounds bounds,int from,int end)
    {
        var items=data.Items;
        int tab=data.NextTab[from];
        long cursor=bounds.Left+data.Prefix[tab]-data.Prefix[from];
        while(tab<end)
        {
            Check();
            var stop=NextStop(props,bounds.Region.Left,cursor);
            int next=Math.Min(end,data.NextTab[tab+1]);
            int edge=next==end?Math.Max(tab+1,data.VisibleEnd[next]):next;
            long width=data.Prefix[edge]-data.Prefix[tab+1];
            long? decimalWidth=null;
            if(stop.Kind==LineTabKind.Decimal)
            {
                char separator=DecimalSeparator(items.Skip(tab+1).Take(edge-tab-1)
                    .Select(a=>a.Fragment>=0?p.Fragments[a.Fragment].Language:null).FirstOrDefault(l=>l is not null));
                for(int i=tab+1;i<edge;i++)
                {
                    Check();
                    if(p.Context[items[i].Start]==separator)
                    {decimalWidth=data.Prefix[i]-data.Prefix[tab+1];break;}
                }
            }
            long target=bounds.Region.Left+stop.Position-TabInset(stop.Kind,width,decimalWidth);
            if(target<cursor)return long.MaxValue;
            cursor=target+width;
            tab=next;
        }
        return cursor-bounds.Left;
    }

    ComposedLine BuildLine(TextParagraph p,LineProperties props,SemanticSource source,ParagraphAtoms data,TextUnicode.BidiLines? bidi,
        Bounds b,int ordinal,int from,int end,MeasuredListMarker? marker)
    {
        int begin=from<data.Items.Length?data.Items[from].Start:p.ContextLength;
        int finish=end>from?data.Items[end-1].End:begin;
        LineCutKind cut=Cut(data,p,end,from);
        var range=new Utf16Span(begin,finish);
        var issues=props.Issues.ToBuilder();
        if(marker is not null)issues.AddRange(marker.Issues);
        if(diagnosticsLimited)
            issues.Add(new("LINE_TEXT_DIAGNOSTICS_LIMIT",source.Location,"Diagnostic typographique détaillé borné.",false));
        bool missing=marker is {TextStart:null};
        foreach(var atom in data.Items.AsSpan(from,end-from))
        {
            Check();
            if(atom.Control is {} control)
            {
                if(control.Kind=="Tab"&&model.Name(control.Source.NodeId)==SemanticIndex.QName(SemanticIndex.W,"ptab"))
                {missing=true;Issue("LINE_POSITIONAL_TAB_PENDING",control.Source,"Taquet positionnel à résoudre avec ses marges et sa région.");}
                if(control.Kind=="Break"&&model.Attribute(control.Source.NodeId,"clear") is {} clear&&clear!="none")
                    Issue("LINE_BREAK_CLEAR_PENDING",control.Source,"Dégagement du flottant à résoudre dans la région de page.");
            }
            if(atom.Width is null)
            {
                missing=true;
                var f=atom.Fragment>=0?p.Fragments[atom.Fragment]:null;
                var origin=f?.Sources.FirstOrDefault().Source.Location??atom.Control?.Source.Location??source.Location;
                issues.Add(new(f?.MeasurementIssue??(atom.Control is {Kind:"Symbol"}?"LINE_WORD_SYMBOL_UNRESOLVED":
                    f is null?"LINE_INLINE_PENDING":"TEXT_NOTDEF"),origin,
                    f is null?"Objet inline conservé sans avance qualifiée.":"Texte et étendue d’origine conservés sans mesure qualifiée.",false));
            }
        }
        if(b.Width<=0)Issue("LINE_INDENT_OUTSIDE",source,"Les retraits épuisent la région.");
        if(p.RightToLeft&&data.NextTab[from]<end)
        {missing=true;Issue("LINE_BIDI_TABS",data.Items[data.NextTab[from]].Control!.Value.Source,"Tabulations en paragraphe RTL hors profil.");}
        bool positionsKnown=!missing&&b.Width>0;
        byte[] levels=positionsKnown&&bidi is not null?bidi.Levels(begin,finish):[];
        var parts=Parts(p,data.Items,from,end,range,levels,positionsKnown);
        foreach(var part in parts)
            if(part.Fragment?.Measure?.Clusters.Any(c=>c.MissingGlyph)==true)
            {
                positionsKnown=false;
                Issue("LINE_RESHAPE_NOTDEF",part.Fragment.Sources[0].Source,"Le nouveau contexte produit un glyphe sans couverture.");
            }
        var geometry=positionsKnown?Place(p,props,b,parts,levels,range,cut,source,issues,marker?.Decoration):null;
        if(geometry?.Occupied>b.Width)Issue("LINE_OVERFLOW",source,"Segment indivisible plus large que la région disponible.");
        return new(p.Id,p.StoryId,source,ordinal,b.Region,b.Left,b.Right,range,cut,props,parts,
            geometry?.Visual??[],geometry?.Tabs??[],geometry?.Generated??[],geometry?.Occupied,
            geometry?.Advance,geometry?.Ascent,geometry?.Descent,geometry?.Ascent,geometry?.Height,
            issues.Count==0&&geometry is not null?LineCoverage.Complete:LineCoverage.Partial,issues.ToImmutable())
            {Boxes=geometry?.Boxes??[],ListMarker=marker};

        void Issue(string code,SemanticSource origin,string message)=>issues.Add(new(code,origin.Location,message,false));
    }

    LineCutKind Cut(ParagraphAtoms data,TextParagraph p,int end,int from)
    {
        if(end==from)return LineCutKind.End;
        var a=data.Items[end-1];
        if(a.Control?.Kind=="Break")
            return model.Attribute(a.Control.Value.Source.NodeId,"type") switch
            {
                "page"=>LineCutKind.Page,"column"=>LineCutKind.Column,_=>LineCutKind.Line
            };
        if(a.Hard&&a.End<p.ContextLength)return LineCutKind.Mandatory;
        if(a.End==p.ContextLength)return LineCutKind.End;
        return a.Soft?LineCutKind.SoftHyphen:LineCutKind.Allowed;
    }

    ImmutableArray<LinePart> Parts(TextParagraph p,Atom[] atoms,int from,int end,Utf16Span line,byte[] levels,bool reshape)
    {
        var parts=ImmutableArray.CreateBuilder<LinePart>();
        string? context=null;
        for(int index=from;index<end;)
        {
            Check();
            var a=atoms[index];
            if(a.Control is {} control)
            {
                parts.Add(new(new(a.Start,a.End),null,control,a.Width is null?"LINE_INLINE_PENDING":null));
                index++;continue;
            }
            int next=index+1;
            while(next<end&&atoms[next].Fragment==a.Fragment)next++;
            var f=p.Fragments[a.Fragment];
            int limit=atoms[next-1].End;
            int start=a.Start;
            if(!reshape)
            {
                Add(start,limit,f.BidiLevel);
            }
            else
            {
                byte level=levels[start-line.Start];
                foreach(var graph in Graphemes(f,start,limit))
                {
                    Check();
                    int offset=f.ContextOffset+graph;
                    if(offset==start)continue;
                    byte candidate=levels[offset-line.Start];
                    if(candidate==level)continue;
                    Add(start,offset,level);
                    start=offset;level=candidate;
                }
                Add(start,limit,level);
            }
            index=next;

            void Add(int left,int right,byte level)
            {
                bool full=left==f.ContextOffset&&right==f.ContextOffset+f.Text.Length;
                bool changedEdge=(left==line.Start&&line.Start>0)||(right==line.EndExclusive&&line.EndExclusive<p.ContextLength);
                TextMeasure? measured=full?f.Measure:null;
                if(reshape&&f.Measure is not null&&f.LegacySymbol is null&&(!full||level!=f.BidiLevel||changedEdge))
                {
                    var key=(p.Id,line.Start,line.EndExclusive,left,right,level);
                    if(shapes.TryGetValue(key,out var hit))
                    {hits++;measured=hit;Count(hit.Glyphs.Length);}
                    else
                    {
                        long startTime=Stopwatch.GetTimestamp();
                        context??=p.Context[line.Start..line.EndExclusive];
                        int[] starts=[..Graphemes(f,left,right).Select(x=>x+f.ContextOffset-left)];
                        if(starts.Length==0||starts[0]!=0)throw new InvalidDataException("LINE_GRAPHEME_BOUNDARY");
                        measured=session.ReshapeLine(f,context,left-line.Start,right-left,level,starts,Check,Count);
                        shapeTicks+=Stopwatch.GetTimestamp()-startTime;reshaped++;
                        if(shapes.Count<512&&cachedGlyphs+measured.Glyphs.Length<=64_000)
                        {shapes.Add(key,measured);cachedGlyphs+=measured.Glyphs.Length;}
                    }
                }
                var fragment=full&&level==f.BidiLevel&&ReferenceEquals(measured,f.Measure)?f:Slice(f,left,right,level,measured);
                parts.Add(new(new(left,right),fragment,null,fragment.MeasurementIssue));
            }
        }
        return parts.ToImmutable();
    }

    TextFragment Slice(TextFragment fragment,int start,int end,byte level,TextMeasure? measure)
    {
        int left=start-fragment.ContextOffset,right=end-fragment.ContextOffset;
        var sources=ImmutableArray.CreateBuilder<TextSlice>();
        foreach(var s in fragment.Sources)
        {
            int a=Math.Max(left,s.FragmentRange.Start),b=Math.Min(right,s.FragmentRange.EndExclusive);
            if(a>=b)continue;
            var original=s.Source.Span!.Value;
            var exact=model.Source(s.Source.NodeId,new(original.Start+a-s.FragmentRange.Start,original.Start+b-s.FragmentRange.Start));
            sources.Add(s with{Source=exact,FragmentRange=new(a-left,b-left)});
        }
        return fragment with
        {
            ContextOffset=start,Text=fragment.Text[left..right],Sources=sources.ToImmutable(),BidiLevel=level,Measure=measure,
            Breaks=[],GraphemeStarts=[..Graphemes(fragment,start,end).Select(x=>x-left)]
        };
    }

    static IEnumerable<int> Graphemes(TextFragment fragment,int start,int end)
    {
        int left=start-fragment.ContextOffset,right=end-fragment.ContextOffset;
        int index=fragment.GraphemeStarts.BinarySearch(left);
        if(index<0)index=~index;
        for(;index<fragment.GraphemeStarts.Length&&fragment.GraphemeStarts[index]<right;index++)
            yield return fragment.GraphemeStarts[index];
    }

    static bool OnlySpaces(ReadOnlySpan<char> text)=>text.Length>0&&text.IndexOfAnyExcept(' ')<0;
    InlineImage? Image(TextControl control)=>control.Kind=="Drawing"?images?.GetValueOrDefault(control.Source.NodeId):null;
    static long ImageEmu(float points)=>checked((long)Math.Round((double)points*TypographySession.EmuPerPoint));
    static bool IsSoftHyphen(ReadOnlySpan<char> text)=>text.Length==1&&text[0]=='\u00ad';

    static long GlyphAdvance(TextMeasure measure,TextCluster cluster,bool vertical)
    {
        long size=0;
        for(int i=0;i<cluster.GlyphCount;i++)
        {
            var g=measure.Glyphs[cluster.FirstGlyph+i];
            size+=vertical?g.AdvanceY:g.AdvanceX;
        }
        return size;
    }
}
