using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class LineCases
{
    internal const int Pt=TypographySession.EmuPerPoint;
    internal static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        const string rPr="""
            <w:rPr><w:rFonts w:ascii="DejaVu Serif" w:hAnsi="DejaVu Serif"/><w:sz w:val="24"/>
            <w:lang w:val="fr-FR"/><w:kern w:val="2"/>
            <w14:ligatures xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" w14:val="standard"/></w:rPr>
            """;
        const string tab="<w:r><w:tab/></w:r>";
        string R(string s)=>"<w:r>"+rPr+"<w:t xml:space=\"preserve\">"+s+"</w:t></w:r>";
        string P(string s,string pp="")=>"<w:p>"+(pp.Length==0?"":"<w:pPr>"+pp+"</w:pPr>")+s+"</w:p>";
        string accent="e\u0301",math=char.ConvertFromUtf32(0x1d434),pua=char.ConvertFromUtf32(0x10fffd);
        string[] paragraphs=[
            P(R("AV office ")+"<w:r><w:t>e</w:t></w:r><w:r><w:t>\u0301</w:t></w:r>"+
                R(" français "+math+" one\u00a0two one two three end"),
                """<w:jc w:val="both"/><w:ind w:left="80" w:right="120" w:firstLine="200"/><w:keepNext/><w:keepLines/><w:widowControl w:val="0"/>"""),
            P(R("ab")+tab+R("g")+tab+R("right")+tab+R("centre")+tab+R("12,3"),
                """<w:pStyle w:val="Derived"/><w:tabs><w:tab w:val="clear" w:pos="200"/></w:tabs>"""),
            P(R("x")+tab+R("a b c d e f g h i j k l m n o p q")),
            P(R("titre")+tab+"<w:fldSimple w:instr=\"PAGE\">"+R("12")+"</w:fldSimple>",
                """<w:tabs><w:tab w:val="right" w:pos="2800" w:leader="dot"/></w:tabs>"""),
            P(R("alpha")+"<w:r>"+rPr+"<w:softHyphen/></w:r>"+R("bêta")),
            P(R("avant")+"<w:r>"+rPr+"<w:noBreakHyphen/><w:t>après</w:t><w:br w:type=\"page\"/><w:t>suite</w:t><w:br/></w:r>"),
            P(R("שלום abc אבג 12 سلام")+"<w:r><w:br/></w:r>"+R("مرحبا 7 abc    "),"<w:bidi/>"),
            P(R("right"),"""<w:jc w:val="right"/><w:ind w:left="400" w:hanging="200"/>"""),
            P(R("center"),"""<w:jc w:val="center"/>"""),
            P(R("a "+pua+" fin")),
            P(R("Motindivisible")),
            P(R("simple")),
            P(R("left")+tab+R("text"),"<w:bidi/>"),
            P(R("alpha\u00adbêta")),
            P(R("alpha\u00adbêta")),
            P(R("abc\u2029سلام")),
            P("""<w:r><w:rPr><w:spacing w:val="3"/></w:rPr><w:t>ABC</w:t></w:r>"""),
            P(R("a b")+tab+R("a b c d e f g h i j k l m n o p q"),
                """<w:jc w:val="both"/><w:tabs><w:tab w:val="left" w:pos="900" w:leader="dot"/></w:tabs>""")
        ];
        var parts=new Dictionary<string,string>
        {
            ["word/styles.xml"]=Fixtures.Xml("styles","""
                <w:style w:type="paragraph" w:styleId="Base"><w:pPr><w:tabs>
                <w:tab w:val="left" w:pos="200"/><w:tab w:val="left" w:pos="900"/><w:tab w:val="right" w:pos="2100"/>
                <w:tab w:val="center" w:pos="3300"/><w:tab w:val="decimal" w:pos="4400"/>
                </w:tabs></w:pPr></w:style>
                <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/></w:style>
                <w:style w:type="table" w:styleId="Grid"><w:pPr><w:ind w:left="120"/></w:pPr></w:style>
                """),
            ["word/settings.xml"]=Fixtures.Xml("settings","""<w:defaultTabStop w:val="480"/>""")
        };
        string table="<w:tbl><w:tblPr><w:tblStyle w:val=\"Grid\"/></w:tblPr><w:tr><w:tc>"+P(R("Cellule"))+"</w:tc></w:tr></w:tbl>";
        string relations=Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("settings","settings","settings.xml");
        string path=Fixtures.Write(root,"line-contract.docx",Fixtures.Parts(string.Concat(paragraphs)+table,parts,relations));
        var inspected=DocumentInspector.InspectDocument(path);
        var model=TypographyCases.Model(inspected);
        using var typography=new TypographySession(inspected,model);
        var doc=typography.Measure();
        var paragraphIndex=doc.Paragraphs.Select((p,i)=>(p.Id,i)).ToDictionary(v=>v.Id,v=>v.i);
        LineRegion Region(LineRequest request)=>new("synthetic",14*Pt,
            (paragraphIndex[request.ParagraphId] switch { 0=>105,1=>270,2=>66,4 or 13=>39,6=>110,10=>10,17=>100,_=>160 })*Pt);
        using var composer=new LineComposer(model,doc,typography);
        var composed=composer.Compose(Region);
        Verify(model,composed);
        var byParagraph=composed.Lines.GroupBy(l=>paragraphIndex[l.ParagraphId]).ToDictionary(g=>g.Key,g=>g.ToArray());
        var justification=byParagraph[0];
        Check(justification.Length>=3&&justification[0].Occupied==justification[0].ContentRight-justification[0].ContentLeft&&
            justification[0].Visual.Any(v=>v.ExtraAfter>0)&&justification[^1].Visual.All(v=>v.ExtraAfter==0),"Justification de lignes intermédiaires. "+string.Join("; ",justification.Select(l=>$"{l.Cut}/{l.Properties.Alignment}/{l.Occupied}/{l.ContentRight-l.ContentLeft}/{l.Visual.Count(x=>x.ExtraAfter>0)}/{string.Join('.',l.Diagnostics.Select(d=>d.Code))}")));
        Check(justification[0].Properties is {First:127000,Left:50800,Right:76200,KeepNext:true,KeepLines:true,WidowControl:false}&&
            justification[0].ContentLeft-justification[1].ContentLeft==127000,"Retrait de première ligne ou propriétés de continuation.");
        Check(justification.SelectMany(l=>l.Parts).Any(p=>p.Fragment is {} f&&f.Text.Contains(accent)&&f.Sources.Length>1),"Accent inter-nœuds perdu.");
        var tabs=byParagraph[1].Single();
        Check(tabs.Properties.Tabs.Length==4&&tabs.Tabs.Select(t=>t.Stop.Kind).SequenceEqual(
            new[]{LineTabKind.Left,LineTabKind.Right,LineTabKind.Center,LineTabKind.Decimal})&&
            tabs.Tabs.All(t=>t.Stop.Source?.Location.PartUri=="/word/styles.xml")&&tabs.Properties.DefaultTab==24*Pt,
            "Cascade, effacement des taquets, ou pas documentaire.");
        Check(FieldAnchor(tabs,0)==tabs.Region.Left+900*635&&FieldAnchor(tabs,1)==tabs.Region.Left+2100*635&&
            Math.Abs(FieldAnchor(tabs,2)-(tabs.Region.Left+3300*635))<=1&&FieldAnchor(tabs,3)==tabs.Region.Left+4400*635,
            "Ancre des champs de tabulation.");
        Check(byParagraph[2].Length>1&&byParagraph[2].SelectMany(l=>l.Tabs).Single().Stop.Source?.Location.PartUri=="/word/settings.xml",
            "Retour automatique et taquet documentaire.");
        var tabJustified=byParagraph[17][0];
        var justifyStop=tabJustified.Tabs.Single();
        Check(justifyStop.To==tabJustified.Region.Left+45*Pt&&
            tabJustified.Occupied==tabJustified.ContentRight-tabJustified.ContentLeft&&
            tabJustified.Visual.Where(p=>p.ExtraAfter>0).Any()&&
            tabJustified.Visual.Where(p=>p.ExtraAfter>0).All(p=>p.X>=justifyStop.To)&&
            tabJustified.Generated.Any(d=>d.Kind=="leader")&&byParagraph[17][^1].Visual.All(p=>p.ExtraAfter==0),
            "La justification atteint la marge après le taquet fixe et préserve les points de suite.");
        var toc=byParagraph[3].Single();
        Check(toc.Generated.Any(d=>d.Kind=="leader"&&d.Count>3)&&toc.Parts.SelectMany(p=>p.Fragment?.Sources??[]).Any(s=>s.Role==TextRole.StoredField),
            "Points de suite ou champ stocké.");
        Check(byParagraph[4][0].Cut==LineCutKind.SoftHyphen&&byParagraph[4][0].Generated.Single().Kind=="softHyphen"&&
            byParagraph[4][1].Generated.Length==0,"Trait conditionnel dessiné uniquement à la coupure.");
        Check(byParagraph[5].Select(l=>l.Cut).SequenceEqual(new[]{LineCutKind.Page,LineCutKind.Line,LineCutKind.End})&&
            byParagraph[5][0].Generated.Any(d=>d.Kind=="noBreakHyphen"),"Sauts et dernière ligne vide.");
        Check(byParagraph[6].Length>1&&byParagraph[6].Any(l=>l.Parts.Any(p=>p.Fragment?.BidiLevel%2==0)&&
            l.Parts.Any(p=>p.Fragment?.BidiLevel%2==1)&&!l.Visual.Select(v=>v.Part).SequenceEqual(l.Visual.Select(v=>v.Part).Order())),
            "Bidi de ligne et ordre visuel.");
        Check(byParagraph[6][^1].Parts.Last(p=>p.Text is not null).Fragment!.BidiLevel%2==1,
            "Les espaces terminaux doivent retrouver le niveau de base RTL.");
        var right=byParagraph[7].Single();
        var center=byParagraph[8].Single();
        Check(right.Properties.First==-127000&&right.Visual[0].X+right.Occupied==right.ContentRight&&
            center.Visual[0].X==center.ContentLeft+(center.ContentRight-center.ContentLeft-center.Occupied)/2,"Alignement ou retrait suspendu.");
        Check(byParagraph[9].Single() is {Coverage:LineCoverage.Partial,Occupied:null} missing&&
            missing.Parts.Any(p=>p.Text==pua&&p.Limitation=="TEXT_MISSING_FACE")&&missing.Diagnostics.Any(d=>d.Code=="TEXT_MISSING_FACE"),
            "Texte privé sans géométrie certifiée.");
        Check(byParagraph[10].Single().Diagnostics.Any(d=>d.Code=="LINE_OVERFLOW"),"Débordement indivisible silencieux.");
        Check(byParagraph[12].Single().Diagnostics.Any(d=>d.Code=="LINE_BIDI_TABS"),"Taquet RTL hors profil sans diagnostic.");
        Check(byParagraph[13][0].Cut==LineCutKind.SoftHyphen&&byParagraph[13][0].Parts.Last().Text!.EndsWith('\u00ad')&&
            byParagraph[13][0].Generated.Single().Cause.Span is {Start:5,EndExclusive:6}&&
            byParagraph[14].Single().Generated.IsEmpty,"Trait facultatif Unicode et source exacte.");
        Check(byParagraph[15].Select(l=>l.Cut).SequenceEqual(new[]{LineCutKind.Mandatory,LineCutKind.End}),
            "Séparateur Unicode obligatoire.");
        var tracked=byParagraph[16].Single();
        Check(tracked is {Coverage:LineCoverage.Complete,Occupied:not null}&&tracked.Parts.Single().Fragment?.Style.Tracking==.15f,
            "Espacement source sur des clusters latins simples.");
        Check(byParagraph[18].Single().Properties.Origins["left"].Location.PartUri=="/word/styles.xml"&&
            byParagraph[18].Single().Properties.Left==76200,"Héritage de paragraphe depuis le style de tableau.");
        ComparePaint(root,composer,typography,byParagraph[11].Single());
        var warm=composer.Compose(Region);
        Check(composed.Performance.Reshaped>0&&warm.Performance.ShapeHits>0,"Recalcul de bord et cache.");
        Check(Fingerprint(composed)==Fingerprint(warm),"Cache de composition modifie les lignes.");
        using var cancelled=new CancellationTokenSource();
        Expect<OperationCanceledException>(()=>composer.Compose(r=>{cancelled.Cancel();return Region(r);},cancelled.Token));
        Check(Fingerprint(composer.Compose(Region))==Fingerprint(composed),"Session non réutilisable après annulation.");
        Expect<InvalidOperationException>(()=>composer.Compose(r=>{composer.Compose(Region);return Region(r);}),"LINE_REENTRANT");
        foreach(var limit in new[]{new LineBudget(MaxLines:1),new LineBudget(MaxWork:1),new LineBudget(MaxGlyphs:1)})
        {
            using var limited=new LineComposer(model,doc,typography,limit);
            Expect<InvalidDataException>(()=>limited.Compose(Region),
                limit.MaxLines==1?"LINE_COUNT_LIMIT":limit.MaxWork==1?"LINE_WORK_LIMIT":"LINE_GLYPH_LIMIT");
        }
        using var other=new TypographySession(inspected,model);
        Expect<InvalidDataException>(()=>new LineComposer(model,doc,other));
    }

    static long FieldAnchor(ComposedLine line,int index)
    {
        int part=line.Parts.IndexOf(line.Parts.Single(p=>p.Control?.Source==line.Tabs[index].Source));
        var slots=line.Visual.Where(v=>v.Part>part&&(v.Cluster>=0||
            line.Parts[v.Part].Control?.Kind=="Tab")).TakeWhile(v=>v.Cluster>=0).ToArray();
        return index switch
        {
            0=>slots[0].X,
            1=>slots[^1].X+slots[^1].AdvanceX,
            2=>(slots[0].X+slots[^1].X+slots[^1].AdvanceX)/2,
            _=>slots.Single(v=>line.Parts[v.Part].Text is {} text&&
                text[line.Parts[v.Part].Fragment!.Measure!.Clusters[v.Cluster].Range.Start]==',').X
        };
    }

    static void ComparePaint(string root,LineComposer composer,TypographySession typography,ComposedLine line)
    {
        var f=line.Parts.Single().Fragment!;
        using var a=SKSurface.Create(new SKImageInfo(420,150));
        using var b=SKSurface.Create(new SKImageInfo(420,150));
        foreach(var canvas in new[]{a.Canvas,b.Canvas}){canvas.Clear(SKColors.White);canvas.Scale(2);}
        composer.Paint(a.Canvas,line,2,40,SKColors.Black);
        typography.Paint(b.Canvas,f,2+(float)line.ContentLeft/Pt,40,SKColors.Black);
        using var ai=a.Snapshot();using var bi=b.Snapshot();
        using var ap=ai.Encode(SKEncodedImageFormat.Png,100);using var bp=bi.Encode(SKEncodedImageFormat.Png,100);
        Check(ap.AsSpan().SequenceEqual(bp.AsSpan()),"Le dessin des positions mesurées diffère du fragment HarfBuzz/Skia.");
        File.WriteAllBytes(Path.Combine(root,"line-paint.png"),ap.ToArray());
    }

    internal static void Verify(SemanticDocument model,ComposedLines composed)
    {
        var groups=composed.Lines.GroupBy(l=>l.ParagraphId).ToDictionary(g=>g.Key,g=>g.ToArray());
        Check(groups.Count==composed.Typography.Paragraphs.Length,"Paragraphe non représenté.");
        foreach(var paragraph in composed.Typography.Paragraphs)
        {
            var lines=groups[paragraph.Id];
            string startText=string.Concat(paragraph.Fragments.Select(f=>f.Text));
            string endText=string.Concat(lines.SelectMany(l=>l.Parts.Select(p=>p.Text)));
            Check(startText==endText,"Reconstruction UTF-16 du texte source.");
            Check(lines.SelectMany(l=>l.Parts).Where(p=>p.Control is not null).Select(p=>p.Control!.Value)
                .SequenceEqual(paragraph.Controls),"Provenance des contrôles.");
            int next=0,ordinal=0;
            var graphs=paragraph.Fragments.SelectMany(f=>f.GraphemeStarts.Select(g=>g+f.ContextOffset))
                .Concat(paragraph.Controls.Select(c=>c.Offset)).Append(paragraph.ContextLength).ToHashSet();
            var invalid=paragraph.Fragments.SelectMany(f=>f.Measure?.Clusters.Where(c=>c.UnsafeBefore)
                .Select(c=>f.ContextOffset+c.Range.Start)??[]).ToHashSet();
            var cuts=paragraph.Breaks.Select(b=>b.Offset).Concat(paragraph.Controls.Where(c=>c.Kind is "Break" or "SoftHyphen")
                .Select(c=>c.Offset+1)).ToHashSet();
            foreach(var line in lines)
            {
                Check(line.Index==ordinal++&&line.StoryId==paragraph.StoryId&&line.ContextRange.Start==next&&
                    line.ParagraphSource==model.Source(paragraph.Id),"Identité de ligne ou progression.");
                next=line.ContextRange.EndExclusive;
                Check(graphs.Contains(next)&&(next==paragraph.ContextLength||!invalid.Contains(next)),"Coupure à l’intérieur d’un cluster.");
                Check(next==paragraph.ContextLength||cuts.Contains(next),"Coupure absente des opportunités typographiques.");
                int partNext=line.ContextRange.Start;
                foreach(var part in line.Parts)
                {
                    Check(part.ContextRange.Start==partNext,"Rupture de provenance à l’intérieur d’une ligne.");
                    partNext=part.ContextRange.EndExclusive;
                    if(part.Fragment is not {} f)continue;
                    int textNext=0;
                    foreach(var s in f.Sources)
                    {
                        Check(s.Source.Span is {}&&s.FragmentRange.Start==textNext,"Plage de source non contiguë.");
                        var span=s.Source.Span!.Value;
                        Check(model.Source(s.Source.NodeId,span)==s.Source&&
                            model.RawText(s.Source.NodeId)![span.Start..span.EndExclusive]==f.Text[s.FragmentRange.Start..s.FragmentRange.EndExclusive],
                            "Identité UTF-16 OOXML non exacte.");
                        textNext=s.FragmentRange.EndExclusive;
                    }
                    Check(textNext==f.Text.Length,"Source d’une partie de texte manquante.");
                }
                Check(partNext==next,"Fin de plage non représentée.");
                if(line.Occupied is null)
                {
                    Check(line.Coverage==LineCoverage.Partial&&line.Diagnostics.Length>0&&line.Visual.IsEmpty&&line.Generated.IsEmpty,
                        "Une géométrie inconnue est publiée sans diagnostic.");
                    continue;
                }
                Check(line.Height>0&&line.Baseline==line.Ascent&&line.Advance>=line.Occupied,"Métriques de ligne.");
                Check(line.Occupied<=line.ContentRight-line.ContentLeft||line.Diagnostics.Any(d=>d.Code=="LINE_OVERFLOW"),
                    "Débordement non signalé.");
                foreach(var v in line.Visual.Where(v=>v.Cluster>=0))
                {
                    var m=line.Parts[v.Part].Fragment!.Measure!;
                    var c=m.Clusters[v.Cluster];
                    var gs=m.Glyphs.Skip(c.FirstGlyph).Take(c.GlyphCount);
                    Check(gs.Sum(g=>g.AdvanceX)==v.AdvanceX&&gs.Sum(g=>g.AdvanceY)==v.AdvanceY&&
                        v.GlyphOriginX+m.Glyphs.Take(c.GlyphCount==0?0:c.FirstGlyph).Sum(g=>g.AdvanceX)==v.X,
                        "Les positions emploient une autre avance que le dessin.");
                }
            }
            Check(next==paragraph.ContextLength,"Fin de paragraphe non représentée.");
        }
    }

    internal static int Fingerprint(ComposedLines value)
    {
        var hash=new HashCode();
        foreach(var l in value.Lines)
        {
            hash.Add(l.ParagraphId);hash.Add(l.ContextRange);hash.Add(l.Cut);hash.Add(l.Occupied);hash.Add(l.Coverage);hash.Add(l.Height);
            foreach(var p in l.Parts)
            {
                hash.Add(p.Text);hash.Add(p.Control);
                if(p.Fragment?.Measure is {} shape)
                    foreach(var g in shape.Glyphs)hash.Add(g);
            }
            foreach(var v in l.Visual)hash.Add(v);
            foreach(var t in l.Tabs)hash.Add(t);
            foreach(var d in l.Generated){hash.Add(d.X);hash.Add(d.Count);hash.Add(d.Step);hash.Add(d.Cause);}
        }
        return hash.ToHashCode();
    }

    static void Expect<T>(Action action,string? message=null) where T:Exception
    {
        try{action();}
        catch(T ex) when(message is null||ex.Message==message){return;}
        throw new InvalidOperationException("Refus ciblé attendu "+typeof(T).Name+" "+message);
    }
}
