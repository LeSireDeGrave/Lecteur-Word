using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using SkiaSharp;

namespace LecteurWord.Flow;

internal sealed partial class LineComposer
{
    internal bool PermitsPdfEmbedding(FontResolution face)=>session.PermitsPdfEmbedding(face);
    internal void PaintDiagramLabel(SKCanvas canvas,DiagramTextLine line)=>
        session.Paint(canvas,line.Fragment,line.X,line.Baseline,SKColors.Black);
    readonly record struct Slot(int Part,int Cluster,byte Level,long Width,long Height,long PenX,long PenY,
        bool Space,bool Trailing,char Separator,TextControl? Tab,LineDecoration? Decoration);
    sealed record Geometry(ImmutableArray<LinePlacement> Visual,ImmutableArray<LineTabPlacement> Tabs,
        ImmutableArray<LineDecoration> Generated,ImmutableArray<LineInlineBox> Boxes,ImmutableArray<LineUnderline> Underlines,
        long Occupied,long Advance,long Ascent,long Descent,long Height);

    Geometry? Place(TextParagraph p,LineProperties props,Bounds bounds,ImmutableArray<LinePart> parts,byte[] levels,
        Utf16Span line,LineCutKind cut,SemanticSource source,ImmutableArray<FlowIssue>.Builder issues,LineDecoration? marker,LineDecoration? pageField)
    {
        int trim=parts.Length==0?line.EndExclusive:parts[^1].ContextRange.EndExclusive;
        if(parts.Length>0&&parts[^1].Control?.Kind=="Break")trim--;
        if(pageField is null)
            while(trim>line.Start&&p.Context[trim-1]==' ')trim--;
        var slots=new List<Slot>();
        long ascent=0,descent=0,gap=0;
        for(int i=0;i<parts.Length;i++)
        {
            Check();
            var part=parts[i];
            if(part.Control is {} c)
            {
                if(c.Style?.Background is not null)return Unknown("LINE_CONTROL_SHADING_UNQUALIFIED",c.Source);
                byte level=levels[c.Offset-line.Start];
                var slot=new Slot(i,-1,level,0,0,0,0,false,false,'\0',c.Kind=="Tab"?c:null,null);
                if(Image(c) is {} image)
                {
                    slot=slot with {Width=ImageEmu(image.Width)};
                    ascent=Math.Max(ascent,ImageEmu(image.ReservedHeight));
                }
                if(c.Symbol is {Measure:{} symbol} fragmentSymbol)
                {
                    if(symbol.Metrics.Width<0)return Unknown("LINE_NEGATIVE_ADVANCE",c.Source);
                    Include(symbol);
                    string kind=c.Kind=="InlinePageField"?"pageField":"wordSymbol";
                    slot=slot with{Width=symbol.Metrics.Width,Decoration=new(kind,fragmentSymbol,0,0,1,c.Source)};
                }
                if(c.Kind=="NoBreakHyphen")
                {
                    var d=c.NonBreakingHyphen is {} hyphen&&level%2==0?new LineDecoration("noBreakHyphen",hyphen,0,0,1,c.Source):
                        Decoration("noBreakHyphen","\u2011",c.Style??p.MarkStyle,c.Source,level);
                    if(d is null)return Unknown("LINE_NOBREAK_HYPHEN_FACE",c.Source);
                    var m=d.Fragment.Measure!;
                    Include(m);
                    slot=slot with {Width=m.Metrics.Width,Decoration=d};
                }
                if(NoteNumber(p,c,level) is {} noteNumber)
                {
                    var measure=noteNumber.Fragment.Measure!;
                    Include(measure);
                    slot=slot with{Width=measure.Metrics.Width,Decoration=noteNumber};
                }
                else if(noteNumbers?.ContainsKey(c.Source.NodeId)==true)
                    return Unknown("LINE_NOTE_NUMBER_FACE",c.Source);
                slots.Add(slot);
                continue;
            }
            var fragment=part.Fragment!;
            if(fragment.Measure is not {} shape)return Unknown("LINE_MISSING_SLICE_MEASUREMENT",fragment.Sources[0].Source);
            Include(shape);
            var penX=new long[shape.Glyphs.Length+1];
            var penY=new long[penX.Length];
            for(int j=0;j<shape.Glyphs.Length;j++)
            {
                penX[j+1]=penX[j]+shape.Glyphs[j].AdvanceX;
                penY[j+1]=penY[j]+shape.Glyphs[j].AdvanceY;
            }
            for(int j=0;j<shape.Clusters.Length;j++)
            {
                Check();
                var cluster=shape.Clusters[j];
                var text=fragment.Text.AsSpan(cluster.Range.Start,cluster.Range.EndExclusive-cluster.Range.Start);
                // A literal soft hyphen retains its source and range. At a taken break the
                // generated hyphen below supplies the full visible advance.
                if(IsSoftHyphen(text))
                {
                    slots.Add(new(i,-1,fragment.BidiLevel,0,0,0,0,false,false,'\0',null,null));
                    continue;
                }
                bool space=OnlySpaces(text);
                int first=cluster.GlyphCount==0?0:cluster.FirstGlyph;
                int after=first+cluster.GlyphCount;
                long width=penX[after]-penX[first],height=penY[after]-penY[first];
                if(!TrackingQualified(fragment,cluster,width))return Unknown("LINE_TRACKING_CLUSTER_UNQUALIFIED",fragment.Sources[0].Source);
                if(width<0)return Unknown("LINE_NEGATIVE_ADVANCE",fragment.Sources[0].Source);
                slots.Add(new(i,j,fragment.BidiLevel,width,height,penX[first],penY[first],space,
                    pageField is null&&space&&fragment.ContextOffset+cluster.Range.Start>=trim,text.Length==1?text[0]:'\0',null,null));
            }
        }
        if(cut is LineCutKind.SoftHyphen or LineCutKind.AutomaticHyphen)
        {
            var last=parts[^1];
            var f=last.Fragment;
            var origin=last.Control?.Source??SourceOf(f!,f!.Text.Length-1);
            byte level=slots.Count==0?(byte)(p.RightToLeft?1:0):slots[^1].Level;
            bool automatic=cut==LineCutKind.AutomaticHyphen;
            if(automatic)origin=origin with {Span=null};
            var d=Decoration(automatic?"automaticHyphen":"softHyphen","\u2010",
                last.Control?.Style??f?.Style??p.MarkStyle,origin,level);
            if(d is null)return Unknown("LINE_SOFT_HYPHEN_FACE",origin);
            var m=d.Fragment.Measure!;
            Include(m);
            slots.Add(new(-1,-1,level,m.Metrics.Width,0,0,0,false,false,'\0',null,d));
        }
        if(pageField is {} dField)
        {
            var measured=dField.Fragment.Measure!;
            Include(measured);
            slots.Add(new(-1,-1,dField.Fragment.BidiLevel,measured.Metrics.Width,0,0,0,false,false,'\0',null,dField));
        }
        if(ascent==0&&descent==0)
        {
            var mark=Decoration("paragraphMetrics"," ",p.MarkStyle,source,(byte)(p.RightToLeft?1:0));
            if(mark is null)return Unknown("LINE_MARK_METRICS",source);
            Include(mark.Fragment.Measure!);
        }
        var placed=ImmutableArray.CreateBuilder<LinePlacement>();
        var tabs=ImmutableArray.CreateBuilder<LineTabPlacement>();
        var generated=ImmutableArray.CreateBuilder<LineDecoration>();
        if(marker is not null)
        {
            generated.Add(marker with{X=marker.X+bounds.Region.Left});
            Include(marker.Fragment.Measure!);
        }
        var boxes=ImmutableArray.CreateBuilder<LineInlineBox>();
        long pen=bounds.Left;
        long baselineY=0, trailing=0;
        var visualSlots=new List<Slot>();
        var visualTabs=new List<(TextControl Control,LineTabStop Stop,long From,long To)>();
        TextControl? pending=null;
        for(int first=0;first<=slots.Count;)
        {
            int last=first;
            while(last<slots.Count&&slots[last].Tab is null){Check();last++;}
            var field=slots.GetRange(first,last-first);
            Order(field);
            if(pending is {} tab)
            {
                var stop=NextStop(props,bounds,pen,out var outside);
                if(outside is {} unsupported&&issues.All(i=>i.Source!=unsupported.Location||i.Code!="LINE_TAB_OUTSIDE_PAPER"))
                    issues.Add(new("LINE_TAB_OUTSIDE_PAPER",unsupported.Location,
                        "Taquet gauche hors papier. Le caractère de tabulation reste placé sur la grille automatique.",false));
                long width=field.Where(s=>!s.Trailing).Sum(s=>s.Width);
                long? decimalWidth=null;
                if(stop.Kind==LineTabKind.Decimal)
                {
                    string? language=field.Where(s=>s.Part>=0).Select(s=>parts[s.Part].Fragment?.Language).FirstOrDefault(l=>l is not null);
                    char separator=DecimalSeparator(language);
                    long before=0;
                    if(field.Any(s=>s.Level%2!=0))return Unknown("LINE_DECIMAL_BIDI",tab.Source);
                    foreach(var slot in field)
                    {
                        Check();
                        if(slot.Separator==separator)
                        {
                            if(decimalWidth is not null)return Unknown("LINE_DECIMAL_AMBIGUOUS",tab.Source);
                            decimalWidth=before;
                        }
                        before+=slot.Width;
                    }
                }
                long target=bounds.TabOrigin+stop.Position-TabInset(stop.Kind,width,decimalWidth);
                if(target<pen)return Unknown("LINE_TAB_OVERLAP",tab.Source);
                visualTabs.Add((tab,stop,pen,target));
                pen=target;
            }
            visualSlots.AddRange(field);
            if(last<slots.Count)
            {
                visualSlots.Add(slots[last]);
                pending=slots[last].Tab;
            }
            else break;
            pen+=field.Sum(s=>s.Width);
            first=last+1;
        }
        // Pen above stopped before the final field. A second pass positions every visual slot with
        // the same widths, while preserving a distinct range and advance for each source tab.
        int tabIndex=0;
        pen=bounds.Left;
        foreach(var slot in visualSlots)
        {
            if(slot.Tab is not null)pen=visualTabs[tabIndex++].To;
            else pen+=slot.Width;
            if(slot.Trailing)trailing+=slot.Width;
        }
        long advance=pen-bounds.Left,occupied=advance-trailing;
        bool fixedLeftTabs=visualTabs.All(tab=>tab.Stop.Kind==LineTabKind.Left)&&!p.RightToLeft;
        var expansion=Expand(props,visualSlots,visualTabs.Count,fixedLeftTabs,cut,bounds.Width-occupied,source,issues);
        long extra=expansion.Sum();
        // The alignment is applied to the measured line. The compatibility flag omits
        // only the last non-left tab's advance from the width used for alignment.
        long alignmentWidth=occupied;
        if(props.ForgetLastTabAlignment&&visualTabs.Count>0&&
            visualTabs[^1] is {Stop.Kind:LineTabKind.Center or LineTabKind.Right or LineTabKind.Decimal} lastTab)
            alignmentWidth-=lastTab.To-lastTab.From;
        long free=bounds.Width-alignmentWidth;
        if(!bounds.Region.AllowHorizontalOverflow&&bounds.Region.CenteredImageOverhang==0)free=Math.Max(0,free);
        long shift=props.Alignment switch
        {
            LineAlignment.Right=>free,
            LineAlignment.Center=>free/2,
            _=>0
        };
        // UBA L1 sends trailing visual spaces to the right in LTR and to the left in RTL.
        pen=bounds.Left+shift-(p.RightToLeft?trailing:0);
        ImmutableArray<LineUnderline>.Builder? underlines=null;
        Dictionary<int,(long Top,long Bottom)>? underlineMetrics=null;
        if(marker is {Fragment.Style.Underline:"single" or "double"})
        {
            var text=marker.Fragment;
            var origin=text.Style.Origins.GetValueOrDefault("underline",marker.Cause);
            if(p.RightToLeft||session.UnderlineInk(text) is not {} metrics)
                return Unknown("PAGE_INLINE_DECORATION_PENDING",origin);
            long left=marker.X+bounds.Region.Left;
            AddUnderline(text,origin,metrics,left,left+text.Measure!.Metrics.Width,0);
        }
        tabIndex=0;
        for(int i=0;i<visualSlots.Count;i++)
        {
            Check();
            var slot=visualSlots[i];
            if(slot.Tab is {} tab)
            {
                var t=visualTabs[tabIndex++];
                long fill=t.To-t.From;
                placed.Add(new(slot.Part,-1,pen,pen,baselineY,fill,0,0));
                tabs.Add(new(tab.Source,t.Stop,t.From+shift,t.To+shift));
                if(t.Stop.Leader!="none"&&fill>0)
                {
                    string? glyph=t.Stop.Leader switch{"dot"=>".","hyphen"=>"-","underscore"=>"_","middleDot"=>"·",_=>null};
                    var leader=glyph is null?null:Decoration("leader",glyph,tab.Style??p.MarkStyle,tab.Source,slot.Level);
                    if(leader is {} l&&l.Fragment.Measure!.Metrics.Width is >0 and var step)
                    {
                        long count=fill/step;
                        if(count>256)issues.Add(new("LINE_LEADER_LIMIT",tab.Source.Location,"Suite dessinée jusqu’au plafond de 256 glyphes.",false));
                        int repetitions=(int)Math.Min(count,256);
                        if(repetitions>0)
                        {
                            Count(checked(repetitions*l.Fragment.Measure.Glyphs.Length));
                            generated.Add(l with{X=t.From+shift+(fill-count*step)/2,Step=step,Count=repetitions});
                            Include(l.Fragment.Measure);
                        }
                    }
                    else issues.Add(new("LINE_LEADER_FACE",tab.Source.Location,"Suite de tabulation conservée sans dessin qualifié.",false));
                }
                pen=t.To+shift;
                continue;
            }
            if(slot.Part>=0)
            {
                placed.Add(new(slot.Part,slot.Cluster,pen,pen-slot.PenX,baselineY-slot.PenY,slot.Width,slot.Height,expansion[i]));
                if(parts[slot.Part].Control is {} control&&Image(control) is {} image)
                    boxes.Add(new(control.Source,pen,slot.Width,ImageEmu(image.ReservedHeight),image));
                if(slot.Cluster>=0&&!slot.Trailing&&slot.Width+expansion[i]>0&&
                    parts[slot.Part].Fragment is {Style.Underline:"single" or "double"} text)
                {
                    var origin=text.Style.Origins.GetValueOrDefault("underline",text.Sources[0].Source);
                    underlineMetrics??=[];
                    if(!underlineMetrics.TryGetValue(slot.Part,out var stroke))
                    {
                        if(p.RightToLeft||(slot.Level&1)!=0||session.UnderlineInk(text) is not {} measured)
                            return Unknown("PAGE_INLINE_DECORATION_PENDING",origin);
                        underlineMetrics.Add(slot.Part,stroke=measured);
                    }
                    AddUnderline(text,origin,stroke,pen,pen+slot.Width+expansion[i],baselineY);
                }
            }
            if(slot.Decoration is {} d)
            {
                if(d is {Kind:"noteNumber",Fragment.Style.Underline:"single" or "double"}&&slot.Width>0)
                {
                    var origin=d.Fragment.Style.Origins.GetValueOrDefault("underline",d.Cause);
                    if(p.RightToLeft||(slot.Level&1)!=0||session.UnderlineInk(d.Fragment,useRaisedBaseline:true) is not {} stroke)
                        return Unknown("PAGE_INLINE_DECORATION_PENDING",origin);
                    AddUnderline(d.Fragment,origin,stroke,pen,pen+slot.Width,baselineY);
                }
                generated.Add(d with{X=pen});
            }
            pen+=slot.Width+expansion[i];
            baselineY+=slot.Height;
        }
        return new(placed.ToImmutable(),tabs.ToImmutable(),generated.ToImmutable(),boxes.ToImmutable(),underlines?.ToImmutable()??[],occupied+extra,advance+extra,
            ascent,descent,ascent+descent+gap);

        void AddUnderline(TextFragment text,SemanticSource origin,(long Top,long Bottom) stroke,long left,long right,long baseline)
        {
            long top=baseline+stroke.Top,bottom=baseline+stroke.Bottom;
            string color=text.Style.UnderlineColor=="auto"?text.Style.Color:text.Style.UnderlineColor;
            var rules=underlines??=ImmutableArray.CreateBuilder<LineUnderline>();
            rules.Add(new(origin,new(left,top,right,bottom),color));
            if(text.Style.Underline=="double")
            {
                long thickness=Math.Max(1,bottom-top);
                rules.Add(new(origin,new(left,bottom+thickness,right,bottom+2*thickness),color));
                bottom+=2*thickness;
            }
            ascent=Math.Max(ascent,-top);
            descent=Math.Max(descent,bottom);
        }

        void Include(TextMeasure measure)
        {
            var m=measure.Metrics;
            ascent=Math.Max(ascent,Math.Max(m.Ascent,m.Ink is {} box?-box.Top:0));
            descent=Math.Max(descent,Math.Max(m.Descent,m.Ink?.Bottom??0));
            gap=Math.Max(gap,m.LineGap);
        }
        Geometry? Unknown(string code,SemanticSource origin)
        {
            issues.Add(new(code,origin.Location,"Géométrie refusée pour cette ligne. Contenu et ancre conservés.",false));
            return null;
        }
    }

    internal static bool TrackingQualified(TextFragment fragment,TextCluster cluster,long advance)=>
        fragment.Style.Tracking==0||
        (Math.Abs(fragment.Style.Tracking)<=1&&(fragment.BidiLevel&1)==0&&advance>0&&
            cluster.GlyphCount==1&&cluster.Range.EndExclusive-cluster.Range.Start==1);

    static long[] Expand(LineProperties props,List<Slot> visual,int tabs,bool fixedLeftTabs,LineCutKind cut,long remaining,
        SemanticSource source,ImmutableArray<FlowIssue>.Builder issues)
    {
        var extras=new long[visual.Count];
        if(props.Alignment!=LineAlignment.Justify||cut is not (LineCutKind.Allowed or LineCutKind.SoftHyphen or LineCutKind.AutomaticHyphen)||remaining<=0)
            return extras;
        int start=0;
        if(tabs>0)
        {
            if(!fixedLeftTabs||visual.Any(s=>(s.Level&1)!=0))
            {
                issues.Add(new("LINE_JUSTIFY_TABS_PARTIAL",source.Location,"La justification combinée à des taquets non gauches ou au texte bidirectionnel est hors profil.",false));
                return extras;
            }
            // Keep each first glyph attached to its fixed tab stop.
            // Only the interval after the final tab can expand to the right margin.
            start=visual.FindLastIndex(s=>s.Tab is not null)+1;
        }
        int first=visual.FindIndex(start,s=>!s.Space&&s.Width>0),last=visual.FindLastIndex(s=>!s.Space&&s.Width>0);
        if(first<0||last<=first)return extras;
        var spaces=Enumerable.Range(first+1,last-first-1).Where(i=>visual[i].Space&&!visual[i].Trailing).ToArray();
        for(int i=0;i<spaces.Length;i++)extras[spaces[i]]=remaining/spaces.Length+(i<remaining%spaces.Length?1:0);
        return extras;
    }

    static void Order(List<Slot> slots)
    {
        if(slots.Count==0)return;
        int last=slots.Max(s=>(int)s.Level);
        for(int level=last;level>=1;level--)
            for(int start=0;start<slots.Count;)
            {
                if(slots[start].Level<level){start++;continue;}
                int end=start+1;
                while(end<slots.Count&&slots[end].Level>=level)end++;
                slots.Reverse(start,end-start);
                start=end;
            }
    }

    static LineTabStop NextStop(LineProperties props,Bounds bounds,long x,out SemanticSource? outside)=>
        NextStop(props,bounds.TabOrigin,x,bounds.Region.PaperRight,out outside);

    static LineTabStop NextStop(LineProperties props,long origin,long x)=>
        NextStop(props,origin,x,null,out _);

    static LineTabStop NextStop(LineProperties props,long origin,long x,long? paperRight,out SemanticSource? outside)
    {
        outside=null;
        long position=x-origin;
        foreach(var stop in props.Tabs)
        {
            if(stop.Position<=position)continue;
            if(stop is {Kind:LineTabKind.Left,Leader:"none",Source:{} declaration}&&paperRight is {} right&&
                origin+stop.Position>right)
            {
                outside??=declaration;
                continue;
            }
            return stop;
        }
        long next=(Math.Max(0,position)/props.DefaultTab+1)*props.DefaultTab;
        return new(next,LineTabKind.Left,"none",props.DefaultTabSource);
    }

    static long TabInset(LineTabKind kind,long width,long? decimalWidth)=>kind switch
    {
        LineTabKind.Right=>width,LineTabKind.Center=>width/2,LineTabKind.Decimal=>decimalWidth??width,_=>0
    };

    static char DecimalSeparator(string? language)
    {
        if(language is null or "" or "und")return '.';
        // ICU language and explicit source language drive the decimal separator, independently of UI culture.
        try
        {
            string symbol=CultureInfo.GetCultureInfo(language).NumberFormat.NumberDecimalSeparator;
            return symbol.Length==1?symbol[0]:'.';
        }
        catch(CultureNotFoundException){return '.';}
    }

    LineDecoration? Decoration(string kind,string value,TextStyle style,SemanticSource source,byte level)
    {
        var key=(style,value,level);
        if(!decorations.TryGetValue(key,out var fragment))
        {
            long start=Stopwatch.GetTimestamp();
            fragment=session.ShapeLineDecoration(source.NodeId,value,style,source,level,Check,Count);
            shapeTicks+=Stopwatch.GetTimestamp()-start;
            if(decorations.Count<256)decorations.Add(key,fragment);
        }
        if(fragment is null)return null;
        return new(kind,fragment with {Sources=[fragment.Sources[0] with {Source=source with {Span=null}}]},0,0,1,source);
    }

    static SemanticSource SourceOf(TextFragment fragment,int position)
    {
        var slice=fragment.Sources.First(x=>position>=x.FragmentRange.Start&&position<x.FragmentRange.EndExclusive);
        var span=slice.Source.Span!.Value;
        int start=span.Start+position-slice.FragmentRange.Start;
        return slice.Source with {Span=new Utf16Span(start,start+1)};
    }

    /// <summary>Uses the recorded placements; no call to SKFont.MeasureText or second shaping.</summary>
    internal void Paint(SKCanvas canvas,ComposedLine line,float x,float baseline,SKColor automaticColor)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        session.CheckOwner(document,model);
        if(line.ParagraphSource.SnapshotId!=document.SnapshotId)throw new InvalidDataException("LINE_PAINT_SNAPSHOT");
        if(line.Occupied is null)return;
        if(line.Parts.Any(p=>p.Fragment?.Style.Background is not null)||line.Generated.Any(d=>d.Fragment.Style.Background is not null))
        {
            using var fill=new SKPaint();
            foreach(var slot in line.Visual)
            {
                if(slot.Cluster<0||slot.AdvanceX+slot.ExtraAfter<=0||
                    line.Parts[slot.Part].Fragment is not {Style.Background:{} color,Measure:{} measure} fragment)continue;
                DrawBackground(color,measure.Metrics,fragment.Style.BaselineShift,slot.X,slot.AdvanceX+slot.ExtraAfter);
            }
            foreach(var entry in line.Generated)
                if(entry.Fragment is {Style.Background:{} color,Measure:{} measure})
                    for(int i=0;i<entry.Count;i++)
                        DrawBackground(color,measure.Metrics,entry.Fragment.Style.BaselineShift,entry.X+i*entry.Step,measure.Metrics.Width);

            void DrawBackground(string color,TextMetrics metrics,float shift,long left,long width)
            {
                fill.Color=SKColor.Parse(color);
                canvas.DrawRect(new SKRect(x+PageComposer.Points(left),baseline-shift-PageComposer.Points(metrics.Ascent),
                    x+PageComposer.Points(left+width),baseline-shift+PageComposer.Points(metrics.Descent)),fill);
            }
        }
        if(!line.Underlines.IsEmpty)
        {
            using var paint=new SKPaint{IsAntialias=true};
            foreach(var underline in line.Underlines)
            {
                paint.Color=underline.Color=="auto"?automaticColor:SKColor.Parse(underline.Color);
                var box=underline.Ink;
                canvas.DrawRect(new SKRect(x+PageComposer.Points(box.Left),baseline+PageComposer.Points(box.Top),
                    x+PageComposer.Points(box.Right),baseline+PageComposer.Points(box.Bottom)),paint);
            }
        }
        var glyphGroups=line.Visual.Where(p=>p.Cluster>=0).ToLookup(p=>p.Part);
        if(line.Parts.Any(p=>p.Fragment?.Style.Shadow is {Alpha:>0})||line.Generated.Any(d=>d.Fragment.Style.Shadow is {Alpha:>0}))
            DrawGlyphs(shadowOnly:true);
        DrawGlyphs(shadowOnly:false);

        void DrawGlyphs(bool shadowOnly)
        {
            foreach(var group in glyphGroups)
            {
                var fragment=line.Parts[group.Key].Fragment!;
                if(shadowOnly&&fragment.Style.Shadow is not {Alpha:>0})continue;
                var measure=fragment.Measure!;
                var glyphs=ImmutableArray.CreateBuilder<TextGlyph>(measure.Glyphs.Length);
                foreach(var p in group)
                {
                    var cluster=measure.Clusters[p.Cluster];
                    for(int i=0;i<cluster.GlyphCount;i++)
                    {
                        var g=measure.Glyphs[cluster.FirstGlyph+i];
                        glyphs.Add(g with {X=g.X+p.GlyphOriginX,Y=g.Y+p.GlyphOriginY});
                    }
                }
                session.Paint(canvas,fragment with{Measure=measure with{Glyphs=glyphs.ToImmutable()}},x,baseline,automaticColor,shadowOnly);
            }
            foreach(var d in line.Generated)
            {
                if(shadowOnly&&d.Fragment.Style.Shadow is not {Alpha:>0})continue;
                for(int i=0;i<d.Count;i++)
                    session.Paint(canvas,d.Fragment,x+(float)(d.X+i*d.Step)/TypographySession.EmuPerPoint,baseline,automaticColor,shadowOnly);
            }
        }
    }
}
