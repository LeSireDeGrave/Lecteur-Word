using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void CalculatedFields(string root)
    {
        const string bookmark="""<w:bookmarkStart w:id="7" w:name="cible"/><w:bookmarkEnd w:id="7"/>""";
        const string footer="""<w:footerReference w:type="default" r:id="foot"/>""";
        string section=Section(5500,6000,margins:900).Replace("<w:sectPr>","<w:sectPr>"+footer,StringComparison.Ordinal);
        string restarted=section.Replace("</w:sectPr>","<w:pgNumType w:start=\"9\"/></w:sectPr>",StringComparison.Ordinal);
        string body=P("Début",section)+Paragraph(bookmark+Run("Destination"))+restarted;
        foreach(var (command,expected) in new (string,string[])[]
            {("PAGE",["1","9"]),("NUMPAGES",["2","2"]),("PAGEREF cible",["9","9"])})
        {
            var input=Input(command,$"""<w:fldSimple w:instr="{command}"/>""");
            using var lease=PageComposer.Compose(input);
            var s=lease.Snapshot;
            Complete(s,2);
            var displayed=s.Composition.Pages.Select(p=>p.RunningStories.Single().Lines.Single().Native!.Generated.Single()).ToArray();
            Check(displayed.Select(d=>d.Fragment.Text).SequenceEqual(expected)&&
                displayed.All(d=>d.Kind=="pageField"&&d.Cause.Span is null&&d.Cause.Location.PartUri=="/word/footer1.xml"&&
                    d.Fragment.Measure is {Metrics.Width:>0}),"Valeur mesurée, provenance et numérotation "+command);
            Check(s.Composition.Pages.All(p=>p.RunningStories.Single().Lines.Single().Text==""),
                "La valeur produite reste hors du texte source.");
            Check(Enum.GetValues<ExportFormat>().All(f=>!DocumentExports.Preflight(input,f).Allowed),"Profil d’export inchangé.");
        }
        using var absent=PageComposer.Compose(Input("absent", """<w:fldSimple w:instr="PAGEREF absent"/>"""));
        Check(absent.Snapshot.Coverage==PageCoverage.Partial&&absent.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_FIELD_BOOKMARK_UNRESOLVED"),
            "Renvoi sans signet.");
        using var linked=PageComposer.Compose(Input("linked", """<w:fldSimple w:instr="PAGEREF cible \h"/>"""));
        Check(linked.Snapshot.Coverage==PageCoverage.Partial&&linked.Snapshot.Gaps.Any(g=>g.Reason=="PAGE_STORY_FIELD_UNRESOLVED"),
            "Le lien du champ auxiliaire reste à qualifier.");
        using var locked=PageComposer.Compose(Input("locked", """<w:fldSimple w:instr="NUMPAGES" w:fldLock="1"><w:r><w:t>99</w:t></w:r></w:fldSimple>"""));
        Complete(locked.Snapshot,2);
        Check(locked.Snapshot.Composition.Pages.All(p=>p.RunningStories.Single().Lines.Single().Text=="99"),"Cache verrouillé conservé.");
        using var pending=PageComposer.Compose(Input("cached", """<w:fldSimple w:instr="NUMPAGES"><w:r><w:t>99</w:t></w:r></w:fldSimple>"""));
        Check(pending.Snapshot.Coverage==PageCoverage.Partial&&
            pending.Snapshot.Composition.Pages.All(p=>p.RunningStories.Single().Lines.Single().Text=="99"),
            "Cache non verrouillé conservé avec diagnostic.");

        InspectionResult Input(string name,string field)=>DocumentInspector.InspectDocument(
            Fixtures.Write(root,name.Replace(' ','_')+".docx",Fixtures.Parts(body,
                new(){["word/footer1.xml"]=Fixtures.Xml("ftr",Paragraph(field))},
                Fixtures.Relationship("foot","footer","footer1.xml"))));
    }
}
