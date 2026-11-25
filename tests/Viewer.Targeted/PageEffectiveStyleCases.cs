using System.Security.Cryptography;
using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void EffectiveStyles(string root)
    {
        const string basis = """
            <w:style w:type="paragraph" w:styleId="Base">
              <w:pPr><w:spacing w:before="200" w:after="140"/><w:ind w:left="80"/></w:pPr>
              <w:rPr><w:rFonts w:ascii="DejaVu Serif"/><w:sz w:val="22"/></w:rPr>
            </w:style>
            """;
        const string derived = """
            <w:style w:type="paragraph" w:styleId="Derived">
              <w:basedOn w:val="Base"/>
              <w:pPr><w:spacing w:before="100"/><w:ind w:left="120"/></w:pPr>
              <w:rPr><w:rFonts w:ascii="DejaVu Serif"/><w:sz w:val="24"/></w:rPr>
            </w:style>
            """;
        const string properties = """<w:pStyle w:val="Derived"/><w:spacing w:after="80"/>""";
        string alternate = basis.Replace("before=\"200\"", "before=\"400\"", StringComparison.Ordinal)
            .Replace("left=\"80\"", "left=\"360\"", StringComparison.Ordinal)
            .Replace("DejaVu Serif", "DejaVu Sans", StringComparison.Ordinal)
            .Replace("val=\"22\"", "val=\"38\"", StringComparison.Ordinal);
        string body = P("La première phrase se répartit sur plusieurs lignes dans la zone étroite.", properties)
            + P("Paragraphe suivant.", properties) + Section(width:4200, height:5200);
        foreach (int mode in new[] { 14, 15 })
        {
            using var same = Open($"mode-{mode}-identiques", body, basis + derived, basis + derived, mode);
            using var masked = Open($"mode-{mode}-masquees", body, basis + derived, alternate + derived, mode);
            Complete(same.Snapshot, 1);
            Complete(masked.Snapshot, 1);
            bool plainCopy=mode==14;
            var expectedPart=plainCopy?"/word/styles.xml":"/word/stylesWithEffects.xml";
            var selection=plainCopy?PageStyleSelection.OrdinaryWithPlainCopy:PageStyleSelection.EquivalentComposition;
            var selected = masked.Snapshot.Semantic!;
            var paragraph = masked.Snapshot.RulerModel!.Paragraphs[0];
            var fragment = masked.Snapshot.Typography!.Paragraphs[0].Fragments[0];
            Check(masked.Snapshot.StyleDecision?.Selection==selection&&
                masked.Snapshot.StyleDecision.SelectedRoot?.Location.PartUri==expectedPart&&
                selected.EffectiveStyles == (plainCopy?selected.OrdinaryStyles:selected.EffectsStyles) &&
                selected.Source(selected.StyleUse(selected.ChildId(selected.ChildId(paragraph.Id, "pPr")!, "pStyle")!)!
                    .BaseToDerivedIds[0]).Location.PartUri == expectedPart &&
                fragment.Style.Origins["size"].Location.PartUri == expectedPart &&
                fragment is { PointSize:12, RequestedFamily:"DejaVu Serif" } &&
                paragraph.Properties is { Before:5, After:4, Left:6 },
                "La décision doit atteindre les ancêtres, les propriétés d’indentation et d’espacement et le façonnage.");
            var page = masked.Snapshot.Pages.Single().Geometry;
            Check(page.Lines.Count>2 && Composer.PagePng(page).AsSpan().SequenceEqual(
                Composer.PagePng(same.Snapshot.Pages.Single().Geometry)) &&
                masked.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&!i.BlocksRendering),
                "Coupures et raster égaux, divergence XML toujours consultable.");

            using var live = Open($"mode-{mode}-divergentes", body, basis+derived,
                alternate+derived.Replace("val=\"24\"", "val=\"36\"", StringComparison.Ordinal), mode);
            Check(live.Snapshot.StyleDecision?.Selection==(plainCopy?selection:PageStyleSelection.Unresolved) &&
                live.Snapshot.Coverage==(plainCopy?PageCoverage.Complete:PageCoverage.Partial) &&
                live.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DIVERGENT"&&i.BlocksRendering==!plainCopy),
                "La divergence reste visible et bloque lorsque la collection ordinaire ne peut pas être sélectionnée.");
            using var absentCopy = Open($"mode-{mode}-copie-incomplete", body, basis+derived, basis, mode);
            Check(absentCopy.Snapshot.Coverage==(plainCopy?PageCoverage.Complete:PageCoverage.Partial)&&
                absentCopy.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_MISSING_STYLE"&&i.BlocksRendering==!plainCopy)&&
                absentCopy.Snapshot.Typography!.Paragraphs[0].Fragments[0].PointSize==12,
                "Style ordinaire utilisé et indisponible dans la copie.");
            using var missing = Open($"mode-{mode}-style-absent", body, basis, basis+derived, mode);
            Check(missing.Snapshot.Coverage==PageCoverage.Partial &&
                missing.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_ONLY_STYLE"&&i.BlocksRendering),
                "Un style disponible dans une seule collection active garde son diagnostic.");
        }

        const string defaults = """
            <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="36"/></w:rPr></w:rPrDefault></w:docDefaults>
            """;
        using var maskedDefaults = Open("styles-defaults-masques",body,defaults+basis+derived,
            defaults.Replace("val=\"36\"", "val=\"46\"", StringComparison.Ordinal)+basis+derived,15);
        Complete(maskedDefaults.Snapshot,1);
        Check(maskedDefaults.Snapshot.StyleDecision?.Selection==PageStyleSelection.EquivalentComposition &&
            maskedDefaults.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_DEFAULTS"&&!i.BlocksRendering) &&
            maskedDefaults.Snapshot.Typography!.Paragraphs[0].MarkStyle.Size==12,
            "Une taille documentaire surchargée jusque dans la marque de paragraphe a le même effet.");
        const string empty = """<w:style w:type="paragraph" w:styleId="Empty"><w:name w:val="Sans format"/></w:style>""";
        using var absent = Open("styles-absent-inerte",
            P("Texte ordinaire", """<w:pStyle w:val="Empty"/>""")+Section(),empty,"",15);
        Complete(absent.Snapshot,1);
        Check(absent.Snapshot.StyleDecision?.Selection==PageStyleSelection.EquivalentComposition &&
            absent.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_STYLES_EFFECTS_MISSING_STYLE"&&!i.BlocksRendering) &&
            absent.Snapshot.Semantic!.StyleUses.Any(u=>u.MissingReferenceIgnored),
            "Une référence sans propriétés de rendu subit la règle d’absence du style sans différence de composition.");

        const string ns = """
            xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
            xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"
            """;
        string nsTag = ns.ReplaceLineEndings(" ");
        string effectExtension=$"""<w14:ligatures {nsTag} mc:Ignorable="w14" w14:val="standard"/>""";
        string withEffect=alternate.Replace("</w:rPr>", effectExtension+"</w:rPr>",StringComparison.Ordinal);
        using var unresolvedEffect=Open("styles-14-extension-active",body,basis+derived,
            withEffect+derived,14);
        Check(unresolvedEffect.Snapshot.StyleDecision?.Selection==PageStyleSelection.Unresolved&&
            unresolvedEffect.Snapshot.Coverage==PageCoverage.Partial,
            "La présence d’une extension exclut la politique des copies simples.");
        string branch = $"""
            <mc:AlternateContent {nsTag}><mc:Choice Requires="w14">{alternate}</mc:Choice>
              <mc:Fallback>{basis}</mc:Fallback></mc:AlternateContent>
            """;
        string optional = $"""
            <mc:AlternateContent {nsTag}><mc:Choice Requires="w14">
              <w:style w:type="paragraph" w:styleId="Unused"/>
            </mc:Choice></mc:AlternateContent>
            """;
        string insideStyle = derived.Replace("""<w:pPr><w:spacing w:before="100"/><w:ind w:left="120"/></w:pPr>""",
            $"""<mc:AlternateContent {nsTag}><mc:Choice Requires="w"><w:pPr><w:spacing w:before="100"/><w:ind w:left="120"/></w:pPr></mc:Choice></mc:AlternateContent>""",
            StringComparison.Ordinal);
        using var mce = Open("styles-mce-avec-et-sans-repli", body, branch+optional+insideStyle, null, 15);
        Complete(mce.Snapshot, 1);
        var model = mce.Snapshot.Semantic!;
        Check(model.Styles.Select(d=>d.DeclaredId).SequenceEqual(new[]{"Base","Derived"}) &&
            model.StyleUse(model.Styles[1].Id)!.BaseToDerivedIds.SequenceEqual(model.Styles.Select(s=>s.Id)) &&
            model.Alternatives.Length==3 && model.Alternatives.Count(a=>a.SelectedBranchId is null)==1 &&
            model.Alternatives.SelectMany(a=>a.BranchIds).Count(id=>model.Element(id).Coverage==SemanticCoverage.InactiveAlternative)==2 &&
            mce.Snapshot.RulerModel!.Paragraphs[0].Properties is {Before:5,After:4,Left:6} &&
            mce.Snapshot.Composition.Issues.Any(i=>i.Code=="SEMANTIC_STYLE_ALTERNATIVE_EMPTY"),
            "La projection MCE doit résoudre les styles et propriétés actifs, avec inventaire de toutes les alternatives.");
        string invalid = optional.Replace("Requires=\"w14\"", "Requires=\"undeclared\"", StringComparison.Ordinal);
        using var bad = Open("styles-mce-invalide",body,basis+derived+invalid,null,15);
        Check(bad.Snapshot.Coverage!=PageCoverage.Complete &&
            bad.Snapshot.Composition.Issues.Any(i=>i.Code=="SEMANTIC_ALTERNATIVE_UNRESOLVED"&&i.BlocksRendering),
            "Une déclaration MCE invalide ne correspond pas à une projection vide autorisée.");
        using var inactive = Open("styles-mce-difference-inactive",body,branch+derived,
            branch.Replace("DejaVu Sans","DejaVu Sans Mono",StringComparison.Ordinal)+derived,15);
        Complete(inactive.Snapshot,1);
        Check(inactive.Snapshot.StyleDecision?.Selection==PageStyleSelection.Equivalent &&
            inactive.Snapshot.Semantic!.Alternatives.SelectMany(a=>a.BranchIds)
                .Count(id=>inactive.Snapshot.Semantic.Element(id).Coverage==SemanticCoverage.InactiveAlternative)==2,
            "Les deux charges inactives restent inventoriées et les replis donnent la même composition.");

        const string tableStyle = """
            <w:style w:type="table" w:styleId="TBase"><w:rPr><w:sz w:val="20"/></w:rPr></w:style>
            <w:style w:type="table" w:styleId="T">
              <w:basedOn w:val="TBase"/>
              <w:tblPr><w:tblStyleRowBandSize w:val="0"/></w:tblPr>
              <w:rPr><w:sz w:val="24"/></w:rPr>
              <w:tblStylePr w:type="firstCol"><w:tcPr>
                  <w:tcMar><w:left w:w="240" w:type="dxa"/></w:tcMar><w:vAlign w:val="bottom"/>
                </w:tcPr><w:rPr><w:sz w:val="30"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="firstRow"><w:rPr><w:sz w:val="40"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="lastCol"><w:rPr><w:sz w:val="42"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="band1Horz"><w:rPr><w:sz w:val="44"/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="wholeTable"><w:rPr><w:sz w:val="48"/></w:rPr></w:tblStylePr>
            </w:style>
            """;
        string table = """
            <w:tbl><w:tblPr><w:tblStyle w:val="T"/>
              <w:tblLook w:val="01E0" w:firstRow="0" w:firstColumn="1"/>
            </w:tblPr><w:tblGrid><w:gridCol w:w="1600"/><w:gridCol w:w="1600"/></w:tblGrid>
            <w:tr><w:tc><w:p><w:r><w:t>Gauche</w:t></w:r></w:p></w:tc>
            <w:tc><w:tcPr><w:vAlign w:val="top"/></w:tcPr><w:p><w:r><w:t>Droite</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """+Section(width:5800);
        using var styled = Open("styles-table-contexte",table,tableStyle,
            tableStyle.Replace("val=\"20\"", "val=\"28\"", StringComparison.Ordinal),15);
        Complete(styled.Snapshot,1);
        var cells = styled.Snapshot.RulerModel!.Tables.Single().Cells;
        var text = styled.Snapshot.Typography!.Paragraphs.Where(p=>p.Context.Length>0).ToArray();
        Check(cells[0] is {Margins.Left:12,VerticalAlignment:"bottom"} &&
            cells[1] is {Margins.Left:12,VerticalAlignment:"top"} &&
            cells[1].RowCellStyleProperties.Any(id=>styled.Snapshot.Semantic!.Name(id)==SemanticIndex.QName(SemanticIndex.W,"tcMar")) &&
            text[0].Fragments.Single().PointSize==15 && text[1].Fragments.Single().PointSize==12 &&
            text[1].Fragments.Single().Style.Origins["size"].Location.PartUri=="/word/stylesWithEffects.xml" &&
            styled.Snapshot.Composition.Issues.Any(i=>i.Code=="PAGE_TABLE_WHOLE_CONDITION_IGNORED"&&!i.BlocksRendering) &&
            Composer.PagePng(styled.Snapshot.Pages.Single().Geometry).Length>0,
            "Marges de rangée, alignement direct, texte de colonne, taille de bande nulle et masque tblLook Word.");

        PageCompositionLease Open(string name,string content,string ordinary,string? effects,int mode)
        {
            var parts = Fixtures.Parts(content,new()
            {
                ["word/styles.xml"]=Fixtures.Xml("styles",ordinary),
                ["word/settings.xml"]=Fixtures.Xml("settings",$"""
                    <w:compat><w:compatSetting w:name="compatibilityMode"
                    w:uri="http://schemas.microsoft.com/office/word" w:val="{mode}"/></w:compat>
                    """)
            },Fixtures.Relationship("st","styles","styles.xml")+Fixtures.Relationship("settings","settings","settings.xml")+
                (effects is null?"":$"""<Relationship Id="fx" Type="{LayoutReader.StylesEffectsRelation}" Target="stylesWithEffects.xml"/>"""));
            if(effects is not null)
            {
                parts["word/stylesWithEffects.xml"]=Encoding.UTF8.GetBytes(Fixtures.Xml("styles",effects));
                var types=Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>",
                    """<Override PartName="/word/stylesWithEffects.xml" ContentType="application/vnd.ms-word.stylesWithEffects+xml"/></Types>""",
                    StringComparison.Ordinal);
                parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(types);
            }
            string path=Fixtures.Write(root,name+".docx",parts);
            byte[] original=SHA256.HashData(File.ReadAllBytes(path));
            var lease=Compose(path);
            Check(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(original),"Le témoin DOCX a été modifié.");
            return lease;
        }
    }
}
