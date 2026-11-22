using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    static string ListProperties(int instance,int level) =>
        $"""<w:numPr><w:ilvl w:val="{level}"/><w:numId w:val="{instance}"/></w:numPr>""";

    internal static void Numbering(string root)
    {
        static string Level(int level,string format,string label,int start,string suffix,string align,string extra="") => $"""
            <w:lvl w:ilvl="{level}"><w:start w:val="{start}"/><w:numFmt w:val="{format}"/>{extra}
              <w:suff w:val="{suffix}"/><w:lvlText w:val="{label}"/><w:lvlJc w:val="{align}"/>
              <w:pPr><w:tabs><w:tab w:val="num" w:pos="900"/></w:tabs>
                <w:ind w:left="900" w:hanging="500"/>
              </w:pPr>
              <w:rPr><w:rFonts w:ascii="DejaVu Serif" w:hAnsi="DejaVu Serif"/><w:color w:val="A01010"/><w:sz w:val="32"/></w:rPr>
            </w:lvl>
            """;
        var levels=Level(0,"decimal","%1.",4,"tab","left")+
            Level(1,"lowerLetter","%1.%2)",26,"space","center","""<w:pStyle w:val="ListBase"/><w:lvlRestart w:val="1"/>""")+
            Level(2,"lowerRoman","%3",1,"nothing","right","""<w:lvlRestart w:val="0"/>""")+
            Level(3,"bullet","•",1,"nothing","left");
        var number=Fixtures.Xml("numbering",$"""
            <w:abstractNum w:abstractNumId="3"><w:multiLevelType w:val="multilevel"/>{levels}</w:abstractNum>
            <w:num w:numId="7"><w:abstractNumId w:val="3"/></w:num>
            <w:num w:numId="8"><w:abstractNumId w:val="3"/>
              <w:lvlOverride w:ilvl="0"><w:startOverride w:val="10"/>{Level(0,"upperRoman","%1:",99,"nothing","right")}</w:lvlOverride>
            </w:num>
            """);
        var style=Fixtures.Xml("styles","""
            <w:style w:type="paragraph" w:styleId="ListBase"><w:pPr>
              <w:numPr><w:numId w:val="7"/></w:numPr></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="ListDerived"><w:basedOn w:val="ListBase"/></w:style>
            """);
        const string inherited="""<w:pStyle w:val="ListDerived"/>""";
        const string directTab="""<w:tabs><w:tab w:val="clear" w:pos="900"/><w:tab w:val="left" w:pos="1040"/></w:tabs>""";
        const string text="Café 😀 alphabet mot suivant plusieurs mots pour au moins deux lignes";
        string body=P(text,ListProperties(7,0)+directTab)+P("Hérité",inherited)+P("Lettre suivante",ListProperties(7,1))+
            P("Romain",ListProperties(7,2))+P("Supprimé",inherited+ListProperties(0,0))+
            P("Autre instance",ListProperties(8,0))+P("Reprise",ListProperties(7,0)+"<w:keepNext/>")+
            P("Après reprise",inherited)+P("Romain continué",ListProperties(7,2))+P("Puce",ListProperties(7,3))+
            P("Double A",ListProperties(7,1))+P("Double B",ListProperties(7,1))+Section();
        var path=Fixture("page-numbering",body,number);
        using var lease=Compose(path);
        var s=lease.Snapshot;
        Complete(s);
        var lines=s.Lines!.Lines;
        var markers=lines.Where(l=>l.ListMarker is not null).ToArray();
        Check(markers.Select(l=>l.ListMarker!.Label.Text).SequenceEqual(new[]{"4.","4.z)","4.aa)","i","X:","5.","5.z)","ii","•","5.aa)","5.bb)"}),
            "Compteurs, formats, héritage, surcharge, suppression ou redémarrage.");
        Check(markers.Length==lines.Sum(l=>l.Generated.Count(d=>d.Kind=="listMarker"))&&
            markers.All(l=>l.Index==0&&l.Generated.Single(d=>d.Kind=="listMarker").Fragment.Sources.All(x=>x.Source.Span is null)&&
            l.ListMarker!.Label.Numbering is {InstanceId:not null,AbstractId:not null,LevelId:not null,InstanceSourceId:not null}),
            "Provenance et unicité des marques générées.");
        Check(markers[1].ListMarker!.Label.Numbering!.LevelSourceId is {} associated&&
            s.Semantic!.Attribute(associated,"val")=="ListBase"&&markers[1].ListMarker!.Label.ReferencedLevels.Count==2&&
            markers[4].ListMarker!.Label.Numbering!.OverrideId is not null,"Provenance pStyle, placeholders et surcharge.");
        var first=markers[0];
        var firstMarker=first.ListMarker!;
        var firstWrapped=lines.Where(l=>l.ParagraphId==first.ParagraphId).ToArray();
        Check(firstMarker.X==400*635&&firstMarker.TextStart==1040*635&&firstMarker.Tab?.Source is not null&&
            firstWrapped.Length>1&&firstWrapped.Skip(1).All(l=>l.ContentLeft==900*635)&&
            first.Visual[0].X==firstMarker.TextStart&&string.Concat(firstWrapped.SelectMany(l=>l.Parts).Select(p=>p.Text))==text,
            $"Taquet direct, retrait suspendu, premier glyphe ou reconstruction ({firstMarker.X}, {firstMarker.TextStart}, {first.Visual[0].X}).");
        var center=markers[1].ListMarker!;
        var right=markers[3].ListMarker!;
        var centerWidth=center.Decoration!.Fragment.Measure!.Metrics.Width;
        var rightWidth=right.Decoration!.Fragment.Measure!.Metrics.Width;
        Check(center.X==400*635-centerWidth/2&&center.TextStart>center.X+centerWidth&&
            right.X+rightWidth==400*635&&right.TextStart==400*635,"Alignement ou avance du suffixe.");
        Check(first.Ascent>=firstMarker.Decoration!.Fragment.Measure!.Metrics.Ascent,"Métriques de marque.");
        Check(s.Composition.Pages.Any(p=>p.Lines.Any(l=>l.Text=="Reprise")&&p.Lines.Any(l=>l.Text=="Après reprise")),
            "Deux entrées de liste solidaires ont été séparées.");
        VerifyPlaced(s);
        using var again=Compose(path);
        var png=Composer.PagePng(s.Composition.Pages[0],1.5f);
        Check(png.AsSpan().SequenceEqual(Composer.PagePng(again.Snapshot.Composition.Pages[0],1.5f)),"Raster numéroté instable.");
        using var bitmap=SKBitmap.Decode(png);
        var placed=s.Composition.Pages[0].Lines[0];
        var markerInk=firstMarker.Decoration.Fragment.Measure.Metrics.Ink!.Value;
        int x0=(int)Math.Floor(1.5f*(placed.NativeOffsetX+PageComposer.Points(firstMarker.X!.Value+markerInk.Left)));
        int x1=(int)Math.Ceiling(1.5f*(placed.NativeOffsetX+PageComposer.Points(firstMarker.X.Value+markerInk.Right)));
        int y0=(int)Math.Floor(1.5f*(placed.Baseline+PageComposer.Points(markerInk.Top)));
        int y1=(int)Math.Ceiling(1.5f*(placed.Baseline+PageComposer.Points(markerInk.Bottom)));
        Check(Enumerable.Range(y0,y1-y0).Any(y=>Enumerable.Range(x0,x1-x0).Any(x=>
            bitmap.GetPixel(x,y) is {Red:>100,Green:<95,Blue:<95})),"Le glyphe coloré n’est pas dessiné à la boîte mesurée.");
        File.WriteAllBytes(Path.Combine(root,"listes-natives.png"),png);

        using var field=Compose(Fixture("page-list-field",P("Item",ListProperties(7,0))+
            Paragraph("""<w:fldSimple w:instr="PAGEREF Signet"><w:r><w:t>42</w:t></w:r></w:fldSimple>""")+Section(),number));
        Check(field.Snapshot.ListMarkers.Count==1&&field.Snapshot.Lines!.Lines.Single(l=>l.Parts.Any(p=>p.Text=="42")).ListMarker is null&&
            field.Snapshot.Typography!.Tokens.Any(t=>t.Role==TextRole.StoredField&&t.Original=="42"),"Champ PAGEREF distinct de la liste.");

        string cell=Paragraph(Run("I")+"<w:r><w:br/></w:r>"+Run("champignons"),ListProperties(7,0)+directTab);
        string table= """
            <w:tbl><w:tblPr><w:tblLayout w:type="autofit"/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid>
            """+"<w:tr><w:tc>"+cell+"</w:tc></w:tr></w:tbl>";
        using var fitted=Compose(Fixture("page-numbered-autofit",table+Section(width:4000),number));
        Complete(fitted.Snapshot,1);
        var cellLines=fitted.Snapshot.Composition.Pages[0].Lines;
        Check(fitted.Snapshot.RulerModel!.Tables.Single().WidthResult is {ContentVerified:true}&&
            cellLines.Count==2&&cellLines[0].Native?.ListMarker?.TextStart==1040*635&&
            cellLines[1].Native?.ContentLeft==900*635&&cellLines[0].CellId==cellLines[1].CellId,
            "AutoFit doit transmettre la largeur réelle et le retrait distinct des deux lignes.");

        string privateBullets=number.Replace("•","",StringComparison.Ordinal)
            .Replace("DejaVu Serif","Lecteur Word témoin face absente",StringComparison.Ordinal);
        using var missing=Compose(Fixture("page-private-list",P("Avant")+P("Source",ListProperties(7,3))+Section(),privateBullets));
        var unresolved=missing.Snapshot.ListMarkers.Values.Single();
        Check(unresolved.Label.Text==""&&unresolved.Decoration is null&&unresolved.TextStart is null&&
            missing.Snapshot.Coverage==PageCoverage.Partial&&missing.Snapshot.Gaps.Any(g=>g.Reason=="LINE_LIST_GLYPH_MISSING"),
            "Le caractère privé doit rester exact et le défaut doit bloquer le bloc concerné.");
        using var bad=Compose(Fixture("page-ambiguous-list",P("Avant")+P("Ambigu",ListProperties(7,0))+Section(),
            number.Replace(levels,levels+Level(0,"decimal","%1",1,"tab","left"),StringComparison.Ordinal)));
        Check(bad.Snapshot.Coverage==PageCoverage.Partial&&bad.Snapshot.Gaps.Any(g=>g.Reason=="LINE_NUMBERING_UNRESOLVED"),
            "Un niveau ambigu a été présenté comme résolu.");
        var inspection=DocumentInspector.InspectDocument(path);
        var model=TypographyCases.Model(inspection);
        using var typography=new TypographySession(inspection,model);
        var typographyDoc=typography.Measure();
        var layout=LayoutReader.Read(inspection,model,default,new TableStyleCascade(model,default));
        var paragraphs=typographyDoc.Paragraphs.ToDictionary(p=>p.Id);
        using var bounded=new LineComposer(model,typographyDoc,typography,new LineBudget(MaxGlyphs:1));
        bool limited=false;
        try{bounded.MeasureListMarkers(layout.Paragraphs,paragraphs,default);}
        catch(InvalidDataException error) when(error.Message=="LINE_GLYPH_LIMIT"){limited=true;}
        Check(limited,"Le shaping des listes doit consommer le budget de glyphes.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool interrupted=false;
        try{bounded.MeasureListMarkers(layout.Paragraphs,paragraphs,cancelled.Token);}
        catch(OperationCanceledException){interrupted=true;}
        Check(interrupted,"L’annulation doit interrompre la mesure des listes.");

        string Fixture(string name,string content,string definitions)=>Fixtures.Write(root,name+".docx",Fixtures.Parts(content,
            new(){["word/numbering.xml"]=definitions,["word/styles.xml"]=style,
                ["word/settings.xml"]=Fixtures.Xml("settings","<w:compat><w:noTabHangIndent/></w:compat>")},
            Fixtures.Relationship("num","numbering","numbering.xml")+Fixtures.Relationship("styles","styles","styles.xml")+
            Fixtures.Relationship("settings","settings","settings.xml")));
    }

    internal static void KeepNext(string root)
    {
        const string exact="""<w:spacing w:lineRule="exact" w:line="400"/>""";
        const string keep="<w:keepNext/>";
        const string pageBreak="""<w:r><w:br w:type="page"/></w:r>""";
        string rows=string.Join("<w:r><w:br/></w:r>",Enumerable.Range(1,6).Select(i=>Run("Rang "+i)));
        string table="""
            <w:tbl><w:tblPr><w:tblW w:type="dxa" w:w="2000"/><w:tblLayout w:type="fixed"/></w:tblPr>
              <w:tblGrid><w:gridCol w:w="2000"/></w:tblGrid>
            """+"<w:tr><w:tc>"+P("Cellule A",exact+keep)+P("Cellule B",exact)+"</w:tc></w:tr></w:tbl>";
        string body=P("Avant",exact)+P("Début chaîne",exact+keep)+Paragraph(rows,exact+keep)+
            P("Titre tableau",exact+keep+"<w:keepLines/>")+table+Section(height:2880,margins:440);
        using var lease=Compose(Write(root,"page-keep-oversize",body));
        var s=lease.Snapshot;
        Complete(s,3);
        var pages=s.Composition.Pages;
        Check(pages[0].Lines.Single().Text=="Avant"&&pages[1].Lines.First().Text=="Début chaîne"&&
            pages[2].Lines.Select(l=>l.Text).SequenceEqual(new[]{"Rang 5","Rang 6","Titre tableau","Cellule A","Cellule B"})&&
            pages[2].Cells.Count==1&&s.Composition.Issues.Count(i=>i.Code=="PAGE_KEEP_NEXT_SPANS_PAGES"&&!i.BlocksRendering)==1,
            "Départ de la chaîne, veuves, hauteur de cellule et solidarité avec le tableau.");
        VerifyPlaced(s);

        const string seventy="""<w:spacing w:lineRule="exact" w:line="1400"/>""";
        using var impossibleLink=Compose(Write(root,"page-keep-unjoinable",
            P("Bloc A",seventy+keep+"<w:keepLines/>")+P("Bloc B",seventy+"<w:keepLines/>")+Section(height:2880,margins:440)));
        Complete(impossibleLink.Snapshot,2);
        Check(impossibleLink.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_KEEP_NEXT_LINK_EXCEEDS_PAGE"),
            "Le lien physiquement impossible doit être diagnostiqué.");

        const string styles="""
            <w:style w:type="paragraph" w:styleId="Ensemble"><w:pPr><w:keepNext/></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="Derive"><w:basedOn w:val="Ensemble"/></w:style>
            """;
        const string inherited="""<w:pStyle w:val="Derive"/>""";
        string explicitBody=P("Précédent",exact+keep)+Paragraph(Run("Avant rupture")+pageBreak+Run("Après rupture"),inherited+exact)+
            P("Fin attachée",exact)+P("Avant règle",exact+keep)+P("Nouveau",exact+"<w:pageBreakBefore/>")+Section(height:2880,margins:440);
        string explicitPath=Fixtures.Write(root,"page-keep-breaks.docx",Fixtures.Parts(explicitBody,
            new(){["word/styles.xml"]=Fixtures.Xml("styles",styles)},Fixtures.Relationship("styles","styles","styles.xml")));
        using var breaks=Compose(explicitPath);
        Complete(breaks.Snapshot,3);
        Check(breaks.Snapshot.Composition.Pages[0].Lines.Last().HasExplicitPageBreak&&
            breaks.Snapshot.Composition.Pages[1].Lines.Take(2).Select(l=>l.Text).SequenceEqual(new[]{"Après rupture","Fin attachée"})&&
            breaks.Snapshot.Composition.Pages[2].Lines.Single().Text=="Nouveau","Les ruptures explicites et la chaîne héritée doivent coexister.");

        using var blocked=Compose(Write(root,"page-keep-blocked",P("Premier",exact)+P("Attente",keep)+
            P(char.ConvertFromUtf32(0x10fffd))+Section()));
        Check(blocked.Snapshot.Composition.Pages.Single().Lines.Single().Text=="Premier"&&
            blocked.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_KEEP_NEXT_BLOCKED"),"Lien vers un bloc de hauteur inconnue.");
        using var barrier=Compose(Write(root,"page-keep-barrier",P("Placée",keep)+
            P(char.ConvertFromUtf32(0x10fffd),"<w:pageBreakBefore/>")+Section()));
        Check(barrier.Snapshot.Composition.Pages.Single().Lines.Single().Text=="Placée"&&
            barrier.Snapshot.Gaps.All(g=>g.Reason!="PAGE_KEEP_NEXT_BLOCKED"),"La frontière de page doit interrompre le lien.");
    }
}
