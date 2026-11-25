using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void NoteAndTableKeep(string root)
    {
        string section=Section(width:4000,height:4800,margins:600)
            .Replace("w:right=\"600\"","w:right=\"440\"",StringComparison.Ordinal);
        const string noteCall="""
            <w:r><w:rPr><w:sz w:val="24"/><w:vertAlign w:val="superscript"/></w:rPr><w:footnoteReference w:id="1"/></w:r>
            """;
        string body=Paragraph(Run("Recto")+"""<w:r><w:br w:type="page"/></w:r>""")+
            Paragraph(Run("Verso")+noteCall)+P("À garder","<w:keepNext/>")+P("Suite")+section;
        var noteParts=Fixtures.Parts(body,new()
        {
            ["word/settings.xml"]=Fixtures.Xml("settings","<w:mirrorMargins/>"),
            ["word/footnotes.xml"]=Fixtures.Xml("footnotes","""
                <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
                <w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>
                <w:footnote w:id="1"><w:p><w:r><w:footnoteRef/><w:t xml:space="preserve"> La note.</w:t></w:r></w:p></w:footnote>
                """)
        },Fixtures.Relationship("settings","settings","settings.xml")+Fixtures.Relationship("note","footnotes","footnotes.xml"));
        using var notes=Compose(Fixtures.Write(root,"mirrored-note.docx",noteParts));
        Complete(notes.Snapshot,2);
        var verso=notes.Snapshot.Composition.Pages[1];
        var mark=verso.Lines.SelectMany(l=>l.Native!.Generated).Single(d=>d.Kind=="noteNumber");
        Check(mark.Fragment is {Text:"1",PointSize:<12,Style.BaselineShift:>0}&&
            mark.Fragment.Measure!.Glyphs.All(g=>g.Y<0),"Mesure de la marque en exposant et provenance.");
        Check(verso.Footnotes is [var rule,var note]&&rule.Lines.Single().NoteRule is not null&&
            Close(rule.Lines.Single().NativeOffsetX,verso.Geometry.Left)&&rule.Region.Left==PageComposer.Emu(verso.Geometry.Left)&&
            note.Lines.Single().Native!.Generated.Any(d=>d.Kind=="noteNumber"&&d.Fragment.Text=="1")&&
            rule.Region.Top>PageComposer.Emu(verso.Lines.Max(l=>l.NativeTop+l.Height)),
            "Réserve, séparateur et numéro de note dans la marge alternée de la feuille.");

        const string prefix="""
            <w:tbl><w:tblPr><w:tblW w:type="dxa" w:w="2000"/><w:tblLayout w:type="fixed"/></w:tblPr>
              <w:tblGrid><w:gridCol w:w="1000"/><w:gridCol w:w="1000"/></w:tblGrid><w:tr><w:trPr><w:cantSplit/></w:trPr>
            """;
        foreach(bool both in new[]{true,false})
        {
            string table=prefix+Cell("Une",true)+Cell("Deux",both)+"</w:tr></w:tbl>";
            string content=P("Avant","""<w:spacing w:line="2000" w:lineRule="exact"/>""")+table+
                P("Légende","<w:keepNext/>")+P("Suite")+Section(height:2880);
            using var lease=Compose(Write(root,"table-keep-"+both,content));
            var s=lease.Snapshot;
            if(!both)
            {
                Check(s.Coverage!=PageCoverage.Complete&&s.Composition.Issues.Any(i=>i.Code=="LAYOUT_CELL_KEEP_NEXT_UNQUALIFIED"),
                    "Une ligne avec des propriétés contradictoires reste hors profil.");
                continue;
            }
            Complete(s,2);
            Check(s.Composition.Pages[0].Cells.Count==0&&s.Composition.Pages[1].Cells.Count==2&&
                s.Composition.Pages[1].Lines.TakeLast(2).Select(l=>l.Text).SequenceEqual(new[]{"Légende","Suite"}),
                "La dernière rangée et la chaîne qui suit passent ensemble à la feuille suivante.");
        }

        static string Cell(string text,bool keep)=>"<w:tc>"+P(text,keep?"<w:keepNext/>":"")+"</w:tc>";
    }

    internal static void StoryDocumentProperties(string root)
    {
        const string contents="""
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/custom-properties"
                xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <property fmtid="{D5CDD505-2E9C-101B-9397-08002B2CF9AE}" pid="2" name="Libellé"><vt:lpwstr>Valeur interne</vt:lpwstr></property>
            </Properties>
            """;
        foreach(string stored in new[]{"","Valeur périmée","Valeur interne"})
        {
            string field=$"""<w:fldSimple w:instr="DOCPROPERTY Libellé">{Run(stored)}</w:fldSimple>""";
            string complex=$"""
                <w:r><w:fldChar w:fldCharType="begin"/></w:r>
                <w:r><w:instrText> DOCPROPERTY "Libellé" </w:instrText></w:r>
                <w:r><w:fldChar w:fldCharType="separate"/></w:r>{(stored.Length==0?"":Run(stored))}
                <w:r><w:fldChar w:fldCharType="end"/></w:r>
                """;
            string section=Section(width:4200,height:4600,margins:800)
                .Replace("<w:sectPr>","""<w:sectPr><w:headerReference w:type="default" r:id="head"/>""",StringComparison.Ordinal);
            var parts=Fixtures.Parts(P("Corps")+Paragraph(Run("Propriété ")+field)+Paragraph(complex)+
                Paragraph("""<w:fldSimple w:instr="NUMPAGES"/>""")+section,
                new(){["word/header1.xml"]=Fixtures.Xml("hdr",Paragraph(field))},
                Fixtures.Relationship("head","header","header1.xml"));
            string index=Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>",
                """<Override PartName="/docProps/custom.xml" ContentType="application/vnd.openxmlformats-officedocument.custom-properties+xml"/></Types>""",
                StringComparison.Ordinal);
            string packageRelations=Encoding.UTF8.GetString(parts["_rels/.rels"]).Replace("</Relationships>",
                """
                <Relationship Id="properties" Target="docProps/custom.xml"
                    Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/custom-properties"/></Relationships>
                """,StringComparison.Ordinal);
            parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(index);
            parts["_rels/.rels"]=Encoding.UTF8.GetBytes(packageRelations);
            parts["docProps/custom.xml"]=Encoding.UTF8.GetBytes(contents);
            using var lease=Compose(Fixtures.Write(root,"docproperty-"+(stored=="Valeur interne"?"actuel":stored.Length.ToString())+".docx",parts));
            var s=lease.Snapshot;
            Complete(s,1);
            var line=s.Composition.Pages[0].RunningStories.Single().Lines.Single();
            Check(stored=="Valeur interne"?line.Text==stored&&line.Native!.Generated.IsEmpty&&
                s.Composition.Issues.Any(i=>i.Code=="PAGE_STORY_PROPERTY_VERIFIED"):
                line.Native!.Generated.Single().Fragment.Text=="Valeur interne",
                "Source OPC de la propriété appliquée, même avec un cache vide ou périmé.");
            Check(s.FieldStability=="Stable"&&s.FieldPasses[^1].Fields.Select(f=>f.Text)
                .SequenceEqual(new[]{"Valeur interne","Valeur interne","1"})&&
                s.Composition.Pages.Single().Lines.SelectMany(l=>l.Native!.Generated).Select(g=>g.Fragment.Text)
                    .SequenceEqual(new[]{"Valeur interne","Valeur interne","1"}),
                "Les champs simple et complexe du corps utilisent la même source et sont remesurés avec le total de pages.");
        }
    }
}
