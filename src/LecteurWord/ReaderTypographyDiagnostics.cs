using System.Collections.Immutable;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

internal sealed record UnmeasuredText(string ParagraphId,string Text,ImmutableArray<SemanticSource> Sources);
internal sealed record TypographyDiagnosticResult(string SnapshotId,ImmutableArray<UnmeasuredText> Unmeasured,
    ImmutableArray<FlowIssue> Diagnostics,string? Refusal=null);

public sealed partial class ReaderWindow
{
    CancellationTokenSource? typographyCancellation;
    TypographyDiagnosticResult? typographyDiagnostics;

    internal async Task<TypographyDiagnosticResult?> AnalyzeTypographyAsync()
    {
        if(typographyDiagnostics is not null)return typographyDiagnostics;
        if(closed||pending is not null||typographyCancellation is not null||
            inspection is not {} source||documentIndex?.Semantic is not {} semantic)return null;
        using var operation=new CancellationTokenSource();
        typographyCancellation=operation;
        if(navMode=="diagnostics")FillPanel();

        try
        {
            var result=await Task.Run(()=>
            {
                try
                {
                    using var session=new TypographySession(source,semantic,cancellation:operation.Token);
                    var typography=session.Measure(operation.Token);
                    var unmeasured=typography.Paragraphs.SelectMany(p=>p.Fragments)
                        .Where(f=>f.Measure is null)
                        .Select(f=>new UnmeasuredText(f.ParagraphId,f.Text,[..f.Sources.Select(s=>s.Source)]))
                        .ToImmutableArray();
                    return new TypographyDiagnosticResult(typography.SnapshotId,unmeasured,typography.Diagnostics);
                }
                catch(InvalidDataException e)
                {
                    return new TypographyDiagnosticResult(semantic.SnapshotId,[],[],e.Message);
                }
                catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
                {
                    return new TypographyDiagnosticResult(semantic.SnapshotId,[],[],"TEXT_RUNTIME_UNAVAILABLE");
                }
            },operation.Token);
            if(operation.IsCancellationRequested||closed||!ReferenceEquals(source,inspection))return null;
            return typographyDiagnostics=result;
        }
        catch(OperationCanceledException){return null;}
        finally
        {
            typographyCancellation=null;
            if(!closed&&pending is null&&navMode=="diagnostics")FillPanel();
        }
    }

    void FillTypographyDiagnostics()
    {
        if(documentIndex?.Semantic is null)return;

        var section=new StackPanel{Spacing=5};
        if(typographyDiagnostics is not {} result)
        {
            var analyze=new Button{Name="AnalyzeTypography",IsEnabled=typographyCancellation is null,
                Content=typographyCancellation is null?T("Analyser la couverture du texte","Analyze text coverage"):
                    T("Analyse en cours…","Analyzing…"),HorizontalAlignment=HorizontalAlignment.Stretch};
            analyze.Click+=async(_,_)=>await AnalyzeTypographyAsync();
            section.Children.Add(analyze);
        }
        else if(result.Refusal is {} code)
            section.Children.Add(Explain(T("Analyse typographique indisponible","Typography analysis unavailable")+" · "+code));
        else
        {
            section.Children.Add(Explain(T("Fragments sans mesure : ","Unmeasured fragments: ")+result.Unmeasured.Length));
            if(!result.Unmeasured.IsEmpty)
            {
                var missing=new ListBox{Name="UnmeasuredTypography",ItemsSource=result.Unmeasured,Height=160,FontSize=11,
                    ItemTemplate=new FuncDataTemplate<UnmeasuredText>((fragment,_)=>new TextBlock
                    {
                        Text=fragment is null?"":string.Join(" ",fragment.Text.EnumerateRunes().Take(12)
                            .Select(r=>$"U+{r.Value:X4}"))+(fragment.Text.EnumerateRunes().Skip(12).Any()?" …":""),
                        TextTrimming=TextTrimming.CharacterEllipsis
                    })};
                var details=new Button{Name="UnmeasuredTypographyDetails",Content=T("Voir le texte et sa source","Show text and source"),
                    HorizontalAlignment=HorizontalAlignment.Stretch,IsVisible=false};
                missing.SelectionChanged+=(_,_)=>details.IsVisible=missing.SelectedItem is UnmeasuredText;
                details.Click+=(_,_)=>{if(missing.SelectedItem is UnmeasuredText fragment)ShowTypographyFragment(fragment,details);};
                section.Children.Add(missing);
                section.Children.Add(details);
            }
            if(!result.Diagnostics.IsEmpty)
            {
                var diagnostics=result.Diagnostics.Select(d=>d.Code+" · "+d.Message+"\n"+d.Source?.PartUri+" "+d.Source?.Path).ToArray();
                section.Children.Add(new Expander{Header=T("Diagnostics de mesure","Measurement diagnostics"),
                    Content=new ListBox{Name="TypographyIssues",ItemsSource=diagnostics,Height=230,FontSize=11,
                        ItemTemplate=new FuncDataTemplate<string>((line,_)=>Explain(line??""))}});
            }
        }
        navItems.Children.Add(new Expander{Name="TypographyDiagnostics",Header=T("Couverture typographique","Text coverage"),
            IsExpanded=typographyDiagnostics is not null||typographyCancellation is not null,
            HorizontalAlignment=HorizontalAlignment.Stretch,Content=section});
    }

    void ShowTypographyFragment(UnmeasuredText fragment,Control anchor)
    {
        var source=new StackPanel{Spacing=8,MaxWidth=420};
        source.Children.Add(Explain(T("Texte source","Source text")));
        source.Children.Add(new SelectableTextBlock{Text=fragment.Text,TextWrapping=TextWrapping.Wrap});
        foreach(var address in fragment.Sources)
        {
            string range=address.Span is {} span?$" · UTF-16 [{span.Start}, {span.EndExclusive})":"";
            source.Children.Add(new SelectableTextBlock
            {
                Text=address.NodeId+range+"\n"+address.Location.PartUri+" "+address.Location.Path,
                FontSize=11,TextWrapping=TextWrapping.Wrap
            });
        }
        var note=readable?.Presentation.Notes.Values.FirstOrDefault(n=>n.Verified&&
            ReadablePresentation.Walk(n.Content).Any(b=>b.Id==fragment.ParagraphId));
        if(note is not null||documentIndex?.BodyIndices.ContainsKey(fragment.ParagraphId)==true)
        {
            var go=new Button{Content=note is not null?T("Lire la note","Read note"):T("Aller au passage","Go to passage")};
            go.Click+=(_,_)=>
            {
                readingFlyout?.Hide();
                if(note is not null)OpenNote(note,readingList);
                else NavigateToBlock(fragment.ParagraphId);
            };
            source.Children.Add(go);
        }
        readingFlyout?.Hide();
        var card=ContextCard(new ScrollViewer{Content=source,MaxHeight=410});
        ActiveReadingContext=card;
        readingFlyout=new Flyout{Content=card};
        readingFlyout.ShowAt(anchor);
    }
}
