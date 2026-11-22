using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LecteurWord.Flow;

internal sealed partial class TypographySession
{
    sealed record ContextSlice(int Start, int End, TextToken Token);
    sealed record Graph(int Start, int End, ContextSlice Slice, byte Level, string Script, bool CanShape)
    {
        internal bool PrivateUse {get;init;}
    }
    sealed record SegmentKey(TextStyle Style, byte Level, string Script, string Language, string Slot,
        string Family, FontRequest? Request, ThemeFont? Theme, bool Complex, bool CanShape)
    {
        internal float Size => Complex ? Style.ComplexSize ?? Style.Size : Style.Size;
        internal bool Bold => Complex ? Style.ComplexBold ?? Style.Bold : Style.Bold;
        internal bool Italic => Complex ? Style.ComplexItalic ?? Style.Italic : Style.Italic;
    }

    readonly Dictionary<(string Family,bool Bold,bool Italic,bool ThemeMissing,string Language,string Text),FontResolution?> fontUses = [];
    static readonly HashSet<string> EastAsianScripts = ["Hani","Hira","Kana","Hang","Bopo","Yiii"];
    static readonly HashSet<string> ComplexScripts =
        ["Arab","Hebr","Syrc","Thaa","Nkoo","Adlm","Deva","Beng","Taml","Telu","Knda","Mlym","Gujr","Guru","Orya","Sinh","Thai","Laoo","Mymr","Khmr","Tibt","Mong"];

    TextParagraph BuildParagraph(SemanticElement paragraph,IEnumerable<TextToken> tokens)
    {
        long start=Stopwatch.GetTimestamp();
        var mark=styles.Resolve(paragraph.Id,true);
        var (rtl,directionOrigin)=styles.ParagraphDirection(paragraph.Id);
        styleTicks+=Stopwatch.GetTimestamp()-start;

        start=Stopwatch.GetTimestamp();
        var builder=new StringBuilder();
        var slices=new List<ContextSlice>();
        foreach(var item in tokens)
        {
            Check();
            string? text=item.Role switch
            {
                TextRole.Source or TextRole.StoredField when item.Style?.Hidden!=true => item.Original,
                TextRole.Control when item.Control=="Symbol"&&item.Style?.Hidden==true => null,
                TextRole.Control => item.Control switch
                {
                    "Tab"=>"\t","Break"=>"\n","SoftHyphen"=>"\u00ad","NoBreakHyphen"=>"\u2011",_=>"\ufffc"
                },
                _=>null
            };
            if (string.IsNullOrEmpty(text))continue;
            if (text.Length>limits.MaxParagraphChars-builder.Length)throw new InvalidDataException("TEXT_PARAGRAPH_LIMIT");
            int offset=builder.Length;
            builder.Append(text);
            slices.Add(new(offset,builder.Length,item));
        }

        string context=builder.ToString();
        var fragments=new List<TextFragment>();
        var graphs=new List<Graph>();
        var keys=new List<SegmentKey>();
        if(context.Length>0)
        {
            var levels=unicode.Levels(context,rtl);
            var boundaries=new SortedSet<int>(unicode.Boundaries(context,true,"und",Check).Select(b=>b.Offset));
            foreach(var control in slices.Where(s=>s.Token.Role==TextRole.Control))
            {
                if(control.Start>0)boundaries.Add(control.Start);
                boundaries.Add(control.End);
            }
            int first=0, startAt=0;
            foreach (var boundary in boundaries)
            {
                Check();
                while(slices[first].End<=startAt)first++;
                var slice=slices[first];
                string script=StrongScript(context.AsSpan(startAt,boundary-startAt));
                bool shape=slice.Token.Role!=TextRole.Control && slice.Token.Style is not null;
                if(shape && boundary>slice.End)
                {
                    for(int i=first+1;i<slices.Count&&slices[i].Start<boundary;i++)
                    {
                        var other=slices[i].Token;
                        if(other.Role==TextRole.Control || other.Style is null ||
                            (other.Style with { Origins = ImmutableDictionary<string,SemanticSource>.Empty }) !=
                            (slice.Token.Style! with { Origins = ImmutableDictionary<string,SemanticSource>.Empty }))
                        {
                            shape=false;
                            Issue("TEXT_GRAPH_STYLE_CONFLICT",slice.Token.Source.NodeId,
                                "Graphème sur plusieurs propriétés incompatibles. Affichage et coupure non qualifiés.");
                            break;
                        }
                    }
                }
                bool privateUse=LegacySymbols.Private(context[startAt]);
                if(!privateUse&&boundary-startAt>1)
                    foreach(var scalar in context.AsSpan(startAt,boundary-startAt).EnumerateRunes())
                        if(LegacySymbols.Private(scalar.Value)){privateUse=true;break;}
                graphs.Add(new(startAt,boundary,slice,levels[startAt],script,shape){PrivateUse=privateUse});
                startAt=boundary;
            }

            ResolveNeutralScripts(graphs);
            foreach(var graph in graphs)
            {
                Check();
                keys.Add(Key(context,graph,mark));
            }
        }
        segmentTicks+=Stopwatch.GetTimestamp()-start;

        int sourceCursor=0;
        for(int i=0;i<graphs.Count;)
        {
            Check();
            var graph=graphs[i];var key=keys[i];
            if(graph.Slice.Token.Role==TextRole.Control){i++;continue;}
            if(graph.PrivateUse)
            {
                string original=context[graph.Start..graph.End];
                var symbol=LegacySymbols.SingleScalar(original,out int code)?
                    MeasureSymbol(paragraph.Id,graph.Start,original,code,key,Check,CountGlyphs):null;
                Add(i,i+1,null,symbol);
                i++;
                continue;
            }
            int end=i+1;
            while(end<graphs.Count&&graphs[end].Slice.Token.Role!=TextRole.Control&&!graphs[end].PrivateUse&&keys[end]==key)end++;
            var resolution=Select(context[graph.Start..graphs[end-1].End],key);
            if (resolution is not null || !key.CanShape)
                Add(i,end,resolution);
            else
            {
                int position=i;
                FontResolution? previous=null;
                for(int candidate=i;candidate<end;candidate++)
                {
                    Check();
                    var next=Select(context[graphs[candidate].Start..graphs[candidate].End],key);
                    if(candidate>position&&next!=previous)
                    {
                        Add(position,candidate,previous);
                        position=candidate;
                    }
                    previous=next;
                }
                Add(position,end,previous);
            }
            i=end;
        }

        start=Stopwatch.GetTimestamp();
        var cuts = LineBreaks(context,graphs,keys,fragments);
        var byCut=cuts.ToLookup(b=>FindFragment(fragments,b.Offset));
        for(int i=0;i<fragments.Count;i++)
            fragments[i]=fragments[i] with { Breaks=[..byCut[i].Select(b=>b with {Offset=b.Offset-fragments[i].ContextOffset})] };
        var order=VisualOrder(fragments);
        segmentTicks+=Stopwatch.GetTimestamp()-start;
        return new(paragraph.Id,paragraph.StoryId!,rtl,mark,[..fragments],order)
        {
            ContextLength=context.Length,
            Context=context,
            DirectionOrigin=directionOrigin,
            Breaks=cuts,
            Controls=[..slices.Where(s=>s.Token.Role==TextRole.Control).Select(Control)]
        };

        TextControl Control(ContextSlice slice)
        {
            var token=slice.Token;
            var control=new TextControl(token.Source,slice.Start,token.Control!){Style=token.Style};
            if(token.Control!="Symbol")return control;
            Check();
            string? family=model.Attribute(token.Source.NodeId,"font");
            if(family is not {Length:>0 and <=128}||token.Original is not {Length:4} raw||
                !int.TryParse(raw,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out int code)||!Rune.IsValid(code)||code<0x20)
            {
                Issue("TEXT_WORD_SYMBOL_INVALID",token.Source.NodeId,"Déclaration w:sym conservée avec ses attributs. Police ou code non qualifié.");
                return control;
            }
            var style=token.Style??mark;
            string original=char.ConvertFromUtf32(code);
            var key=Key(original,new Graph(0,original.Length,slice,0,"Zsym",true),style) with
            {Family=family,Request=new(family,false,token.Source.Location,"font"),Theme=null,Slot="symbol",CanShape=true};
            var symbol=MeasureSymbol(paragraph.Id,slice.Start,original,code,key,Check,CountGlyphs);
            ReportSymbol(symbol,token.Source.NodeId);
            return control with {Symbol=symbol with
                {Sources=[new(token.Source,token.RunId,style,new(0,original.Length)){Role=TextRole.Control}]}};
        }

        void Add(int from,int to,FontResolution? font,TextFragment? symbol=null)
        {
            int offset=graphs[from].Start, end=graphs[to-1].End;
            SegmentKey descriptor=keys[from];
            var source=ImmutableArray.CreateBuilder<TextSlice>();
            while (slices[sourceCursor].End<=offset)sourceCursor++;
            for (int j=sourceCursor;j<slices.Count&&slices[j].Start<end;j++)
            {
                var s=slices[j];
                int left=Math.Max(offset,s.Start),right=Math.Min(end,s.End);
                if(s.Token.Source.Span is not {} span||s.Token.Style is null)continue;
                var exact=model.Source(s.Token.Source.NodeId,new(span.Start+left-s.Start,span.Start+right-s.Start));
                source.Add(new(exact,s.Token.RunId,s.Token.Style,new(left-offset,right-offset)){Role=s.Token.Role});
            }
            string text=context[offset..end];
            string id=graphs[from].Slice.Token.Source.NodeId;
            if(symbol is not null)
            {
                ReportSymbol(symbol,id);
                fragments.Add(symbol with {Sources=source.ToImmutable()});
                return;
            }
            if (font is null && descriptor.CanShape)
                Issue("TEXT_MISSING_FACE",id,"Aucune face admise ne couvre ce fragment. Texte et étendue conservés.");
            else if (font?.Status=="substituted")
                Issue("TEXT_FONT_SUBSTITUTION",id,font.RequestedFamily+" → "+font.UsedFamily+". "+font.Reason);
            var features=Features(descriptor);
            int[] starts=[..graphs.Skip(from).Take(to-from).Select(g=>g.Start-offset)];
            TextMeasure? measured=font is null ? null : Shape(paragraph.Id,context,offset,end,descriptor,font,features,id,starts);
            fragments.Add(new(paragraph.Id,offset,text,source.ToImmutable(),descriptor.Script,descriptor.Language,descriptor.Level,
                descriptor.Style,descriptor.Slot,descriptor.Family,descriptor.Request,descriptor.Theme?.Source,
                font is null ? null : font with { Data=null },descriptor.Size,features,measured,[])
            {
                GraphemeStarts=ImmutableCollectionsMarshal.AsImmutableArray(starts),
                MeasurementIssue=font is not null?null:descriptor.CanShape?"TEXT_MISSING_FACE":"TEXT_SEGMENT_UNQUALIFIED"
            });
        }
    }

    void ReportSymbol(TextFragment symbol,string id)
    {
        var decision=symbol.LegacySymbol!;
        if(symbol.Measure is null)
            Issue("TEXT_MISSING_FACE",id,$"{decision.Family} U+{decision.Code:X4}. {decision.Evidence}");
        else if(decision.Method=="documented")
            Issue("TEXT_LEGACY_SYMBOL_SUBSTITUTION",id,symbol.Face!.Reason);
    }

    string StrongScript(ReadOnlySpan<char> text)
    {
        foreach(var scalar in text.EnumerateRunes())
        {
            string script=Script(scalar.Value);
            if(script is not ("Zyyy" or "Zinh" or "Zzzz")) return script;
        }
        return "Zyyy";
    }

    static void ResolveNeutralScripts(List<Graph> graphs)
    {
        for(int start=0;start<graphs.Count;)
        {
            int end=start+1;
            while(end<graphs.Count && graphs[end].Level==graphs[start].Level && graphs[end].Slice.Token.Role!=TextRole.Control)end++;
            string? previous=null;
            for(int i=start;i<end;i++)
            {
                var graph=graphs[i];
                if(graph.Script!="Zyyy")previous=graph.Script;
                else if(previous is not null)graphs[i]=graph with {Script=previous};
            }
            string? next=null;
            for(int i=end-1;i>=start;i--)
            {
                var graph=graphs[i];
                if(graph.Script!="Zyyy")next=graph.Script;
                else if(next is not null)graphs[i]=graph with {Script=next};
            }
            start=end;
        }
    }

    SegmentKey Key(string context,Graph graph,TextStyle mark)
    {
        var style=graph.Slice.Token.Style??mark;
        bool east=EastAsianScripts.Contains(graph.Script);
        bool complex=style.Complex||style.RightToLeft||ComplexScripts.Contains(graph.Script);
        Rune scalar=Rune.GetRuneAt(context,graph.Start);
        string slot=complex?"cs":east?"eastAsia":scalar.IsAscii?"ascii":"hAnsi";
        FontRequest? request=slot switch {"cs"=>style.Slots.Cs,"eastAsia"=>style.Slots.EastAsia,"ascii"=>style.Slots.Ascii,_=>style.Slots.HAnsi};
        string language=(complex?style.BidiLanguage:east?style.EastAsianLanguage:null)??style.Language;
        ThemeFont? theme=request?.Theme==true?ResolveTheme(request.Value,graph.Script):null;
        string family=request is null?"DejaVu Serif":request.Theme?theme?.Family??"thème:"+request.Value:request.Value;
        bool canShape=graph.CanShape;
        if (family.Length is 0 or >128)
        {
            Issue("TEXT_FONT_REQUEST_INVALID",graph.Slice.Token.Source.NodeId,"Nom de face invalide. Requête conservée, mesure refusée.");
            canShape=false;
        }
        if(style.RightToLeft && (graph.Level&1)==0 && graph.Slice.Token.Source.NodeId is {} id && graph.CanShape)
            Issue("TEXT_WORD_RTL_PARTIAL",id,"Propriété rtl conservée. Réordonnancement Unicode effectué, règles de forçage Word non qualifiées.");
        return new(style,graph.Level,graph.Script,language,slot,family,request,theme,complex,canShape);
    }

    ThemeFont? ResolveTheme(string key,string script)
    {
        key=key.Replace("Ascii","HAnsi",StringComparison.Ordinal);
        if(key.EndsWith("Bidi",StringComparison.Ordinal)||key.EndsWith("EastAsia",StringComparison.Ordinal))
        {
            string prefix=key.StartsWith("major",StringComparison.Ordinal)?"major":key.StartsWith("minor",StringComparison.Ordinal)?"minor":"";
            string scriptKey=script switch {"Hani"=>"Hans","Hira" or "Kana"=>"Jpan","Hang"=>"Hang",_=>script};
            // Han variants require language-specific evidence. Use the script entry only for unambiguous scripts.
            if (script!="Hani" && prefix.Length>0 &&
                SemanticIndex.Unique(fonts.Theme.Where(t=>t.Key==prefix+":"+scriptKey&&t.Family.Length>0)) is {} supplemental)
                return supplemental;
        }
        return SemanticIndex.Unique(fonts.Theme.Where(t=>t.Key==key&&t.Family.Length>0));
    }

    FontResolution? Select(string text,SegmentKey key)
    {
        if(!key.CanShape)return null;
        long start=Stopwatch.GetTimestamp();
        try
        {
            bool missing=key.Request?.Theme==true&&key.Theme is null;
            var signature=(key.Family,key.Bold,key.Italic,missing,key.Language,text);
            if(text.Length<=24&&fontUses.TryGetValue(signature,out var cached))return cached;
            FontResolution? result;
            try { result=selection.Select(key.Family,key.Bold,key.Italic,text,missing,true,Ignorable,key.Language); }
            catch(InvalidDataException e) when(e.Message=="LAYOUT_GLYPH_MISSING") { result=null; }
            if(text.Length<=24&&fontUses.Count<4096)fontUses.TryAdd(signature,result);
            return result;
        }
        finally{ fontTicks+=Stopwatch.GetTimestamp()-start; }
    }

    ImmutableArray<TextBreak> LineBreaks(string context,List<Graph> graphs,List<SegmentKey> keys,List<TextFragment> fragments)
    {
        if(graphs.Count==0)return [];
        string[] locales=keys.Where(k=>k.CanShape).Select(k=>k.Language).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if(locales.Length>1)
            Issue("TEXT_MIXED_LINE_LOCALE",graphs[0].Slice.Token.Source.NodeId,"Langues mixtes : règles de coupure Unicode en locale racine.");
        string locale=locales.Length==1?locales[0]:"und";
        var permitted=new SortedSet<int>(graphs.Select(g=>g.End));
        var controls=graphs.Where(g=>g.Slice.Token.Role==TextRole.Control).Select(g=>g.End).ToHashSet();
        foreach(var fragment in fragments)
        {
            Check();
            var clusters=fragment.Measure?.Clusters.Select(c=>c.Range.EndExclusive).ToHashSet();
            foreach(var offset in permitted.GetViewBetween(fragment.ContextOffset+1,fragment.ContextOffset+fragment.Text.Length).ToArray())
                if(clusters?.Contains(offset-fragment.ContextOffset)!=true) permitted.Remove(offset);
            if(fragment.Measure is {} measure)
                foreach(var c in measure.Clusters.Where(c=>c.UnsafeBefore))
                    permitted.Remove(fragment.ContextOffset+c.Range.Start);
        }
        return [..unicode.Boundaries(context,false,locale,Check)
            .Where(b=>permitted.Contains(b.Offset) || b.Mandatory && controls.Contains(b.Offset))
            .Select(b=>new TextBreak(b.Offset,b.Mandatory))];
    }

    static int FindFragment(List<TextFragment> fragments,int offset)
    {
        int low=0,high=fragments.Count-1;
        while(low<=high)
        {
            int mid=(low+high)/2;
            var f=fragments[mid];
            if(offset<=f.ContextOffset)high=mid-1;
            else if(offset>f.ContextOffset+f.Text.Length)low=mid+1;
            else return mid;
        }
        return -1;
    }

    static ImmutableArray<int> VisualOrder(List<TextFragment> fragments)
    {
        if(fragments.Count==0)return [];
        var order=Enumerable.Range(0,fragments.Count).ToArray();
        int last=fragments.Max(f=>(int)f.BidiLevel);
        for(int level=last;level>=1;level--)
        {
            int start=0;
            while(start<order.Length)
            {
                if(fragments[order[start]].BidiLevel<level){start++;continue;}
                int end=start+1;
                while(end<order.Length&&fragments[order[end]].BidiLevel>=level)end++;
                Array.Reverse(order,start,end-start);
                start=end;
            }
        }
        return [..order];
    }
}
