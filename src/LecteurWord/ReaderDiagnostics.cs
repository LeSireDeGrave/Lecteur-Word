using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed record DiagnosticGroup(string Category,string Component,string Severity,string Code,int Count,bool BlocksReading,string Example);

public static class DiagnosticSummary
{
    public static IReadOnlyList<DiagnosticGroup> Group(InspectionResult inspection,IEnumerable<FlowIssue> layoutIssues,IEnumerable<FlowIssue> readingIssues,bool readable)
    {
        var entries=inspection.Manifest.Diagnostics.Select(d=>new DiagnosticGroup(
            d.Source?.PartUri is "/[Content_Types].xml" || d.Source?.PartUri.EndsWith(".rels",StringComparison.Ordinal)==true?"OPC":"DOCX",
            d.Source?.PartUri??"package",d.Severity,d.Code,1,!inspection.Manifest.InspectionComplete&&d.Severity=="error",d.Message))
            .Concat(layoutIssues.Select(i=>new DiagnosticGroup("Composition",i.Source?.PartUri??"document",i.BlocksRendering?"warning":"info",i.Code,1,!readable&&i.BlocksRendering,i.Message)))
            .Concat(readingIssues.Select(i=>new DiagnosticGroup("Lecture / Reading",i.Source?.PartUri??"document",i.BlocksRendering?"error":"warning",i.Code,1,i.BlocksRendering,i.Message)));
        return entries.GroupBy(d=>(d.Category,d.Component,d.Severity,d.Code,d.BlocksReading))
            .Select(g=>g.First() with {Count=g.Count()}).OrderByDescending(g=>g.BlocksReading).ThenBy(g=>g.Category,StringComparer.Ordinal).ThenBy(g=>g.Code,StringComparer.Ordinal).ToArray();
    }
}

public sealed partial class ReaderWindow
{
    void FillDiagnostics()
    {
        if(inspection is null)return;
        var readingIssues=readable?.Issues.Where(i=>i.Code!="READ_TABLE_APPROXIMATE").Concat(readable.Presentation.Issues)??[];
        var tableFallbacks=readable?.Presentation.BodyBlocks.Where(b=>b.Kind=="table"&&
            ReadingTableSnapshot?.ReadingTables.ContainsKey(b.Id)!=true).Select(b=>new FlowIssue("READ_TABLE_GRID_FALLBACK",b.Source,
                "Grille en lecture de secours ; certaines dimensions ou propriétés graphiques restent non qualifiées.",false,false)).ToArray()??[];
        var groups=DiagnosticSummary.Group(inspection,composition?.Issues??flow?.Issues??[],
            readingIssues.Concat(tableFallbacks),readable?.Available==true||composition?.Available==true);
        bool canRead=readable?.Available==true||composition?.Available==true;
        navItems.Children.Add(new TextBlock{Name="DiagnosticUserSummary",Text=canRead?T("Lecture possible","Reading available"):T("Lecture indisponible","Reading unavailable"),FontSize=17,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        navItems.Children.Add(Explain(canRead?T("Le contenu est consultable. Certains éléments utilisent une présentation adaptée.","The content is readable. Some elements use an adapted presentation."):T("Le traitement s’est arrêté. Le fichier d’origine reste intact ; consultez le rapport pour connaître la cause.","Processing stopped. The original file is intact; consult the report for the cause.")));
        if(readable is not null)
        {
            int notes=readable.Presentation.Notes.Values.Count(n=>n.Verified),uncertain=readable.Presentation.Notes.Values.Count(n=>!n.Verified),
                missing=readable.Presentation.Fields.Values.Count(f=>f.Name!="TC"&&!f.HasStoredValue);
            if(notes>0)navItems.Children.Add(Explain(notes+T(" appels de notes reliés à leur contenu."," note references linked to their content.")));
            if(uncertain>0)navItems.Children.Add(Explain(uncertain+T(" correspondances de notes restent à vérifier."," note references remain unverified.")));
            if(missing>0)navItems.Children.Add(Explain(missing+T(" champs n’ont pas de valeur enregistrée. Aucune valeur n’est inventée."," fields have no stored value. No value is invented.")));
            if(readable.Presentation.Fields.Count>0)
            {
                var fields=new ListBox{Name="ReadingFieldDiagnostics",ItemsSource=readable.Presentation.Fields.Values.ToArray(),Height=240,
                    ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ReadableFieldValue>((field,_)=>Explain(field is null?"":(
                        field.Source.PartUri+" "+field.Source.Path+"\n"+field.Code.Trim()+"\n"+
                        (field.HasStoredValue?field.StoredValue:field.Name=="TC"?T("Entrée de sommaire","Table of contents entry"):
                            T("Résultat indisponible","Result unavailable")))))};
                navItems.Children.Add(new Expander{Header=T("Champs et résultats enregistrés","Fields and stored results"),Content=fields});
            }
            if(tableFallbacks.Length>0)navItems.Children.Add(Explain(tableFallbacks.Length+T(
                " tableau(x) en lecture de secours. Consultez les propriétés non qualifiées dans les détails.",
                " table(s) use the fallback grid. See the details for unqualified properties.")));
            if(readable.Issues.Any(i=>i.Code=="READ_IMAGE_UNQUALIFIED"))navItems.Children.Add(Explain(T("Certaines images sont remplacées par un repère.","Some images are represented by a contextual mark.")));
        }
        navItems.Children.Add(Explain(T("Les repères dans le document donnent accès au détail des éléments concernés. La mise en page Microsoft Word n’est pas reproduite à l’identique.","Document marks open details for the relevant elements. Microsoft Word layout is not reproduced exactly.")));
        FillTypographyDiagnostics();
        if(nativePageGaps.Count>0)
        {
            var gaps=new ListBox{Name="NativePageGaps",ItemsSource=nativePageGaps,Height=220,FontSize=11,
                ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<PagePlacementGap>((gap,_)=>Explain(gap is null?"":
                    gap.Reason+" · "+gap.Source.Location.PartUri+" "+gap.Source.Location.Path+
                    (gap.ContextRange is {} span?$" · UTF-16 [{span.Start}, {span.EndExclusive})":"")))};
            var go=new Button{Content=T("Aller au passage","Go to passage"),IsEnabled=false};
            gaps.SelectionChanged+=(_,_)=>go.IsEnabled=IsSimplifiedReading&&
                gaps.SelectedItem is PagePlacementGap gap&&documentIndex?.BodyIndices.ContainsKey(gap.Source.NodeId)==true;
            go.Click+=(_,_)=>{if(gaps.SelectedItem is PagePlacementGap gap)NavigateToBlock(gap.Source.NodeId);};
            navItems.Children.Add(new Expander{Header=T("Sources sans position de page","Sources without a page position"),
                Content=new StackPanel{Children={gaps,go}}});
        }
        var technical=groups.Select(g=>$"{g.Category} · {g.Severity} · ×{g.Count}\n{g.Code}\n{g.Component}\n{g.Example}").ToArray();
        navItems.Children.Add(new Expander{Name="DiagnosticTechnicalDetails",Header=T("Détails techniques et XML","Technical and XML details"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,
            Content=new StackPanel{Children={Explain(groups.Sum(g=>g.Count)+T(" occurrences conservées dans "," retained occurrences in ")+groups.Count+T(" groupes."," groups.")),new ListBox{ItemsSource=technical,Height=340,FontSize=11,ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<string>((s,_)=>new TextBlock{Text=s,TextWrapping=TextWrapping.Wrap})}}}});
        var report=new Button{Content=T("Enregistrer le rapport détaillé…","Save detailed report…"),HorizontalAlignment=HorizontalAlignment.Stretch};
        report.Click+=async(_,_)=>await SaveDiagnosticReportAsync();navItems.Children.Add(report);
    }
    static TextBlock Explain(string text)=>new(){Text=text,FontSize=12,LineHeight=19,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,5)};
    void ShowElementDiagnostics(ReadableBlock block)
    {
        ShowPanel("diagnostics");
        bool Related(SourceLocation? source)=>source is not null&&source.PartUri==block.Source.PartUri&&(source.Path==block.Source.Path||source.Path.StartsWith(block.Source.Path+"/",StringComparison.Ordinal));
        var details=(inspection?.Manifest.Diagnostics.Where(d=>Related(d.Source)).Select(d=>d.Code+"\n"+d.Message)??[])
            .Concat(readable?.Issues.Concat(readable.Presentation.Issues).Where(i=>Related(i.Source)).Select(i=>i.Code+"\n"+i.Message)??[]).ToList();
        details.Insert(0,(block.Label??"")+"\n"+block.Text);
        if(block.LinkTarget is {} target)details.Insert(0,T("Cible du lien : ","Link target: ")+target);
        if(readable?.Presentation.Fields.TryGetValue(block.Id,out var field)==true)details.Insert(0,T("Code du champ : ","Field code: ")+field.Code+"\n"+T("Valeur enregistrée : ","Stored value: ")+field.StoredValue);
        navItems.Children.Insert(0,new Expander{Name="ElementTechnicalDetails",Header=T("Élément sélectionné · détails XML","Selected element · XML details"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,
            Content=new StackPanel{Children={Explain(block.Source.PartUri+"\n"+block.Source.Path),new ListBox{ItemsSource=details.Take(100).ToArray(),Height=200,FontSize=11}}}});
        navItems.Children.Insert(0,Explain(MarkerDescription(block)));
    }

    public byte[] DetailedDiagnosticReport()
    {
        return JsonSerializer.SerializeToUtf8Bytes(new{format="lecteur-word-diagnostics-v1",build=BuildReference.Current.FullReference(false),inspection=inspection?.Manifest,
            fonts=CurrentFonts,compositionIssues=composition?.Issues??flow?.Issues??[],legacyProfileIssues=flow?.Issues??[],pagination=composition is null?null:new{composition.Profile,composition.Available,pageCount=composition.Pages.Count,pages=composition.Pages.Select(p=>new{p.Number,p.Section,p.Geometry,cells=p.Cells,lines=p.Lines.Select(l=>new{l.ParagraphId,l.Source,l.CellId,l.IsRepeated,l.X,l.Baseline,l.Height,markers=l.Fragments.Where(f=>f.Generated),tabs=l.Tabs,images=l.Images.Select(i=>new{i.RunId,i.Source,i.X,i.Y,i.IsRepeated,i.Image.ResourceId,i.Image.Width,i.Image.Height,i.Image.Sha256})})})},readingIssues=readable?.Issues??[],readingPresentation=readable is null?null:new{
                notes=readable.Presentation.Notes.Values.Select(n=>new{n.ReferenceId,n.OriginBlockId,n.Kind,n.Number,n.Verified,n.Source,contentIds=n.Content.Select(b=>b.Id)}),
                readable.Presentation.Fields,sections=readable.Presentation.Sections.Select(s=>new{s.Number,s.ClosingBlockId,stories=s.Stories.Select(st=>new{st.Id,st.Kind,st.Variant,st.Inherited,st.Verified,st.Source,contentIds=st.Content.Select(b=>b.Id)})}),readable.Presentation.Issues}},new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
    }
    async Task SaveDiagnosticReportAsync()
    {
        var snapshot=DetailedDiagnosticReport();
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title=T("Rapport technique complet","Complete technical report"),SuggestedFileName="LecteurWord-diagnostics-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+".json",DefaultExtension="json",FileTypeChoices=[new FilePickerFileType("JSON"){Patterns=["*.json"]}]});
        if(file?.TryGetLocalPath() is not {} path)return;
        try{await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await output.WriteAsync(snapshot);status.Text=T("Rapport enregistré","Report saved");}
        catch(IOException){status.Text=T("Rapport non enregistré : fichier existant ou accès refusé","Report not saved: existing file or access denied");}
        catch(UnauthorizedAccessException){status.Text=T("Rapport non enregistré : accès refusé","Report not saved: access denied");}
    }
}
