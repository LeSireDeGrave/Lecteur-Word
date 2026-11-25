using System.Collections.Immutable;
using System.Security.Cryptography;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static class TypographyCases
{
    static string P(string runs, string props="") => "<w:p>"+props+runs+"</w:p>";
    static string R(string text,string props="") => "<w:r>"+props+"<w:t xml:space=\"preserve\">"+text+"</w:t></w:r>";
    static void Check(bool valid,string message)
    {
        if(!valid)throw new InvalidOperationException(message);
    }
    internal static SemanticDocument Model(InspectionResult inspection) =>
        SemanticReader.Build(inspection).Document ?? throw new InvalidOperationException("SEMANTIC-001 indisponible.");
    static InspectionResult Inspect(string root,string body,Dictionary<string,string>? parts=null,string relationships="")
    {
        var package=Fixtures.Parts(body,parts,relationships);
        if(parts?.ContainsKey("word/theme1.xml")==true)
        {
            string types=System.Text.Encoding.UTF8.GetString(package["[Content_Types].xml"]);
            package["[Content_Types].xml"]=System.Text.Encoding.UTF8.GetBytes(types.Replace(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.theme1+xml",
                "application/vnd.openxmlformats-officedocument.theme+xml",StringComparison.Ordinal));
        }
        string path=Fixtures.Write(root,"typography-"+Guid.NewGuid().ToString("N")+".docx",package);
        var result=DocumentInspector.InspectDocument(path);
        Check(result.Manifest.InspectionComplete,"Inspection de la fixture refusée : "+string.Join(", ",result.Manifest.Diagnostics.Take(6).Select(d=>d.Code)));
        return result;
    }

    public static void Styles(string root)
    {
        string linked=Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("theme","theme","theme1.xml");
        const string A="http://schemas.openxmlformats.org/drawingml/2006/main";
        var parts=new Dictionary<string,string>
        {
            ["word/styles.xml"]=Fixtures.Xml("styles",
                "<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val=\"26\"/><w:lang w:val=\"fr-FR\"/><w:rFonts w:asciiTheme=\"minorAscii\"/></w:rPr></w:rPrDefault></w:docDefaults>"+
                "<w:style w:type=\"paragraph\" w:styleId=\"Base\"><w:rPr><w:b/><w:color w:themeColor=\"accent1\" w:val=\"FFFFFF\"/></w:rPr></w:style>"+
                "<w:style w:type=\"paragraph\" w:styleId=\"Derived\"><w:basedOn w:val=\"Base\"/><w:rPr><w:b/><w:i/><w:spacing w:val=\"10\"/></w:rPr></w:style>"+
                "<w:style w:type=\"character\" w:styleId=\"Inline\"><w:rPr><w:u w:val=\"single\"/><w:sz w:val=\"30\"/></w:rPr></w:style>"+
                "<w:style w:type=\"table\" w:styleId=\"Table\"><w:rPr><w:sz w:val=\"34\"/></w:rPr></w:style>"),
            ["word/theme1.xml"]="<a:theme xmlns:a=\""+A+"\"><a:themeElements><a:clrScheme><a:accent1><a:srgbClr val=\"315075\"/></a:accent1></a:clrScheme>"+
                "<a:fontScheme><a:minorFont><a:latin typeface=\"DejaVu Serif\"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>"
        };
        string body=P(R("Style", "<w:rPr><w:rStyle w:val=\"Inline\"/><w:b w:val=\"0\"/></w:rPr>") + R("Hérité"),
            "<w:pPr><w:pStyle w:val=\"Derived\"/><w:rPr><w:sz w:val=\"44\"/></w:rPr></w:pPr>")+
            "<w:tbl><w:tblPr><w:tblStyle w:val=\"Table\"/></w:tblPr><w:tr><w:tc>"+Fixtures.Paragraph("Cellule")+"</w:tc></w:tr></w:tbl>"+
            P(R("Invalide","<w:rPr><w:rStyle w:val=\"Absent\"/><w:sz w:val=\"xxx\"/></w:rPr>"))+
            P(R("Type incompatible","<w:rPr><w:rStyle w:val=\"Base\"/></w:rPr>"));
        var inspected=Inspect(root,body,parts,linked);
        var semantic=SemanticReader.Build(inspected);
        var model=semantic.Document??throw new InvalidOperationException("SEMANTIC-001 indisponible.");
        using var session=new TypographySession(inspected,model);
        var result=session.Measure();
        var first=result.Paragraphs[0].Fragments.First(f=>f.Text=="Style");
        Check(first.Style is {Size:15,Bold:false,Italic:true,Tracking:.5f,Underline:"single",Color:"315075",Language:"fr-FR"},"Cascade effective inattendue.");
        Check(first.Request is {Theme:true,Value:"minorAscii"}&&first.RequestedFamily=="DejaVu Serif"&&first.ThemeSource is not null,
            "Thème de police absent ou priorité erronée.");
        var inherited=result.Tokens.Single(t=>t.Original=="Hérité").Style;
        Check(inherited is {Size:13,Bold:false,Italic:true} && result.Paragraphs[0].MarkStyle.Size==22,"Les métriques de la marque ont contaminé le texte.");
        var sizeId=first.Style.Origins["size"].NodeId;
        Check(model.Source(sizeId).Location.PartUri=="/word/styles.xml"&&model.Attribute(sizeId,"val")=="30"&&
            first.Style.Origins["colorThemeValue"].Location.PartUri=="/word/theme1.xml","Provenance des valeurs effectives perdue.");
        Check(result.Tokens.Single(t=>t.Original=="Cellule").Style?.Size==17,"Héritage typographique de tableau absent.");
        Check(result.Diagnostics.Any(i=>i.Code=="TEXT_PROPERTY_PARTIAL"&&i.Source?.PartUri=="/word/document.xml"),
            "Valeur contradictoire sans diagnostic.");
        Check(result.Diagnostics.Any(i=>i.Code=="TEXT_STYLE_REFERENCE_PARTIAL"&&i.Source?.PartUri=="/word/document.xml"),
            "Un style existant de type incompatible doit rester diagnostiqué.");
        var missing=model.StyleUses.Single(u=>model.Attribute(u.Id,"val")=="Absent");
        Check(missing is {Resolved:true,MissingReferenceIgnored:true}&&
            semantic.Issues.Any(i=>i.Code=="SEMANTIC_STYLE_REFERENCE_IGNORED"&&i.Source==model.Source(missing.Id).Location),
            "Une référence de caractère absente est ignorée et sa source conservée.");
        Verify(model,result);
    }

    internal static void Shadows(string root)
    {
        const string ns="http://schemas.microsoft.com/office/word/2010/wordml";
        const string drawing="http://schemas.openxmlformats.org/drawingml/2006/main";
        string Shadow(string color)=>
            $"<w14:shadow xmlns:w14=\"{ns}\" w14:sx=\"100000\" w14:sy=\"100%\" w14:dist=\"25400\" "+
            "w14:dir=\"5400000\" w14:blurRad=\"38100\" w14:algn=\"ctr\">"+color+"</w14:shadow>";
        string outline=$"<w14:textOutline xmlns:w14=\"{ns}\" w14:w=\"0\"><w14:noFill/>"+
            "<w14:prstDash w14:val=\"solid\"/><w14:round/></w14:textOutline>";
        var style=P(R("Encre du texte","<w:rPr><w:sz w:val=\"36\"/>"+outline+"</w:rPr>"));
        var parts=new Dictionary<string,string>
        {
            ["word/styles.xml"]=Fixtures.Xml("styles","<w:docDefaults><w:rPrDefault><w:rPr>"+
                Shadow("<w14:schemeClr w14:val=\"dk1\"><w14:alpha w14:val=\"60000\"/></w14:schemeClr>")+
                "</w:rPr></w:rPrDefault></w:docDefaults>"),
            ["word/theme1.xml"]=$"<a:theme xmlns:a=\"{drawing}\"><a:themeElements><a:clrScheme>"+
                "<a:dk1><a:srgbClr val=\"FE0010\"/></a:dk1></a:clrScheme></a:themeElements></a:theme>"
        };
        string relationships=Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("theme","theme","theme1.xml");
        var inherited=Inspect(root,style,parts,relationships);
        using var withShadow=PageComposer.Compose(inherited);
        using var noShadow=PageComposer.Compose(Inspect(root,style));
        var drawn=withShadow.Snapshot;
        Check(drawn.Coverage==PageCoverage.Complete&&noShadow.Snapshot.Coverage==PageCoverage.Complete,"Effets valides refusés");
        var glyphs=drawn.Pages.Single().Geometry.Lines.Single().Native!;
        var fragment=glyphs.Parts.Single(p=>p.Fragment is not null).Fragment!;
        Check(fragment.Style.Shadow is {Color:"FE0010",Alpha:153,OffsetY:2,Blur:3}&&
            fragment.Style.Origins.ContainsKey("shadowThemeValue")&&fragment.Style.Origins.ContainsKey("textOutline"),
            "L'ombre héritée doit conserver la couleur de thème, les unités et la provenance");
        var plain=noShadow.Snapshot.Pages.Single().Geometry;
        Check(glyphs.Advance==plain.Lines.Single().Native!.Advance&&
            glyphs.Height==plain.Lines.Single().Native!.Height&&
            !Composer.PagePng(drawn.Pages.Single().Geometry).AsSpan().SequenceEqual(Composer.PagePng(plain)),
            "L'ombre modifie les pixels en conservant les avances et la hauteur");

        string direct=Shadow("<w14:srgbClr w14:val=\"0055BB\"/>");
        using var overridden=PageComposer.Compose(Inspect(root,style.Replace(outline,direct+outline,StringComparison.Ordinal),parts,relationships));
        var replaced=overridden.Snapshot.Typography!.Paragraphs.SelectMany(p=>p.Fragments).Single(f=>f.Text=="Encre du texte");
        Check(replaced.Style.Shadow is {Color:"0055BB",Alpha:255}&&!replaced.Style.Origins.ContainsKey("shadowThemeValue"),
            "La couleur directe doit remplacer l'ombre de thème");
        using var unsupported=PageComposer.Compose(Inspect(root,style.Replace(outline,direct.Replace(
            "w14:sx=\"100000\"","w14:sx=\"120000\"",StringComparison.Ordinal),StringComparison.Ordinal)));
        Check(unsupported.Snapshot.Coverage!=PageCoverage.Complete&&unsupported.Snapshot.Composition.Issues
            .Any(i=>i.BlocksRendering&&i.Code is "LAYOUT_CHARACTER_PROPERTY_UNQUALIFIED" or "TEXT_PROPERTY_PARTIAL"),
            "Transformation d'ombre non rendue à signaler");
    }

    public static void Shaping(string root)
    {
        const string W14="http://schemas.microsoft.com/office/word/2010/wordml";
        string font="<w:rFonts w:ascii=\"DejaVu Serif\" w:hAnsi=\"DejaVu Serif\" w:cs=\"DejaVu Serif\"/><w:sz w:val=\"48\"/>";
        string kern="<w:rPr>"+font+"<w:kern w:val=\"2\"/></w:rPr>";
        string lig="<w:rPr>"+font+"<w14:ligatures xmlns:w14=\""+W14+"\" w14:val=\"standard\"/></w:rPr>";
        string supplementary=char.ConvertFromUtf32(0x1d434);
        string body=P(R("AV",kern))+P(R("AV","<w:rPr>"+font+"</w:rPr>"))+P(R("office",lig))+
            P(R("office","<w:rPr>"+font+"<w:spacing w:val=\"5\"/></w:rPr>"))+
            P(R(supplementary,"<w:rPr>"+font+"</w:rPr>"))+
            P(R("e")+R("\u0301")+R("no\u00a0break &amp; "+char.ConvertFromUtf32(0x10fffd)))+
            P(R("abc אבג 12 سلام")+"<w:r><w:tab/></w:r>"+R("token"),"<w:pPr><w:bidi/></w:pPr>")+
            P("<w:fldSimple w:instr=\"PAGE\">"+R("12")+"</w:fldSimple>");
        var inspected=Inspect(root,body,new()
            {
                ["word/settings.xml"]=Fixtures.Xml("settings","<w:compat>"+"""
                    <w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="14"/>
                    """+"</w:compat>")
            },Fixtures.Relationship("settings","settings","settings.xml"));
        var model=Model(inspected);
        using var session=new TypographySession(inspected,model);
        var first=session.Measure();
        Verify(model,first);
        var fragments=first.Paragraphs.SelectMany(p=>p.Fragments).ToArray();
        var av=fragments.Where(f=>f.Text=="AV").ToArray();
        Check(av.Length==2&&av[0].Face?.Sha256==av[1].Face?.Sha256&&
            av[0].Measure!.Metrics.Width<av[1].Measure!.Metrics.Width,"La paire AV ne vérifie pas le crénage sur la même face.");
        var office=fragments.Single(f=>f.Text=="office"&&f.Style.Ligatures=="standard");
        Check(office.Measure!.Glyphs.Length<office.Text.Length&&office.Measure.Clusters.Any(c=>c.Range.EndExclusive-c.Range.Start>1),
            "Ligature réelle ou étendue du cluster manquante.");
        var tracked=fragments.Single(f=>f.Text=="office"&&f.Style.Tracking==.25f);
        Check(tracked.Measure is {} trackedMeasure&&trackedMeasure.Glyphs.Length==tracked.Text.Length&&
            trackedMeasure.Clusters.All(c=>c.Range.EndExclusive-c.Range.Start==1),
            "Les ligatures facultatives doivent être désactivées par défaut dans la mesure latine.");
        var nonBmp=fragments.Single(f=>f.Text==supplementary);
        Check(nonBmp.Measure is {Glyphs.Length:1,Metrics.Width:>0} && nonBmp.Measure.Glyphs[0].Id!=0 &&
            nonBmp.Measure.Clusters.Single().Range==new Utf16Span(0,2),"Sculptage ou plage du caractère hors BMP incorrects.");
        var combining=fragments.Single(f=>f.Text.StartsWith("e\u0301",StringComparison.Ordinal));
        Check(combining.Sources.Length>=2&&combining.Measure?.Clusters[0].Range==new Utf16Span(0,2),"Graphème fractionné entre nœuds.");
        int nbsp=combining.Text.IndexOf('\u00a0');
        if(nbsp>=0)Check(combining.Breaks.All(b=>b.Offset!=nbsp&&b.Offset!=nbsp+1),"Coupure à l’espace insécable.");
        Check(first.Tokens.Any(t=>t.Role==TextRole.StoredField&&t.Original=="12"),"Résultat de champ confondu avec un calcul.");
        Check(fragments.Any(f=>f.Script=="Hebr"&&(f.BidiLevel&1)==1&&f.Measure is {Glyphs.Length:>0})&&
            fragments.Any(f=>f.Script=="Arab"&&(f.BidiLevel&1)==1&&f.Measure is {Glyphs.Length:>0}),"RTL ou itemisation Unicode non démontrés.");
        Check(first.Paragraphs.Any(p=>p.RightToLeft&&p.Controls.Any(c=>c.Kind=="Tab")&&
            !p.VisualFragmentOrder.SequenceEqual(Enumerable.Range(0,p.Fragments.Length))),"Ordre bidi ou contrôle absent.");
        var pua=fragments.Single(f=>f.Text==char.ConvertFromUtf32(0x10fffd));
        Check(pua.Measure is null&&first.Diagnostics.Any(i=>i.Code=="TEXT_MISSING_FACE"),"Un caractère privé manquant a été remplacé silencieusement.");
        Check(fragments.Any(f=>f.Script=="Arab"&&f.Face?.Status=="substituted"&&f.Face.UsedFamily!=f.RequestedFamily) &&
            first.Diagnostics.Any(i=>i.Code=="TEXT_FONT_SUBSTITUTION"),"Substitution de l’arabe non tracée.");

        // Use the actual raster and compare its bounds with the declared glyph ink at 2 px/pt.
        using var sk=SKSurface.Create(new SKImageInfo(320,170));
        sk.Canvas.Clear(SKColors.White);
        sk.Canvas.Scale(2);
        session.Paint(sk.Canvas,office,12,52,SKColors.Black);
        using var image=sk.Snapshot();
        using var bitmap=SKBitmap.FromImage(image);
        var pixels=new List<(int X,int Y)>();
        for(int y=0;y<bitmap.Height;y++)
            for(int x=0;x<bitmap.Width;x++)
                if(bitmap.GetPixel(x,y).Red<220)pixels.Add((x,y));
        var expected=office.Measure.Metrics.Ink!.Value;
        Check(pixels.Count>30&&Near(pixels.Min(p=>p.X),expected.Left,12)&&Near(pixels.Max(p=>p.X)+1,expected.Right,12)&&
            Near(pixels.Min(p=>p.Y),expected.Top,52)&&Near(pixels.Max(p=>p.Y)+1,expected.Bottom,52),"L’encre du raster diverge des positions mesurées.");
        using(var encoded=image.Encode(SKEncodedImageFormat.Png,100))File.WriteAllBytes(Path.Combine(root,"glyphs.png"),encoded.ToArray());
        var warm=session.Measure();
        Check(warm.Timings.ShapeCacheHits>0&&warm.Paragraphs.SelectMany(p=>p.Fragments).Zip(fragments).All(
            pair=>pair.First.Measure==pair.Second.Measure),"Les mesures mises en cache diffèrent.");
        File.WriteAllText(Path.Combine(root,"text-comparison.json"),System.Text.Json.JsonSerializer.Serialize(new
        {
            sameFace=av[0].Face,
            kernedPt=Pt(av[0].Measure!.Metrics.Width), unkernedPt=Pt(av[1].Measure!.Metrics.Width),
            legacyAvPt=session.LegacyAdvance(av[0]),officeWidthPt=Pt(office.Measure.Metrics.Width),
            legacyOfficePt=session.LegacyAdvance(office),officeGlyphs=office.Measure.Glyphs.Length,
            rasterBounds=new { left=pixels.Min(p=>p.X),right=pixels.Max(p=>p.X)+1,top=pixels.Min(p=>p.Y),bottom=pixels.Max(p=>p.Y)+1 }
        }));

        static bool Near(int actual,long emu,int origin)=>Math.Abs(actual-(Pt(emu)+origin)*2)<=3;
        static double Pt(long emu)=>(double)emu/TypographySession.EmuPerPoint;
    }

    public static void Bounds(string root)
    {
        var input=Inspect(root,Fixtures.Paragraph("Un témoin pour les budgets."));
        var model=Model(input);
        Refuse(new(MaxChars:4),"TEXT_TOKEN_LIMIT");
        Refuse(new(MaxGlyphs:2),"TEXT_GLYPH_LIMIT");
        Refuse(new(MaxParagraphChars:4),"TEXT_PARAGRAPH_LIMIT");
        using var session=new TypographySession(input,model);
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        try { session.Measure(cancelled.Token); throw new InvalidOperationException("Annulation ignorée."); }
        catch (OperationCanceledException) {}
        Check(session.Measure().Paragraphs[0].Fragments[0].Measure is not null,"Session inutilisable après annulation.");
        session.Dispose();
        try { session.Measure(); throw new InvalidOperationException("Session fermée utilisable."); }
        catch (ObjectDisposedException) {}

        void Refuse(TextBudget budget,string code)
        {
            using var instance=new TypographySession(input,model,budget);
            try{instance.Measure();throw new InvalidOperationException("Budget ignoré : "+code);}
            catch(InvalidDataException e) when(e.Message==code){}
        }
    }

    internal static void Verify(SemanticDocument model,TypographyDocument result)
    {
        var byId=result.Tokens.ToDictionary(t=>t.Source.NodeId);
        var measuredSpans=new Dictionary<string,List<Utf16Span>>();
        foreach(var e in model.Elements.Where(e=>e.Kind is SemanticKind.Text or SemanticKind.DeletedText or SemanticKind.FieldInstruction))
        {
            var token=byId[e.Id];
            Check(token.Original==model.RawText(e.Id)&&token.Source==model.TextSource(e.Id),"Texte source, identifiant ou étendue modifiés.");
        }
        foreach(var paragraph in result.Paragraphs)
        {
            foreach(var fragment in paragraph.Fragments)
            {
                int end=0;
                foreach(var source in fragment.Sources)
                {
                    var span=source.Source.Span!.Value;
                    var raw=model.RawText(source.Source.NodeId)!;
                    Check(source.FragmentRange.Start==end&&source.Source==model.Source(source.Source.NodeId,span)&&
                        raw.AsSpan(span.Start,span.EndExclusive-span.Start).SequenceEqual(fragment.Text.AsSpan(end,source.FragmentRange.EndExclusive-end)),
                        "Position de source inventée ou perte de texte.");
                    end=source.FragmentRange.EndExclusive;
                    if(!measuredSpans.TryGetValue(source.Source.NodeId,out var spans))measuredSpans.Add(source.Source.NodeId,spans=[]);
                    spans.Add(span);
                }
                Check(end==fragment.Text.Length,"Fragment incomplet.");
                if(fragment.Measure is not {} measured)continue;
                end=0;
                foreach(var cluster in measured.Clusters)
                {
                    Check(cluster.Range.Start==end&&(end==0||!char.IsLowSurrogate(fragment.Text[end])),"Demi-scalaire dans un cluster.");
                    end=cluster.Range.EndExclusive;
                    for(int i=0;i<cluster.GlyphCount;i++)Check(measured.Glyphs[cluster.FirstGlyph+i].Cluster==cluster.Range.Start,"Glyphe sans source.");
                }
                Check(end==fragment.Text.Length&&measured.Glyphs.Sum(g=>g.AdvanceX)==measured.Metrics.Width,
                    "Cluster ou avancée incohérente.");
                Check(fragment.Breaks.All(b=>measured.Clusters.Any(c=>c.Range.EndExclusive==b.Offset)),"Coupure intérieure à un cluster.");
            }
        }
        foreach(var token in result.Tokens.Where(t=>t.Role is TextRole.Source or TextRole.StoredField && t.Style?.Hidden!=true && t.Original is {Length:>0}))
        {
            int end=0;
            foreach(var span in measuredSpans.GetValueOrDefault(token.Source.NodeId,[]))
            {
                Check(span.Start==end,"Trou ou doublon dans la projection.");
                end=span.EndExclusive;
            }
            Check(end==token.Original!.Length,"Une partie du texte actif a disparu de la projection.");
        }
    }
}
