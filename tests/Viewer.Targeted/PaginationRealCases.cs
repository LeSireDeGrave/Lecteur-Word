using LecteurWord.DocxCore;
using LecteurWord.Flow;
using static LineCases;

static class PaginationRealCases
{
    internal static void Hyphenation(string root)
    {
        var patterns=new HyphenationPatterns();
        Check(patterns.TryGet("fr-FR","extraordinaire",()=>{},out var french)&&french.Length>1,
            "Le dictionnaire local français doit fournir plusieurs points.");
        Check(patterns.TryGet("en-US","extraordinary",()=>{},out var english)&&english.Length>1,
            "Le dictionnaire local anglais doit fournir plusieurs points.");
        Check(!patterns.TryGet("en-GB","extraordinary",()=>{},out _)&&!patterns.TryGet("und","extraordinaire",()=>{},out _),
            "Aucun repli linguistique implicite.");
        const string frenchText="extraordinaire extraordinaire extraordinaire";
        const string englishText="extraordinary extraordinary extraordinary";
        const string settings="""<w:autoHyphenation/><w:hyphenationZone w:val="0"/>""";
        var basic=Compose(settings,
            Run(frenchText,"fr-FR"),Run(englishText,"en-US"),Run(frenchText,null),Run(englishText,"en-GB"),
            Run(frenchText,"fr-FR","<w:suppressAutoHyphens/>"));
        Check(basic[0].Any(Automatic)&&basic[1].Any(Automatic),"Coupures composées dans les deux langues.");
        Check(basic[2].Any(l=>l.Diagnostics.Any(d=>d.Code=="LINE_HYPHENATION_LANGUAGE_UNRESOLVED"))&&
            basic[3].Any(l=>l.Diagnostics.Any(d=>d.Code=="LINE_HYPHENATION_RESOURCE_UNAVAILABLE"))&&
            basic[4].All(l=>!Automatic(l)&&l.Diagnostics.All(d=>!d.Code.StartsWith("LINE_HYPHENATION"))),
            "Langue non déclarée, langue hors profil et suppression locale.");
        string[] original=[frenchText,englishText,frenchText,englishText,frenchText];
        for(int i=0;i<basic.Length;i++)
        {
            var source=string.Concat(basic[i].SelectMany(l=>l.Parts).Select(p=>p.Text));
            Check(source==original[i],"Les fragments sélectionnables reconstruisent le texte sans trait ajouté.");
            foreach(var decoration in basic[i].SelectMany(l=>l.Generated).Where(d=>d.Kind=="automaticHyphen"))
                Check(decoration.Cause.Span is null&&decoration.Fragment.Sources.All(s=>s.Source.Span is null)&&
                    decoration.Fragment.Measure is {Metrics.Width:>0},"Trait mesuré et sans étendue UTF-16 documentaire.");
        }
        var capped=Compose(settings+"<w:doNotHyphenateCaps/><w:consecutiveHyphenLimit w:val=\"1\"/>",
            Run(frenchText.ToUpperInvariant(),"fr-FR"),Run(frenchText,"fr-FR"),Run("Anticonstitutionnellement","fr-FR"));
        Check(capped[0].All(l=>!Automatic(l))&&capped[2].Any(Automatic),"Majuscules intégrales et capitale initiale.");
        Check(capped[1].Any(Automatic)&&!capped[1].Zip(capped[1].Skip(1)).Any(pair=>Automatic(pair.First)&&Automatic(pair.Second)),
            "La limite de césures consécutives s’applique aux lignes obtenues.");
        var unavailable=Compose(settings+"""
            <w:compat><w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="15"/></w:compat>
            """,Run(frenchText,"fr-FR"));
        Check(unavailable[0].Any(l=>l.Diagnostics.Any(d=>d.Code=="LINE_HYPHENATION_COMPATIBILITY"))&&unavailable[0].All(l=>!Automatic(l)),
            "Compatibilité non qualifiée diagnostiquée.");
        var zoned=Compose("""<w:autoHyphenation/><w:hyphenationZone w:val="1600"/>""",Run(frenchText,"fr-FR"));
        Check(zoned[0].All(l=>!Automatic(l)),"Zone déclarée appliquée.");
        var controls=Compose(settings,Run("alpha\u00adbêta","fr-FR"),Run("alpha\u2011bêta","fr-FR"));
        Check(controls[0].Any(l=>l.Cut==LineCutKind.SoftHyphen)&&controls[1].All(l=>!Automatic(l)),"Traits explicites.");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        try{ComposeLines(settings,[Run(frenchText,"fr-FR")],cancelled.Token);throw new InvalidOperationException("Annulation ignorée.");}
        catch(OperationCanceledException){}

        static bool Automatic(ComposedLine line)=>line.Cut==LineCutKind.AutomaticHyphen;
        static string Run(string text,string? language,string properties="")=>
            "<w:p><w:pPr>"+properties+"</w:pPr><w:r><w:rPr>"+
            "<w:rFonts w:ascii=\"DejaVu Serif\" w:hAnsi=\"DejaVu Serif\"/><w:sz w:val=\"24\"/>"+
            (language is null?"":$"""<w:lang w:val="{language}"/>""")+"</w:rPr><w:t>"+text+"</w:t></w:r></w:p>";
        ComposedLine[][] Compose(string options,params string[] paragraphs)=>ComposeLines(options,paragraphs,default);
        ComposedLine[][] ComposeLines(string options,string[] paragraphs,CancellationToken token)
        {
            string path=Fixtures.Write(root,Guid.NewGuid().ToString("N")+".docx",
                Fixtures.Parts(string.Concat(paragraphs),new(){["word/settings.xml"]=Fixtures.Xml("settings",options)},
                    Fixtures.Relationship("settings","settings","settings.xml")));
            var input=DocumentInspector.InspectDocument(path);
            var model=TypographyCases.Model(input);
            using var typography=new TypographySession(input,model);
            var doc=typography.Measure();
            using var painter=new LineComposer(model,doc,typography);
            return painter.Compose(_=>new("test",0,(paragraphs.Length==2&&paragraphs[0].Contains('\u00ad')?39:110)*Pt),token)
                .Lines.GroupBy(l=>l.ParagraphId).Select(g=>g.ToArray()).ToArray();
        }
    }
}
