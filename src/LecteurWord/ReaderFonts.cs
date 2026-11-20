using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LecteurWord.Flow;

namespace LecteurWord;

public sealed partial class ReaderWindow
{
    public DocumentFonts CurrentFonts=>composition?.Fonts??new();
    void FillProperties()
    {
        if(inspection is null){navItems.Children.Add(Explain(T("Aucun document","No document")));return;}
        navItems.Children.Add(Explain($"{fileName}\n{inspection.Manifest.Format}\n{inspection.Manifest.Parts.Count} parties / parts\n{inspection.Manifest.Nodes.Count} éléments / nodes\nSHA-256\n{inspection.Manifest.SourceSha256}"));
        var fonts=CurrentFonts;
        var section=new StackPanel{Spacing=8,Name="DocumentFonts"};
        section.Children.Add(new TextBlock{Text=T("Polices du document","Document fonts"),FontSize=17,FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap});
        section.Children.Add(Explain(T("Les polices ci-dessous concernent le moteur paginé. La lecture simplifiée conserve sa typographie de lecture.","The fonts below apply to the paginated engine. Simplified reading keeps its reading typography.")));
        var names=fonts.Declarations.Select(d=>d.Family).Concat(fonts.Requests.Where(r=>!r.Theme).Select(r=>r.Value)).Concat(fonts.Theme.Where(t=>t.Family.Length>0).Select(t=>t.Family)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).ToArray();
        var installed=names.Where(FontResolver.IsInstalled).ToArray();var missing=names.Where(n=>!FontResolver.IsInstalled(n)).ToArray();
        Add(T("Déclarées et référencées","Declared and referenced"),names.Select(n=>n));
        Add(T("Disponibles sous Linux","Available on Linux"),installed);
        Add(T("Absentes de Linux","Missing on Linux"),missing);
        var used=fonts.Uses.Where(u=>u.Resolution is not null).Select(u=>u.Resolution!).DistinctBy(r=>(r.RequestedFamily,r.UsedFamily,r.Status,r.Weight,r.Slant)).ToArray();
        Add(T("Faces réellement sélectionnées","Actual selected faces"),used.Select(r=>$"{r.RequestedFamily} → {r.UsedFamily}\n{r.Weight} / {r.Slant} · {r.Status}"));
        Add(T("Substitutions","Substitutions"),used.Where(r=>r.Status=="substituted").Select(r=>$"{r.RequestedFamily} → {r.UsedFamily}\n{r.Reason}"));
        Add(T("Polices incorporées","Embedded fonts"),fonts.Embedded.Select(e=>$"{e.Family} · {e.Face}\n{e.Status} · {e.Rights}\n{e.Detail}\n{e.Copyright}\n{e.LicenseNotice}\n{e.LicenseUrl}"));
        section.Children.Add(Explain(T("Une substitution ou une autre version de police peut changer les lignes et le nombre de pages. Aucune égalité de métriques avec Word n’est garantie.","A substitution or another font version can change lines and page count. Metric equivalence with Word is not guaranteed.")));
        section.Children.Add(Explain(T("Les scripts complexes et les propriétés hors profil gardent un refus explicite. Le rapport technique conserve les références, provenances et empreintes des faces.","Complex scripts and unsupported properties remain explicitly refused. The technical report retains references, source locations and face fingerprints.")));
        navItems.Children.Add(section);
        void Add(string title,IEnumerable<string> values)
        {
            var all=values.ToArray();var content=new StackPanel{Spacing=6};foreach(string value in all.Take(100))content.Children.Add(Explain(value));
            if(all.Length==0)content.Children.Add(Explain(T("Aucune","None")));
            if(all.Length>100)content.Children.Add(Explain(T("Liste limitée à 100 entrées ; rapport technique complet disponible.","List limited to 100 entries; full technical report available.")));
            section.Children.Add(new Expander{Header=new TextBlock{Text=title+" ("+all.Length+")",TextWrapping=TextWrapping.Wrap},HorizontalAlignment=HorizontalAlignment.Stretch,Content=content});
        }
    }
}
