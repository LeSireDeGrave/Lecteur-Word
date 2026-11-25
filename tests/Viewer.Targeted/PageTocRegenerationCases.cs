using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LecteurWord;
using LecteurWord.Flow;

static partial class PageCases
{
    internal static void RegeneratedToc(string root)
    {
        string? navigation=null;
        foreach(bool complex in new[]{false,true})
        {
            string path=Fixture(complex,"TOC \\o &quot;1-2&quot; \\h");
            navigation=path;
            using var lease=Compose(path);
            var s=lease.Snapshot;
            Complete(s,3);
            var rows=s.Pages.SelectMany(p=>p.Geometry.Lines).Where(l=>l.Native?.TocRow is not null).ToArray();
            Check(rows.Select(l=>l.Text.TrimEnd('\t')).SequenceEqual(new[]{"Chapitre Écho","Sous-partie","Chapitre Bis"}),
                "Les styles personnalisés et hérités sélectionnent les titres dans l'ordre. Le niveau direct seul est exclu.");
            Check(rows.Select(l=>l.Native!.TocRow!.Level).SequenceEqual(new[]{1,2,1})&&
                rows[1].X>rows[0].X&&rows.All(l=>l.Native!.Tabs.Single().Stop.Kind==LineTabKind.Right),
                "Styles TOC par niveau et points de suite mesurés.");
            Check(rows.Select(l=>l.Native!.Generated.Single(g=>g.Kind=="pageField").Fragment.Text)
                .SequenceEqual(new[]{"vii","vii","viii"}),"Le sommaire utilise la numérotation de chaque destination physique.");
            Check(s.FieldStability=="Stable"&&s.FieldPasses.Length==3&&rows.All(r=>
            {
                var row=r.Native!.TocRow!;
                var glyphs=r.Native.Generated.Single(g=>g.Kind=="pageField");
                return glyphs.Cause==row.Field&&s.PageLinkTargets[row.Id]==row.Destination.NodeId&&
                    s.FieldBookmarks[row.Destination.NodeId].ParagraphId==row.Destination.NodeId;
            }),"Le champ, le résultat dérivé et les positions de titres conservent leurs identités.");
            Check(s.Semantic!.Fields.Count(f=>f.Command=="PAGEREF")==1&&
                Line(s,"Référence\t").Native!.Generated.Single(g=>g.Kind=="pageField").Fragment.Text=="7"&&
                s.Pages.Select(p=>p.Geometry.RunningStories.Single().Lines.Single().Native!.Generated.Single().Fragment.Text)
                    .SequenceEqual(new[]{"1","7","8"}),"Interaction avec PAGEREF explicite, PAGE et redémarrage romain.");
            Check(s.Semantic.Fields.Single(f=>f.Command=="TOC").StoredResultIds.IsEmpty&&
                s.Typography!.Paragraphs.All(p=>p.TocRow is null),
                "Les cellules de source et le champ d’entrée restent intacts.");
            Check(Composer.PagePng(s.Composition.Pages[0]).Length>0,"Raster natif du sommaire généré.");
        }

        foreach(var (instruction,labels) in new[]
        {
            ("TOC \\t &quot;Nom du chapitre;2&quot;",new[]{"Chapitre Écho","Chapitre Bis"}),
            ("TOC \\u",new[]{"Niveau direct seul"}),
            ("TOC \\o &quot;1-2&quot; \\z",new[]{"Chapitre Écho","Sous-partie","Chapitre Bis"}),
            ("TOC \\t &quot;Nom du chapitre,1&quot; \\o &quot;2-2&quot;",new[]{"Chapitre Écho","Sous-partie","Chapitre Bis"})
        })
        {
            using var selected=Compose(Fixture(true,instruction));
            Complete(selected.Snapshot,3);
            var rows=selected.Snapshot.Composition.Pages.SelectMany(p=>p.Lines).Where(l=>l.Native?.TocRow is not null).ToArray();
            Check(rows.Select(l=>l.Text.TrimEnd('\t')).SequenceEqual(labels)&&rows.All(l=>
                !selected.Snapshot.PageLinkTargets.ContainsKey(l.Native!.TocRow!.Id)),"Sélecteur de styles explicites ou de plan direct.");
            if(instruction.EndsWith("\\z",StringComparison.Ordinal))
                Check(rows.All(l=>l.Native!.Tabs.Single().Stop.Leader=="dot"&&
                    l.Native.Generated.Any(g=>g.Kind=="pageField")),"Le commutateur Web conserve taquets et numéros en vue paginée.");
        }

        using(var spanning=Compose(Fixture(true,"TOC \\o &quot;1-2&quot; \\h",split:true)))
        {
            var s=spanning.Snapshot;
            Complete(s,3);
            var field=s.Semantic!.Fields.Single(f=>f.Command=="TOC");
            Check(field.EndId is not null&&field.StoredResultIds.IsEmpty&&
                s.Semantic.Source(field.Id).Location!=s.Semantic.Source(field.EndId).Location&&
                s.Composition.Pages.SelectMany(p=>p.Lines).Count(l=>l.Native?.TocRow is not null)==3&&
                s.FieldPasses[^1].Fields.TakeLast(3).Select(f=>f.Text).SequenceEqual(new[]{"vii","vii","viii"}),
                "Les marqueurs répartis entre paragraphes ne produisent pas de lignes vides ni de destinations artificielles.");
        }
        using(var mixed=Compose(Fixture(true,"TOC \\o &quot;1-2&quot; \\h",split:true,foreign:true)))
            Check(mixed.Snapshot.Coverage!=PageCoverage.Complete&&mixed.Snapshot.Composition.Issues.Any(i=>
                i.BlocksRendering&&i.Code=="PAGE_FIELD_PROFILE_UNQUALIFIED"),"Un résultat mêlé de texte source conserve son diagnostic.");

        using var invalid=Compose(Fixture(true,"TOC \\o &quot;1-2&quot; \\f C"));
        Check(invalid.Snapshot.Coverage!=PageCoverage.Complete&&invalid.Snapshot.Composition.Issues.Any(i=>
            i.BlocksRendering&&i.Code=="PAGE_FIELD_PROFILE_UNQUALIFIED"),
            "Une combinaison de sélecteurs hors profil reste explicite.");

        string many=Paragraph("""<w:fldSimple w:instr="TOC \o &quot;1-1&quot; \h"/>""")+
            string.Concat(Enumerable.Range(1,28).Select(i=>P($"Entrée {i:00}","""<w:pStyle w:val="Titre"/>""")))+
            Section(width:5000,height:3200,margins:400);
        using(var longToc=Compose(Fixtures.Write(root,"toc-plusieurs-feuilles.docx",Fixtures.Parts(many,
            new(){["word/styles.xml"]=Fixtures.Xml("styles","""
                <w:style w:type="paragraph" w:styleId="Titre"><w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style>
                """)},Fixtures.Relationship("styles","styles","styles.xml")))))
        {
            var s=longToc.Snapshot;
            Complete(s);
            var tocPages=s.Composition.Pages.Where(p=>p.Lines.Any(l=>l.Native?.TocRow is not null)).ToArray();
            Check(tocPages.Length>1&&tocPages.Sum(p=>p.Lines.Count(l=>l.Native?.TocRow is not null))==28&&
                s.FieldPasses[^1].Fields.All(f=>f.PhysicalPage>=2),
                "Les entrées prennent leur place et décalent leurs propres destinations physiques.");
        }

        ValidateNavigation(navigation!);

        string Fixture(bool complex,string instruction,bool split=false,bool foreign=false)
        {
            string field=complex?
                $"""<w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> {instruction} </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>""":
                $"""<w:fldSimple w:instr="{instruction}"/>""";
            if(split)
                field=field.Replace("""<w:r><w:fldChar w:fldCharType="end"/></w:r>""",
                    "</w:p><w:p>"+(foreign?"""<w:r><w:tab/></w:r>""":"<w:r><w:t/></w:r>")+
                    """</w:p><w:p><w:r><w:fldChar w:fldCharType="end"/></w:r>""",StringComparison.Ordinal);
            const string styles="""
                <w:style w:type="paragraph" w:styleId="BaseChapitre">
                  <w:pPr><w:outlineLvl w:val="0"/></w:pPr>
                </w:style>
                <w:style w:type="paragraph" w:styleId="Chapitre"><w:name w:val="Nom du chapitre"/><w:basedOn w:val="BaseChapitre"/></w:style>
                <w:style w:type="paragraph" w:styleId="Rubrique"><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
                <w:style w:type="paragraph" w:styleId="TOC1">
                  <w:pPr><w:tabs><w:tab w:val="right" w:leader="dot" w:pos="3900"/></w:tabs></w:pPr>
                </w:style>
                <w:style w:type="paragraph" w:styleId="TOC2"><w:pPr><w:ind w:left="200"/></w:pPr></w:style>
                """;
            string first=Section(width:5500,height:6800,margins:800).Replace("<w:sectPr>",
                """<w:sectPr><w:footerReference w:type="default" r:id="foot"/>""",StringComparison.Ordinal);
            string following=Section(width:5500,height:6800,margins:800).Replace("</w:sectPr>",
                """<w:pgNumType w:start="7" w:fmt="lowerRoman"/></w:sectPr>""",StringComparison.Ordinal);
            string body=Paragraph(field)+P("Niveau direct seul","""<w:outlineLvl w:val="0"/>""")+
                Paragraph(Run("Référence")+"""<w:r><w:tab/></w:r><w:fldSimple w:instr="PAGEREF debut \* Arabic"/>""",first)+
                Paragraph("""<w:bookmarkStart w:id="1" w:name="debut"/><w:bookmarkEnd w:id="1"/>"""+Run("Chapitre Écho"),
                    """<w:pStyle w:val="Chapitre"/>""")+
                P("Sous-partie","""<w:pStyle w:val="Rubrique"/>""")+
                P("Chapitre Bis","""<w:pStyle w:val="Chapitre"/><w:pageBreakBefore/>""")+following;
            string key=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instruction)));
            return Fixtures.Write(root,"toc-sans-cache-"+complex+key[..12]+"-"+split+"-"+foreign+".docx",Fixtures.Parts(body,new()
            {
                ["word/styles.xml"]=Fixtures.Xml("styles",styles),
                ["word/footer1.xml"]=Fixtures.Xml("ftr",Paragraph("""<w:fldSimple w:instr="PAGE \* Arabic"/>"""))
            },Fixtures.Relationship("styles","styles","styles.xml")+Fixtures.Relationship("foot","footer","footer1.xml")));
        }
    }

    static void ValidateNavigation(string path)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false})
            .UseSkia().WithInterFont().SetupWithoutStarting();
        var window=new ReaderWindow{Width=1280,Height=900};
        window.Show();
        try
        {
            AboutCases.Complete(window.OpenDocumentAsync(path));
            var s=window.ReadingTableSnapshot!;
            Complete(s,3);
            var entry=s.Composition.Pages[0].Lines.First(l=>l.Native?.TocRow is not null);
            var destination=s.FieldBookmarks[entry.Native!.TocRow!.Destination.NodeId];
            foreach(var view in new[]{ReadingView.SinglePage,ReadingView.FacingPages})
            {
                if(view==ReadingView.FacingPages)
                    window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="ViewFacingPages")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                foreach(var rect in new[]{PageCoordinates.Clusters(entry).First().Rect,PageCoordinates.FieldLabels(entry).Single().Rect})
                {
                    window.GoToPage(0);window.SetZoom(.8);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
                    var page=window.GetVisualDescendants().OfType<Image>().Single(c=>c.Name=="CalculatedPage"&&Equals(c.Tag,1));
                    double scale=window.Zoom*96/72;
                    var point=page.TranslatePoint(new((rect.Left+rect.Width/2)*scale,(rect.Top+rect.Height/2)*scale),window);
                    Check(point is not null,"Rectangle du lien généré.");
                    window.MouseDown(point!.Value,MouseButton.Left);window.MouseUp(point.Value,MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    Check(window.CurrentReadingView==view&&window.CurrentPage==destination.Page&&window.CurrentBlockId==destination.ParagraphId,
                        "Le libellé et le numéro d’une entrée générée atteignent le titre source.");
                }
            }
        }
        finally{window.Close();}
    }
}
