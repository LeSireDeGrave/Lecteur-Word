using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using SkiaSharp;

static partial class PageCases
{
    internal static void StyleSelection(string root)
    {
        const string normal = """
            <w:style w:type="paragraph" w:default="on" w:styleId="Base">
              <w:name w:val="Base"/><w:next w:val="Derived"/><w:autoRedefine/>
              <w:pPr><w:spacing w:after="120" w:line="280" w:lineRule="atLeast"/></w:pPr>
              <w:rPr><w:rFonts w:asciiTheme="minorAscii"/><w:b/><w:color w:themeColor="accent1" w:val="FFFFFF"/>
                <w:sz w:val="24"/></w:rPr>
            </w:style>
            """;
        const string derived = """
            <w:style w:type="paragraph" w:styleId="Derived">
              <w:name w:val="Dérivé"/><w:basedOn w:val="Base"/>
              <w:rPr><w:i/></w:rPr>
            </w:style>
            """;
        const string defaults = """
            <w:docDefaults><w:rPrDefault><w:rPr><w:lang w:val="fr-FR"/></w:rPr></w:rPrDefault></w:docDefaults>
            """;
        const string mode14 = """
            <w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="14"/>
            """;
        const string select = """<w:pStyle w:val="Derived"/>""";
        string alternate = normal.Replace("w:default=\"on\"", "w:default=\"1\"", StringComparison.Ordinal)
            .Replace("<w:b/>", """<w:b w:val="true"/>""", StringComparison.Ordinal)
            .Replace("""<w:next w:val="Derived"/><w:autoRedefine/>""", """<w:next w:val="Base"/>""", StringComparison.Ordinal);
        string ordinary = defaults + normal + derived;
        string auxiliary = defaults + alternate + derived;
        var input = Fixture("styles-provenance", ordinary, auxiliary, mode14);
        var frozen = ManifestJson.Serialize(input.Manifest);
        using var good = PageComposer.Compose(input);
        var s = good.Snapshot;
        Complete(s, 1);
        var semantic = s.Semantic!;
        var selected = semantic.OrdinaryStyles;
        var effects = semantic.EffectsStyles;
        Check(s.StyleDecision is {Selection:PageStyleSelection.Equivalent,SelectedRoot:{} evidence} &&
            evidence == semantic.Source(selected.RootId!) && evidence.Location.PartUri == "/word/canonical.xml" &&
            selected.Relations.Single().Id == "ordinary" && effects.Relations.Single().Id == "effects" &&
            effects.DefaultsId is not null && semantic.Source(effects.DefaultsId).Location.PartUri == "/word/archive.xml",
            "Le choix doit suivre les relations OPC et préserver les parties d’origine.");
        Check(semantic.StyleCompatibility is {Mode:14,SettingsRelation.Id:"compat"} &&
            semantic.Source(semantic.StyleCompatibility.DeclarationIds.Single()).Location.PartUri == "/word/settings.xml",
            "Réglage de compatibilité et source.");
        foreach (var collection in new[] { selected, effects })
        {
            var first = collection.Definitions.Single(d => d.DeclaredId == "Base");
            var last = collection.Definitions.Single(d => d.DeclaredId == "Derived");
            Check(first.IsDefault && last.ParentId == first.Id &&
                semantic.StyleUse(last.Id) is {Resolved:true} chain && chain.BaseToDerivedIds.SequenceEqual(new[]{first.Id,last.Id}),
                "Les chaînes des collections doivent rester indépendantes.");
        }
        Check(semantic.ChildId(selected.Definitions[0].Id,"autoRedefine") is not null &&
            semantic.ChildId(effects.Definitions[0].Id,"autoRedefine") is null,
            "Métadonnées d’édition conservées dans la bonne partie.");
        var line = Line(s,"Premier");
        string spacingId = semantic.PresentationDeclarations(line.ParagraphId)
            .Select(id=>semantic.ChildId(id,"spacing")).OfType<string>().Single();
        var inherited = line.Native!.Parts.First(p=>p.Fragment is not null).Fragment!;
        Check(inherited.Style is {Size:12,Bold:true,Italic:false,Language:"fr-FR",Color:"315075"} &&
            inherited.Style.Origins["size"].Location.PartUri == "/word/canonical.xml" &&
            inherited.Style.Origins["colorThemeValue"].Location.PartUri == "/word/theme1.xml" &&
            inherited.Request is {Theme:true,Value:"minorAscii"} && inherited.ThemeSource?.PartUri == "/word/theme1.xml" &&
            inherited.RequestedFamily=="DejaVu Serif" && inherited.Measure?.Metrics.Width>0 &&
            semantic.Source(spacingId).Location.PartUri == "/word/canonical.xml" &&
            semantic.Attribute(spacingId,"line")=="280" && semantic.Attribute(spacingId,"after")=="120",
            "Police, couleur, métriques et provenance du style par défaut résolu.");
        var header = s.Composition.Pages[0].RunningStories.Single().Lines.Single();
        Check(header.Native!.Parts.Any(p=>p.Fragment?.Style is {Bold:true,Italic:true,Color:"315075"}) &&
            header.Source.PartUri == "/word/header1.xml","Cascade dérivée dans l’histoire de page.");
        VerifyPlaced(s);
        TypographyCases.Verify(semantic,s.Typography!);
        byte[] raster = Composer.PagePng(s.Composition.Pages[0], 2);
        using var image = SKBitmap.Decode(raster);
        Check(image.Pixels.Any(pixel=>pixel.Red==0x31&&pixel.Green==0x50&&pixel.Blue==0x75), "Couleur thématique réellement dessinée.");
        File.WriteAllBytes(Path.Combine(root,"style-theme.png"), raster);

        using var larger = PageComposer.Compose(Fixture("styles-size",
            Change(ordinary,"val=\"24\"","val=\"38\""), Change(auxiliary,"val=\"24\"","val=\"38\"")));
        Complete(larger.Snapshot, 1);
        Check(Line(larger.Snapshot,"Premier").Native!.Advance > line.Native.Advance &&
            Line(larger.Snapshot,"Premier").Native!.Ascent > line.Native.Ascent &&
            larger.Snapshot.Composition.Pages[0].RunningStories.Single().Lines.Single().Native!.Advance > header.Native.Advance,
            "Une taille héritée différente doit modifier la mesure et la position du corps et de l’en-tête.");
        using var spacing = PageComposer.Compose(Fixture("styles-spacing",
            Change(ordinary,"after=\"120\" w:line=\"280\"","after=\"280\" w:line=\"500\""),
            Change(auxiliary,"after=\"120\" w:line=\"280\"","after=\"280\" w:line=\"500\"")));
        Complete(spacing.Snapshot, 1);
        Check(Line(spacing.Snapshot,"Premier").Height > line.Height &&
            Line(spacing.Snapshot,"Second").Baseline > Line(s,"Second").Baseline,
            "Recalcul de l’interligne et de l’espace entre les paragraphes.");
        using var light = PageComposer.Compose(Fixture("styles-weight",
            Change(ordinary,"<w:b/>","<w:b w:val=\"off\"/>"),
            Change(auxiliary,"<w:b w:val=\"true\"/>","<w:b w:val=\"0\"/>")));
        Complete(light.Snapshot, 1);
        var lightFragment = Line(light.Snapshot,"Premier").Native!.Parts.First(p=>p.Fragment is not null).Fragment!;
        Check(!lightFragment.Style.Bold && lightFragment.Measure!.Metrics.Width!=inherited.Measure!.Metrics.Width &&
            !raster.AsSpan().SequenceEqual(Composer.PagePng(light.Snapshot.Composition.Pages[0],2)),
            "La graisse modifie la mesure et le raster.");

        using var divergent = PageComposer.Compose(Fixture("styles-profile-15",ordinary,
            Change(auxiliary,"val=\"24\"","val=\"38\""),Change(mode14,"val=\"14\"","val=\"15\"")));
        Check(divergent.Snapshot.Coverage==PageCoverage.Partial &&
            divergent.Snapshot.StyleDecision is {Selection:PageStyleSelection.Unresolved,SelectedRoot.Location.PartUri:"/word/canonical.xml"} &&
            divergent.Snapshot.Semantic!.StyleCompatibility.Mode==15 &&
            divergent.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&i.Message.Contains("sz",StringComparison.Ordinal)) &&
            Composer.PagePng(divergent.Snapshot.Composition.Pages.Single(),2).AsSpan().SequenceEqual(raster),
            "Le mode 15 n’établit aucune priorité et la géométrie candidate garde le style ordinaire.");
        using var missing = PageComposer.Compose(Fixture("styles-toggle-absent",ordinary,Change(auxiliary,"<w:b w:val=\"true\"/>","")));
        Complete(missing.Snapshot,1);
        Check(missing.Snapshot.StyleDecision?.Selection==PageStyleSelection.OrdinaryWithPlainCopy&&
            missing.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&!i.BlocksRendering),
            "L’absence reste une différence et la copie simple ne change pas la graisse ordinaire.");
        const string ligatures="""
            <w14:ligatures xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" w14:val="standard"/>
            """;
        using var activeCompatibility = PageComposer.Compose(Fixture("styles-mc-active",
            Change(ordinary,"<w:b/>","<w:b/>"+ligatures),
            Change(auxiliary,"<w:b w:val=\"true\"/>","<w:b w:val=\"true\"/>"+ligatures)));
        Check(activeCompatibility.Snapshot.Coverage!=PageCoverage.Complete&&activeCompatibility.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_DEFAULTS"),"Le contexte de compatibilité d’une extension utilisée doit rester bloquant.");
        using var twoDefaults = PageComposer.Compose(Fixture("styles-duplicate-default",ordinary+
            """<w:style w:type="paragraph" w:default="true" w:styleId="Autre"/>""",auxiliary));
        Check(twoDefaults.Snapshot.Coverage!=PageCoverage.Complete&&twoDefaults.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_ORDINARY_INVALID"&&i.Source?.PartUri=="/word/canonical.xml"),
            "Deux styles par défaut ne doivent pas être départagés arbitrairement.");
        using var ambiguous = PageComposer.Compose(Fixture("styles-mode-ambiguous",ordinary,auxiliary,mode14+mode14));
        Check(ambiguous.Snapshot.Coverage!=PageCoverage.Complete&&ambiguous.Snapshot.Semantic?.StyleCompatibility is {Mode:null,DeclarationIds.Length:2} &&
            ambiguous.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_COMPATIBILITY_UNRESOLVED"),
            "Deux réglages de compatibilité ne constituent pas une preuve de sélection.");
        using var cycle = PageComposer.Compose(Fixture("styles-cycle",ordinary,
            defaults+alternate.Replace("<w:pPr>","<w:basedOn w:val=\"Derived\"/><w:pPr>",StringComparison.Ordinal)+derived));
        Check(cycle.Snapshot.Coverage==PageCoverage.Partial&&cycle.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_USE_UNRESOLVED"&&i.Source?.PartUri=="/word/archive.xml"),
            "Chaîne auxiliaire cyclique rattachée à sa partie.");
        const string linked="""
            <w:style w:type="character" w:styleId="BaseChar"><w:link w:val="Base"/>
              <w:rPr><w:sz w:val="46"/></w:rPr></w:style>
            """;
        string linkedOrdinary=Change(ordinary,"<w:next","<w:basedOn w:val=\"Absent\"/><w:next")+
            linked;
        string linkedAuxiliary=Change(auxiliary,"<w:next","<w:basedOn w:val=\"BaseChar\"/><w:next")+
            linked;
        using var metadata=PageComposer.Compose(Fixture("styles-link-and-missing-parent",linkedOrdinary,linkedAuxiliary));
        var linkedSnapshot=metadata.Snapshot;
        Complete(linkedSnapshot,1);
        var linkedModel=linkedSnapshot.Semantic!;
        var character=linkedModel.OrdinaryStyles.Definitions.Single(d=>d.DeclaredId=="BaseChar");
        var baseStyle=linkedModel.OrdinaryStyles.Definitions.Single(d=>d.DeclaredId=="Base");
        Check(character.LinkedStyle?.TargetId==baseStyle.Id&&baseStyle.NextStyle?.TargetId==
            linkedModel.OrdinaryStyles.Definitions.Single(d=>d.DeclaredId=="Derived").Id&&baseStyle.ParentId is null&&
            linkedSnapshot.Composition.Issues.Any(i=>i.Code=="SEMANTIC_STYLE_PARENT_IGNORED")&&
            Composer.PagePng(linkedSnapshot.Composition.Pages[0],2).AsSpan().SequenceEqual(raster),
            "Liens et style suivant conservés avec leur source. Un parent absent ou de type incompatible ne contribue pas au rendu.");
        const string latent="""
            <w:latentStyles><w:lsdException w:name="Absent" w:qFormat="1"/></w:latentStyles>
            """;
        string extra=Paragraph(Run("Absent")+"""
            <w:r><w:rPr><w:rStyle w:val="Absent"/></w:rPr><w:t xml:space="preserve"> ordinaire</w:t></w:r>
            ""","""<w:pStyle w:val="Absent"/>""")+
            Paragraph("""<w:r><w:rPr><w:rStyle w:val="BaseChar"/></w:rPr><w:t>Associé</w:t></w:r>""");
        using var declared=PageComposer.Compose(Fixture("styles-latent-et-caractere",
            latent+linkedOrdinary,linkedAuxiliary,extra:extra));
        Complete(declared.Snapshot,1);
        Check(declared.Snapshot.Semantic!.StyleUses.Count(u=>u.MissingReferenceIgnored)==2&&
            Line(declared.Snapshot,"Absent ordinaire").Native!.Parts.First(p=>p.Fragment is not null).Fragment!.Style.Size==12&&
            Line(declared.Snapshot,"Associé").Native!.Parts.First(p=>p.Fragment is not null).Fragment!.Style.Size==23,
            "Le style de caractère est appliqué par sa référence directe. La déclaration latente conserve ses métadonnées.");
        const string onlyEffects="""<w:style w:type="paragraph" w:styleId="Absent"><w:rPr><w:sz w:val="34"/></w:rPr></w:style>""";
        using var conflict=PageComposer.Compose(Fixture("styles-utilise-uniquement-auxiliaire",
            latent+linkedOrdinary,linkedAuxiliary+onlyEffects,extra:extra));
        Check(conflict.Snapshot.Coverage==PageCoverage.Partial&&conflict.Snapshot.Composition.Issues.Any(i=>
            i.Code=="PAGE_STYLES_EFFECTS_ONLY_STYLE"&&i.Source?.PartUri=="/word/archive.xml"),
            "Une définition auxiliaire active empêche de conclure à l’équivalence des cascades.");
        const string defaultsTrue="""
            <w:docDefaults><w:rPrDefault><w:rPr><w:b/></w:rPr></w:rPrDefault></w:docDefaults>
            """;
        const string parityStyle="""
            <w:style w:type="paragraph" w:default="1" w:styleId="Base">
            <w:rPr><w:b/></w:rPr></w:style>
            <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/><w:rPr><w:b/></w:rPr></w:style>
            """;
        using var parity=PageComposer.Compose(Fixture("styles-toggle-deux-niveaux",defaultsTrue+parityStyle,
            defaultsTrue+parityStyle,extra:Paragraph("""
                <w:r><w:rPr><w:b w:val="0"/></w:rPr><w:t>Direct</w:t></w:r>
                """,select)));
        Complete(parity.Snapshot,1);
        Check(!Bold("Premier")&&Bold("Second")&&!Bold("Direct"),
            "La valeur par défaut vraie subit l’inversion des niveaux de style. La déclaration directe est absolue.");
        bool Bold(string value)=>Line(parity.Snapshot,value).Native!.Parts.First(p=>p.Fragment is not null).Fragment!.Style.Bold;
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            string output = Path.Combine(root,"refus-style-"+format);
            Check(!DocumentExports.Preflight(input,format).Allowed,"Profil d’export étendu sans qualification.");
            try
            {
                DocumentExports.Export(input,format,output);
                throw new InvalidOperationException("Écriture de l’export stylé.");
            }
            catch(InvalidDataException e) when(e.Message=="EXPORT_PREFLIGHT_BLOCKED") {}
            Check(!Path.Exists(output)&&!Directory.EnumerateDirectories(root,".lecteur-export-*").Any(),
                "Un refus avant export ne doit produire aucun fichier.");
        }
        Check(frozen.AsSpan().SequenceEqual(ManifestJson.Serialize(input.Manifest)), "Inventaire JSON v1 changé.");
        return;

        static string Change(string value,string before,string after)=>value.Replace(before,after,StringComparison.Ordinal);

        InspectionResult Fixture(string name,string first,string second,string compatibility=mode14,string extra="")
        {
            const string headerRef="""<w:headerReference w:type="default" r:id="head"/>""";
            const string theme="""
                <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:themeElements>
                <a:clrScheme><a:accent1><a:srgbClr val="315075"/></a:accent1></a:clrScheme>
                <a:fontScheme><a:minorFont><a:latin typeface="DejaVu Serif"/></a:minorFont></a:fontScheme>
                </a:themeElements></a:theme>
                """;
            string section=Section(width:5000,height:5400,margins:900).Replace("<w:sectPr>","<w:sectPr>"+headerRef,StringComparison.Ordinal);
            var parts=Fixtures.Parts(P("Premier")+P("Second",select)+extra+section,
                new()
                {
                    ["word/canonical.xml"]=Fixtures.Xml("styles",first),
                    ["word/archive.xml"]=Fixtures.Xml("styles",second).Replace("<w:styles ", """
                        <w:styles xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml" mc:Ignorable="w14"
                        """+" ",StringComparison.Ordinal),
                    ["word/settings.xml"]=Fixtures.Xml("settings","<w:compat>"+compatibility+"</w:compat>"),
                    ["word/theme1.xml"]=theme,
                    ["word/header1.xml"]=Fixtures.Xml("hdr",P("Titre courant",select))
                },
                Fixtures.Relationship("ordinary","styles","canonical.xml")+
                $"""<Relationship Id="effects" Type="{LayoutReader.StylesEffectsRelation}" Target="archive.xml"/>"""+
                Fixtures.Relationship("compat","settings","settings.xml")+
                Fixtures.Relationship("theme","theme","theme1.xml")+
                Fixtures.Relationship("head","header","header1.xml"));
            string types=Encoding.UTF8.GetString(parts["[Content_Types].xml"]);
            parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(types
                .Replace("wordprocessingml.canonical+xml","wordprocessingml.styles+xml",StringComparison.Ordinal)
                .Replace("application/vnd.openxmlformats-officedocument.wordprocessingml.archive+xml",
                    "application/vnd.ms-word.stylesWithEffects+xml",StringComparison.Ordinal)
                .Replace("wordprocessingml.theme1+xml","theme+xml",StringComparison.Ordinal));
            return DocumentInspector.InspectDocument(Fixtures.Write(root,name+".docx",parts));
        }
    }
}
