using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    const string FormattingBase="""
        <w:style w:type="paragraph" w:styleId="Base">
          <w:rPr><w:sz w:val="40"/><w:color w:val="193D62"/></w:rPr>
        </w:style>
        <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/></w:style>
        """;
    const string FormattingDerived="""<w:pStyle w:val="Derived"/>""";
    const string FormattingTheme="""
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <a:themeElements><a:clrScheme>
            <a:accent1><a:srgbClr val="4F81BD"/></a:accent1>
            <a:accent2><a:srgbClr val="CC3300"/></a:accent2>
          </a:clrScheme></a:themeElements>
        </a:theme>
        """;

    internal static void WordStyleCompatibility(string root)
    {
        string auxiliary=FormattingBase.Replace("val=\"40\"","val=\"60\"",StringComparison.Ordinal);
        foreach(int? mode in new int?[]{null,12,14,15})
        {
            var input=FormattingFixture(root,"mode-"+mode,Paragraph(
                FormattingRun("Hérité","<w:u/>")+FormattingRun(" Direct","<w:sz w:val=\"24\"/>"),FormattingDerived),
                FormattingBase,mode,auxiliary);
            using var lease=PageComposer.Compose(input);
            var s=lease.Snapshot;
            bool ecma=mode is null or 12;
            bool plainCopy=mode==14;
            Check(s.Semantic?.StyleCompatibility.Mode==(mode??12)&&s.StyleDecision is
                {SelectedRoot.Location.PartUri:"/word/styles.xml"} decision&&decision.Selection==
                (ecma?PageStyleSelection.EcmaCompatibility:plainCopy?PageStyleSelection.OrdinaryWithPlainCopy:
                    PageStyleSelection.Unresolved),"Profil et provenance de la partie retenue.");
            Check(s.Coverage==(ecma||plainCopy?PageCoverage.Complete:PageCoverage.Partial),"Couverture selon le profil documenté.");
            Check(s.Composition.Issues.Any(i=>i.Source?.PartUri=="/word/effects.xml"&&i.BlocksRendering==(!ecma&&!plainCopy)&&
                i.Code==(ecma?"PAGE_STYLES_EFFECTS_ECMA_IGNORED":"PAGE_STYLES_EFFECTS_DIVERGENT")),
                "La décision doit être explicite dans les deux profils.");
            var styles=Line(s,"Hérité Direct").Native!.Parts.Select(p=>p.Fragment?.Style).OfType<TextStyle>().Distinct().ToArray();
            Check(styles is [{Size:20,Underline:"none"},{Size:12}]&&
                styles[0].Origins["size"].Location.PartUri=="/word/styles.xml"&&
                styles[1].Origins["size"].Location.PartUri=="/word/document.xml","Cascade active et propriété directe.");
        }
    }

    internal static void WordStyleAttributeOrder(string root)
    {
        const string common="""
            <w:style w:type="paragraph" w:styleId="Base">
              <w:pPr><w:spacing w:after="80" w:line="360" w:lineRule="atLeast"/></w:pPr>
              <w:rPr><w:color w:val="F0F0F0" w:themeColor="accent1"/></w:rPr>
            </w:style>
            <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/></w:style>
            """;
        string reordered=common.Replace("w:type=\"paragraph\" w:styleId=\"Base\"",
                "w:styleId=\"Base\" w:type=\"paragraph\"",StringComparison.Ordinal)
            .Replace("w:after=\"80\" w:line=\"360\" w:lineRule=\"atLeast\"",
                "w:lineRule=\"atLeast\" w:line=\"360\" w:after=\"80\"",StringComparison.Ordinal)
            .Replace("w:val=\"F0F0F0\" w:themeColor=\"accent1\"",
                "w:themeColor=\"accent1\" w:val=\"F0F0F0\"",StringComparison.Ordinal);
        string body=Paragraph(FormattingRun("Ordre","<w:b/>"),FormattingDerived)+P("Second paragraphe");
        using var lease=PageComposer.Compose(FormattingFixture(root,"attribute-order",body,common,15,reordered));
        Complete(lease.Snapshot,1);
        Check(lease.Snapshot.StyleDecision?.Selection==PageStyleSelection.Equivalent&&
            WordRunStyle(lease.Snapshot,"Ordre") is {Bold:true,Color:"4F81BD"},"Attributs permutés, propriété héritée et graisse directe.");
        using var different=PageComposer.Compose(FormattingFixture(root,"attribute-difference",body,common,15,
            reordered.Replace("after=\"80\"","after=\"90\"",StringComparison.Ordinal)));
        Check(different.Snapshot.Coverage==PageCoverage.Partial&&different.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&i.Message.Contains("spacing",StringComparison.Ordinal)),
            "Une différence réelle de valeur reste significative.");
    }

    internal static void ObservedCollections(string root)
    {
        var styles=new[]{("Title","Titre d’essai",32,"582719"),("Heading1","Chapitre d’essai",18,"354013"),
            ("Heading2","Rubrique d’essai",13,"235478")};
        foreach(int mode in new[]{12,14,15})
        {
            var input=FormattingFixture(root,"styles-observation-"+mode,
                string.Concat(styles.Select(x=>P(x.Item2,$"""<w:pStyle w:val="{x.Item1}"/>"""))),
                Styles(false),mode,Styles(true));
            using var lease=PageComposer.Compose(input);
            var snapshot=lease.Snapshot;
            Check(snapshot.Coverage==(mode is 12 or 14?PageCoverage.Complete:PageCoverage.Partial)&&
                snapshot.StyleDecision?.SelectedRoot?.Location.PartUri=="/word/styles.xml",
                "Le profil ECMA et la copie simple du profil 14 utilisent la collection ordinaire.");
            var alternate=SemanticReader.Build(input,default,StyleCollectionKind.Effects).Document!;
            using var typography=new TypographySession(input,alternate);
            var alternateText=typography.Measure();
            foreach(var (_,text,size,color) in styles)
            {
                var shaped=snapshot.Typography!.Paragraphs.Single(p=>p.Context==text);
                var fragment=shaped.Fragments[0];
                var effect=alternateText.Paragraphs.Single(p=>p.Context==text).Fragments[0];
                Check(fragment.Style is {Bold:true}&&fragment.PointSize==size&&fragment.Style.Color==color&&
                    fragment.Style.Origins["size"].Location.PartUri=="/word/styles.xml"&&
                    effect.PointSize==size+3&&effect.Style.Color=="224488"&&
                    effect.Style.Origins["size"].Location.PartUri=="/word/effects.xml",
                    "Les deux collections définissent des hypothèses indépendantes avant la mesure.");
            }
        }
        Check(FontResolver.FallbackFamilies("Aptos Display","auto").SequenceEqual(
            new[]{"Calibri","Carlito","Liberation Sans","DejaVu Sans"})&&
            FontResolver.FallbackFamilies("Aptos Serif","auto")[0]=="Liberation Serif"&&
            FontResolver.FallbackFamilies("Aptos Mono","auto")[0]=="Liberation Mono",
            "Replis distincts selon la variante de la famille documentée.");

        const string before="""<w:spacing w:before="320"/>""";
        string breaks=P("Premier",before)+Paragraph("""<w:r><w:br w:type="page"/></w:r>""")+
            P("Après saut",before);
        using var noSpacing=PageComposer.Compose(FormattingFixture(root,"word-layout-break-14",breaks,"",14));
        using var modern=PageComposer.Compose(FormattingFixture(root,"word-layout-break-15",breaks,"",15));
        Complete(noSpacing.Snapshot,2);
        Complete(modern.Snapshot,2);
        Check(Close(Line(noSpacing.Snapshot,"Premier").Baseline,Line(modern.Snapshot,"Premier").Baseline)&&
            Close(Line(modern.Snapshot,"Après saut").Baseline-Line(noSpacing.Snapshot,"Après saut").Baseline,16),
            "Le comportement observé après un saut isolé s’applique au profil 14.");

        string Styles(bool auxiliary)=>string.Concat(styles.Select(x=>$"""
            <w:style w:type="paragraph" w:styleId="{x.Item1}">
              <w:rPr><w:b/><w:color w:val="{(auxiliary?"224488":x.Item4)}"/>
                <w:sz w:val="{2*(x.Item3+(auxiliary?3:0))}"/></w:rPr>
            </w:style>
            """));
    }

    internal static void WordStyleMceLists(string root)
    {
        const string left="""
            xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
            xmlns:p="urn:one" xmlns:q="urn:two" mc:Ignorable="p q" mc:ProcessContent="p:a q:b"
            mc:MustUnderstand="w"
            """;
        const string right="""
            xmlns:m="http://schemas.openxmlformats.org/markup-compatibility/2006"
            xmlns:u="urn:one" xmlns:v="urn:two" m:ProcessContent="v:b u:a" m:Ignorable="v u"
            xmlns:word="http://schemas.openxmlformats.org/wordprocessingml/2006/main" m:MustUnderstand="word"
            """;
        const string content="""
            <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
              <mc:Choice Requires="p q"><w:latentStyles/></mc:Choice>
              <mc:Fallback><w:latentStyles/></mc:Fallback>
            </mc:AlternateContent>
            """;
        string body=Paragraph(FormattingRun("MCE","<w:i/>"),FormattingDerived);
        string auxiliary=FormattingBase+content.Replace("Requires=\"p q\"","Requires=\"v u\"",StringComparison.Ordinal);
        using var equivalent=PageComposer.Compose(FormattingFixture(root,"mce-equivalent",body,FormattingBase+content,14,
            auxiliary,left,right));
        Complete(equivalent.Snapshot,1);
        Check(equivalent.Snapshot.StyleDecision?.Selection==PageStyleSelection.Equivalent&&
            WordRunStyle(equivalent.Snapshot,"MCE") is {Italic:true,Size:20},"Listes MCE résolues dans leur contexte XML.");
        using var changed=PageComposer.Compose(FormattingFixture(root,"mce-rebound",body,FormattingBase+content,14,
            auxiliary.Replace("<mc:Choice ","<mc:Choice xmlns:u=\"urn:rebound\" ",StringComparison.Ordinal),left,right));
        Check(changed.Snapshot.Coverage==PageCoverage.Partial&&changed.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_DEFAULTS"),"Un préfixe redéfini dans la portée du choix change la comparaison.");
        using var process=PageComposer.Compose(FormattingFixture(root,"mce-process-content",body,FormattingBase+content,14,
            auxiliary,left,right.Replace("v:b u:a","v:a u:b",StringComparison.Ordinal)));
        Check(process.Snapshot.Coverage==PageCoverage.Partial&&process.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_DEFAULTS"),"Les noms locaux restent associés à leur espace de noms.");
    }

    internal static void WordUnderlineInheritance(string root)
    {
        string styles=FormattingBase.Replace("<w:sz w:val=\"40\"/>","<w:sz w:val=\"40\"/><w:u w:val=\"single\"/>",
            StringComparison.Ordinal);
        string body=Paragraph(FormattingRun("Hérité","<w:u/>"),FormattingDerived)+
            Paragraph(FormattingRun("Masqué","<w:u w:val=\"none\"/>"),FormattingDerived)+
            Paragraph(FormattingRun("Nu","<w:u/>"));
        using var lease=PageComposer.Compose(FormattingFixture(root,"underline-inherited",body,styles,14));
        var s=lease.Snapshot;
        Complete(s,1);
        Check(WordRunStyle(s,"Hérité").Origins["underline"].Location.PartUri=="/word/styles.xml"&&
            Line(s,"Hérité").Native!.Underlines.Length>0,"La forme omise conserve le trait et sa source hérités.");
        Check(Line(s,"Masqué").Native!.Underlines.IsEmpty&&Line(s,"Nu").Native!.Underlines.IsEmpty&&
            !WordRunStyle(s,"Nu").Origins.ContainsKey("underline"),"Désactivation directe et absence d’héritage.");
        using var invalid=PageComposer.Compose(FormattingFixture(root,"underline-invalid",
            Paragraph(FormattingRun("Invalide","<w:u w:val=\"\"/>")),styles,15));
        Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Composition.Issues.Any(i=>
            i.BlocksRendering&&i.Code is "LAYOUT_UNDERLINE_UNQUALIFIED" or "TEXT_PROPERTY_PARTIAL"),
            "Une valeur vide explicite reste invalide.");
    }

    internal static void WordThemeLuminance(string root)
    {
        // The tint vector is from MS-OI29500 2.1.72. For CC3300 HSL L=0.4 and S=1;
        // the shade multiplies L by 0x55/255=1/3, yielding RGB 68, 17, 0.
        const string tint="""<w:color w:val="000001" w:themeColor="accent1" w:themeTint="99" w:themeShade="00"/>""";
        const string shade="""<w:color w:val="FFFFFF" w:themeColor="accent2" w:themeShade="55"/>""";
        string styles=FormattingBase.Replace("<w:color w:val=\"193D62\"/>",tint,StringComparison.Ordinal);
        string body=P("Teinte",FormattingDerived)+Paragraph(FormattingRun("Ombre",shade),FormattingDerived);
        var input=FormattingFixture(root,"theme-luminance",body,styles,15);
        using var lease=PageComposer.Compose(input);
        Complete(lease.Snapshot,1);
        using var independent=new TypographySession(input,TypographyCases.Model(input));
        var measured=independent.Measure();
        var legacy=LayoutReader.Read(input);
        foreach(var (sample,expected) in new[]{("Teinte","95B3D7"),("Ombre","441100")})
        {
            Check(WordRunStyle(lease.Snapshot,sample).Color==expected&&
                measured.Tokens.Single(t=>t.Original==sample).Style?.Color==expected&&
                legacy.Paragraphs.SelectMany(p=>p.Runs).Single(r=>r.Text==sample).Style.Color==expected,
                "La transformation doit être identique avec ou sans cascade de tableau.");
        }
        Check(WordColor.ApplyLuminance("8064A2",null,"BF")=="5F497A"&&
            WordColor.ApplyLuminance("4F81BD",null,"BF")=="366091"&&
            WordColor.ApplyLuminance("98812F",null,"FF")=="98812F"&&
            WordColor.ApplyLuminance("98812F",null,"00")=="000000",
            "Conserver les bornes et réduire l’écart de quantification des ombres HSL.");
        using var invalid=PageComposer.Compose(FormattingFixture(root,"theme-bad-tint",
            Paragraph(FormattingRun("Invalide",tint.Replace("Tint=\"99\"","Tint=\"\"",StringComparison.Ordinal))),styles,14));
        Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Composition.Issues.Any(i=>
            i.Code=="LAYOUT_COLOR_TRANSFORM_UNQUALIFIED"&&i.BlocksRendering),"Une teinte vide ne doit pas activer l’ombre de repli.");
    }

    internal static void WordUnderlineColor(string root)
    {
        string styles=FormattingBase.Replace("<w:sz w:val=\"40\"/>",
            """<w:sz w:val="40"/><w:u w:val="single" w:color="990044"/>""",StringComparison.Ordinal);
        const string themed="""<w:u w:color="FF0000" w:themeColor="accent1" w:themeTint="99" w:themeShade="00"/>""";
        string body=Paragraph(FormattingRun("Hérité","<w:u w:val=\"single\"/>"),FormattingDerived)+
            Paragraph(FormattingRun("Thème",themed),FormattingDerived)+
            Paragraph(FormattingRun("Auto","<w:u w:color=\"auto\"/>"),FormattingDerived);
        var input=FormattingFixture(root,"underline-color",body,styles,15,header:Paragraph(FormattingRun("Entête",themed),FormattingDerived));
        using var lease=PageComposer.Compose(input);
        var s=lease.Snapshot;
        Complete(s,1);
        var style=WordRunStyle(s,"Thème");
        Check(style is {Color:"193D62",UnderlineColor:"95B3D7",Underline:"single"}&&
            style.Origins["underline"].Location.PartUri=="/word/styles.xml"&&
            style.Origins["underlineColor"].Location.PartUri=="/word/document.xml"&&
            style.Origins["underlineColorThemeValue"].Location.PartUri=="/word/theme1.xml"&&
            s.Semantic?.Attribute(style.Origins["underlineColor"].NodeId,"themeTint")=="99",
            "La forme, la couleur du texte, la déclaration de couleur et sa cible restent distinctes.");
        var page=s.Composition.Pages[0];
        using var bitmap=SKBitmap.Decode(Composer.PagePng(page,3));
        foreach(var (line,expected) in new[]{
            (Line(s,"Hérité"),"990044"),(Line(s,"Thème"),"95B3D7"),(Line(s,"Auto"),"193D62"),
            (page.RunningStories.Single().Lines.Single(),"95B3D7")})
        {
            Check(line.Native!.Underlines.Length>0&&line.Native.Underlines.All(u=>u.Color==expected),
                "Couleur du trait dans la feuille physique.");
        }
        Check(bitmap.Pixels.Contains(SKColor.Parse("95B3D7"))&&bitmap.Pixels.Contains(SKColor.Parse("990044"))&&
            bitmap.Pixels.Contains(SKColor.Parse("193D62")),"Les trois couleurs calculées doivent atteindre le raster.");
    }

    internal static void WordRunShading(string root)
    {
        const string direct="""<w:shd w:val="clear" w:fill="FFEB5F"/>""";
        const string theme="""<w:shd w:val="clear" w:fill="FF0000" w:themeFill="accent1" w:themeFillTint="99"/>""";
        const string nil="""<w:shd w:val="nil"/>""";
        string styles=FormattingBase.Replace("<w:color w:val=\"193D62\"/>","<w:color w:val=\"193D62\"/>"+direct,
            StringComparison.Ordinal);
        string body=Paragraph(FormattingRun("Fond",""),FormattingDerived)+
            Paragraph(FormattingRun("Thème",theme),FormattingDerived)+
            Paragraph(FormattingRun("Sans",nil),FormattingDerived);
        using var lease=PageComposer.Compose(FormattingFixture(root,"run-background",body,styles,15));
        var s=lease.Snapshot;
        Complete(s,1);
        Check(WordRunStyle(s,"Fond") is {Background:"FFEB5F",Color:"193D62"}&&
            WordRunStyle(s,"Fond").Origins["background"].Location.PartUri=="/word/styles.xml"&&
            WordRunStyle(s,"Thème").Background=="95B3D7"&&WordRunStyle(s,"Sans").Background is null,
            "Aplat hérité, fond de thème et suppression par nil.");
        var shaded=Composer.PagePng(s.Composition.Pages[0],2);
        using var bitmap=SKBitmap.Decode(shaded);
        Check(bitmap.Pixels.Contains(SKColor.Parse("FFEB5F"))&&bitmap.Pixels.Contains(SKColor.Parse("95B3D7")),
            "Les deux aplats sont peints derrière les glyphes.");
        using var unshaded=PageComposer.Compose(FormattingFixture(root,"run-background-disabled",
            body.Replace(theme,nil,StringComparison.Ordinal),FormattingBase,15));
        Complete(unshaded.Snapshot,1);
        Check(s.Composition.Pages[0].Lines.Zip(unshaded.Snapshot.Composition.Pages[0].Lines)
            .All(p=>p.First.Baseline==p.Second.Baseline&&p.First.Native!.Advance==p.Second.Native!.Advance),
            "L’aplat ne modifie pas la mesure du texte.");
        using var pattern=PageComposer.Compose(FormattingFixture(root,"run-background-pattern",
            Paragraph(FormattingRun("Motif",direct.Replace("\"clear\"","\"pct20\"",StringComparison.Ordinal))),styles,15));
        Check(pattern.Snapshot.Coverage!=PageCoverage.Complete,"Un motif non pris en charge reste diagnostiqué.");
    }

    static TextStyle WordRunStyle(PageCompositionSnapshot s,string text) =>
        Line(s,text).Native!.Parts.First(p=>p.Fragment is not null).Fragment!.Style;

    static string FormattingRun(string text,string properties) =>
        "<w:r><w:rPr>"+properties+"</w:rPr><w:t xml:space=\"preserve\">"+text+"</w:t></w:r>";

    static string CompatibilityMode(int mode) =>
        $"""<w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="{mode}"/>""";

    static InspectionResult FormattingFixture(string root,string name,string body,string styles,int? mode,string? auxiliary=null,
        string ordinaryAttributes="",string auxiliaryAttributes="",string? header=null)
    {
        var parts=new Dictionary<string,string>{
            ["word/styles.xml"]=Styles(styles,ordinaryAttributes),
            ["word/theme1.xml"]=FormattingTheme
        };
        string relations=Fixtures.Relationship("ordinary","styles","styles.xml")+Fixtures.Relationship("theme","theme","theme1.xml");
        if(mode is {} value)
        {
            parts.Add("word/settings.xml",Fixtures.Xml("settings","<w:compat>"+CompatibilityMode(value)+"</w:compat>"));
            relations+=Fixtures.Relationship("settings","settings","settings.xml");
        }
        if(auxiliary is not null)
        {
            parts.Add("word/effects.xml",Styles(auxiliary,auxiliaryAttributes));
            relations+=$"""<Relationship Id="effects" Type="{LayoutReader.StylesEffectsRelation}" Target="effects.xml"/>""";
        }
        string section=Section(width:6400,height:6600,margins:1200);
        if(header is not null)
        {
            parts.Add("word/header1.xml",Fixtures.Xml("hdr",header));
            relations+=Fixtures.Relationship("head","header","header1.xml");
            section=section.Replace("<w:sectPr>","""<w:sectPr><w:headerReference w:type="default" r:id="head"/>""",StringComparison.Ordinal);
        }
        var package=Fixtures.Parts(body+section,parts,relations);
        string types=Encoding.UTF8.GetString(package["[Content_Types].xml"]);
        package["[Content_Types].xml"]=Encoding.UTF8.GetBytes(types
            .Replace("wordprocessingml.theme1+xml","theme+xml",StringComparison.Ordinal)
            .Replace("application/vnd.openxmlformats-officedocument.wordprocessingml.effects+xml",
                "application/vnd.ms-word.stylesWithEffects+xml",StringComparison.Ordinal));
        return DocumentInspector.InspectDocument(Fixtures.Write(root,name+".docx",package));

        static string Styles(string content,string attributes) =>
            Fixtures.Xml("styles",content).Replace("<w:styles ","<w:styles "+attributes+" ",StringComparison.Ordinal);
    }
}
