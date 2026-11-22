using System.Buffers.Binary;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class FontCases
{
    internal static void LegacySymbolsCase(string root)
    {
        string path=Path.Combine(AppContext.BaseDirectory,"../../../../../src/DocxFlow/Fonts/DejaVuSerif.ttf");
        using var typeface=SKTypeface.FromFile(path);
        using var font=new SKFont(typeface,12);
        ushort glyph=font.GetGlyph('A');
        byte[] cmap=SampleCmap(glyph);
        Check(SymbolCmap.Lookup(cmap,0xf0b7,typeface.GlyphCount,()=>{})==(SymbolCmap.Status.Present,glyph)&&
            SymbolCmap.Lookup(cmap,0xb7,typeface.GlyphCount,()=>{})==(SymbolCmap.Status.Present,glyph)&&
            SymbolCmap.Lookup(cmap,0xf077,typeface.GlyphCount,()=>{})==(SymbolCmap.Status.Present,(ushort)0),
            "Table 3/0 format 4, index direct et alias de l’octet.");
        var malformed=cmap.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(40),0xfffe);
        Check(SymbolCmap.Lookup(malformed,0xf0b7,typeface.GlyphCount,()=>{}).Status==SymbolCmap.Status.Invalid&&
            SymbolCmap.Lookup(cmap,0xf0b7,glyph,()=>{}).Status==SymbolCmap.Status.Invalid,
            "Décalage glyphIdArray ou index de glyphe hors limites.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        bool interrupted=false;
        try{_=SymbolCmap.Lookup(cmap,0xf0b7,typeface.GlyphCount,cancelled.Token.ThrowIfCancellationRequested);}
        catch(OperationCanceledException){interrupted=true;}
        Check(interrupted,"La vérification cmap doit respecter l’annulation.");

        Check(LegacySymbols.Mapping("Symbol",0xf0a7)?.Code==0x2663&&
            LegacySymbols.Mapping("Wingdings",0xf0a7)?.Code==0x25aa&&
            LegacySymbols.Mapping("Arial",0xf0e8) is null,"La famille doit déterminer le sens du code.");
        string symbols="""
            <w:r><w:rPr><w:color w:val="B01010"/></w:rPr><w:sym w:font="LW TestFace" w:char="F0B7"/></w:r>
            <w:r><w:sym w:font="Symbol" w:char="F0B7"/></w:r>
            """;
        const string marker="""
            <w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>
            """;
        string body="<w:p>"+symbols+"</w:p>"+
            "<w:p><w:pPr>"+marker+"</w:pPr>"+Run("DejaVu Serif","Puce distincte")+"</w:p>"+
            "<w:p><w:pPr><w:pageBreakBefore/></w:pPr>"+Run("Symbol","Source \uf02b opérateur")+"</w:p>";
        string witness=Fixture("legacy-symbols",body,TestFont(4,cmap));
        string hash=Sha(witness);
        var input=DocumentInspector.InspectDocument(witness);
        var original=ManifestJson.Serialize(input.Manifest);
        using var lease=PageComposer.Compose(input);
        var snapshot=lease.Snapshot;
        Check(snapshot.Coverage==PageCoverage.Complete&&snapshot.Pages.Length==2,
            "Composition native symbolique. "+string.Join(", ",snapshot.Composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));
        var controls=snapshot.Typography!.Paragraphs.SelectMany(p=>p.Controls).Where(c=>c.Kind=="Symbol").ToArray();
        var embedded=controls[0].Symbol!;
        var adobe=controls[1].Symbol!;
        Check(controls.Length==2&&embedded.LegacySymbol is {Family:"LW TestFace",Method:"embedded",DrawingCode:null}&&
            embedded.Measure?.Glyphs.Single().Id==glyph&&adobe.LegacySymbol is {Family:"Symbol",DrawingCode:0x2022}&&
            controls.All(c=>c.Source.Span is null&&c.Symbol!.Text=="\uf0b7"),"Source, glyphe cmap incorporé et décision de substitution.");
        var first=snapshot.Composition.Pages[0].Lines[0];
        var marks=first.Native!.Generated;
        Check(marks.Length==2&&marks[1].X==marks[0].X+embedded.Measure!.Metrics.Width&&
            marks[0].Fragment==embedded&&PageCoordinates.Clusters(first).Select(c=>c.Sources.Single()).SequenceEqual(controls.Select(c=>c.Source)),
            "Même avance pour mesure, placement, dessin et navigation du w:sym.");
        Check(snapshot.ListMarkers.Values.Single() is {Label.Text:"\uf0b7",TextStart:not null} numbered&&
            numbered.TextStart>numbered.X+numbered.Decoration!.Fragment.Measure!.Metrics.Width&&
            snapshot.Composition.Pages[0].Lines[1].Text=="Puce distincte","Le suffixe espace doit suivre le marqueur substitué.");
        var textLine=snapshot.Composition.Pages[1].Lines.Single();
        var source=PageCoordinates.Clusters(textLine).Single(c=>c.Text=="\uf02b").Sources.Single();
        Check(source.Span is {} span&&span.EndExclusive-span.Start==1&&snapshot.Semantic!.RawText(source.NodeId)![span.Start..span.EndExclusive]=="\uf02b"&&
            textLine.Text.Contains('\uf02b')&&!textLine.Text.Contains('+'),"Valeur de recherche et plage du caractère privé.");
        var readable=ReadableReader.Read(input);
        Check(readable.Presentation.BodyBlocks.Any(p=>readable.Presentation.TextFor(p)=="Source \uf02b opérateur"),
            "La copie en lecture doit conserver la valeur source.");
        PageCases.VerifyPlaced(snapshot);

        using var again=PageComposer.Compose(input);
        var raster=Composer.PagePng(snapshot.Composition.Pages[0],1.5f);
        using var bitmap=SKBitmap.Decode(raster);
        var ink=embedded.Measure!.Metrics.Ink!.Value;
        int x0=(int)Math.Floor((first.NativeOffsetX+PageComposer.Points(marks[0].X+ink.Left))*1.5);
        int y0=(int)Math.Floor((first.Baseline+PageComposer.Points(ink.Top))*1.5);
        int x1=(int)Math.Ceiling((first.NativeOffsetX+PageComposer.Points(marks[0].X+ink.Right))*1.5);
        int y1=(int)Math.Ceiling((first.Baseline+PageComposer.Points(ink.Bottom))*1.5);
        Check(raster.AsSpan().SequenceEqual(Composer.PagePng(again.Snapshot.Composition.Pages[0],1.5f))&&
            Enumerable.Range(y0,y1-y0).Any(y=>Enumerable.Range(x0,x1-x0)
                .Any(x=>bitmap.GetPixel(x,y) is {Red:>100,Green:<90,Blue:<90})),"L’encre du glyphe mesuré doit figurer au raster stable.");
        File.WriteAllBytes(Path.Combine(root,"glyphes-symboliques.png"),raster);
        Check(Sha(witness)==hash&&original.SequenceEqual(ManifestJson.Serialize(input.Manifest)),"Le DOCX ou le JSON v1 a été modifié.");

        using var missing=PageComposer.Compose(DocumentInspector.InspectDocument(Fixture("legacy-rights",
            Fixtures.Paragraph("Avant")+"<w:p>"+symbols+Run("Arial","\uf0b7")+"</w:p>",TestFont(2,cmap))));
        Check(missing.Snapshot.RulerModel!.Fonts.Embedded.Single().Status=="refused"&&
            missing.Snapshot.Typography!.Paragraphs.SelectMany(p=>p.Controls)
                .Any(c=>c.Symbol is {LegacySymbol.Family:"LW TestFace",Measure:null})&&
            missing.Snapshot.Typography.Paragraphs.SelectMany(p=>p.Fragments)
                .Any(f=>f is {Text:"\uf0b7",LegacySymbol.Family:"Arial",Measure:null}),"Face refusée ou code privé hors famille remplacé.");
        using var corrupted=PageComposer.Compose(DocumentInspector.InspectDocument(Fixture("legacy-corrupt",
            Fixtures.Paragraph("Avant")+"<w:p>"+symbols+"</w:p>",TestFont(0,malformed))));
        Check(corrupted.Snapshot.Coverage==PageCoverage.Partial&&
            corrupted.Snapshot.Gaps.Any(g=>g.Reason=="LINE_WORD_SYMBOL_UNRESOLVED"),"Cmap corrompue acceptée.");
        var model=TypographyCases.Model(input);
        using var bounded=new TypographySession(input,model,new TextBudget(MaxGlyphs:1));
        bool limited=false;
        try{_=bounded.Measure();}
        catch(InvalidDataException error) when(error.Message=="TEXT_GLYPH_LIMIT"){limited=true;}
        Check(limited,"Les glyphes symboliques participent au quota.");

        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow();
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(witness));
            window.GetVisualDescendants().OfType<Button>().First(b=>b.Name?.StartsWith("Action_search_",StringComparison.Ordinal)==true)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.GetVisualDescendants().OfType<TextBox>().Single(b=>b.Name=="SearchBox").Text="\uf02b";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content is string caption&&caption.Contains('\uf02b'))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.CurrentPage==2&&window.SelectedNativeSources.Contains(source),"Recherche et navigation privées dans Avalonia.");
        }
        finally{window.Close();}

        string Fixture(string name,string content,byte[] data)
        {
            const string defs="""
                <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/>
                  <w:suff w:val="space"/><w:pPr><w:ind w:left="360" w:hanging="360"/></w:pPr>
                  <w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol"/></w:rPr></w:lvl></w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                """;
            var parts=Fixtures.Parts(content+Section,new()
            {
                ["word/fontTable.xml"]=Fixtures.Xml("fonts","""<w:font w:name="LW TestFace"><w:embedRegular r:id="font"/></w:font>"""),
                ["word/numbering.xml"]=Fixtures.Xml("numbering",defs)
            },Fixtures.Relationship("ft","fontTable","fontTable.xml")+Fixtures.Relationship("num","numbering","numbering.xml"),
                new(){["word/fonts/test.ttf"]=data});
            parts["word/_rels/fontTable.xml.rels"]=Encoding.UTF8.GetBytes(Fixtures.Relations(Fixtures.Relationship("font","font","fonts/test.ttf")));
            parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"])
                .Replace("</Types>","<Default Extension=\"ttf\" ContentType=\"application/x-font-ttf\"/></Types>",StringComparison.Ordinal));
            return Fixtures.Write(root,name+".docx",parts);
        }
    }

    static byte[] SampleCmap(ushort glyph)
    {
        var bytes=new byte[46];
        void U16(int at,ushort value)=>BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at),value);
        U16(2,1);U16(4,3);U16(10,12);
        U16(12,4);U16(14,34);U16(18,4);U16(20,4);U16(22,1);
        U16(26,0xf0b7);U16(28,0xffff);
        U16(32,0xf0b7);U16(34,0xffff);
        U16(38,1);U16(40,4);U16(44,glyph);
        return bytes;
    }
}
