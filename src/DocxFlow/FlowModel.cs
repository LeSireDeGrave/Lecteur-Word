using System.Globalization;
using System.Text;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;
public sealed record FlowIssue(string Code, SourceLocation? Source, string Message, bool BlocksRendering, bool BlocksExport = true);
public sealed record FlowParagraph(string Id, SourceLocation Source, string Text, bool PageBreakAfter = false);
public sealed record FlowDocument(string SourceHash, IReadOnlyList<FlowParagraph> Paragraphs, IReadOnlyList<FlowIssue> Issues)
{
    public bool CanRender => !Issues.Any(i => i.BlocksRendering);
    public bool CanExport => !Issues.Any(i => i.BlocksExport);
    public const string Profile = "plain-paragraphs-v1";
}
public static class FlowReader
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly HashSet<string> Plain = ["document", "body", "p", "r", "t", "tab", "br", "cr"];
    static readonly HashSet<string> Formatting = ["pPr", "rPr", "pStyle", "rStyle", "b", "i", "u", "sz", "rFonts", "spacing", "jc", "ind", "color", "lang", "keepNext", "keepLines", "outlineLvl", "sectPr", "pgSz", "pgMar"];
    public static FlowDocument Read(InspectionResult result)
    {
        var m = result.Manifest;
        var paragraphs = new List<FlowParagraph>();var issues = new List<FlowIssue>();
        if (!m.InspectionComplete)
            return new(m.SourceSha256 ?? "", [], [new("INSPECTION_REFUSED", null, "Inspection refusée / Inspection refused", true)]);
        if(m.Nodes.Count>50_000 || m.Nodes.Sum(n=>(long)(n.Text?.Length??0))>2_000_000)
            return new(m.SourceSha256??"",[],[new("FLOW_BUDGET",null,"Limite de composition ; inventaire disponible / Composition limit; inventory available",true)]);
        string main = m.Relationships.Single(r => r.SourcePart == "/" && r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).ResolvedPart!;
        var byId=m.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);
        var children=m.Nodes.Where(n=>n.ParentId is not null).ToLookup(n=>n.ParentId!);
        foreach (var part in m.Parts.Where(p=>p.PartUri!=main && p.PartUri!="/[Content_Types].xml" && !p.PartUri.EndsWith(".rels",StringComparison.Ordinal)))
            issues.Add(new("AUXILIARY_UNMAPPED",new(part.PartUri,"/"),"Partie auxiliaire conservée dans l’inventaire ; export non qualifié / Unmapped auxiliary part",false));
        foreach(var rel in m.Relationships.Where(r=>r.SourcePart!="/"))
            issues.Add(new("RELATIONSHIP_UNMAPPED",new(rel.SourcePart,"/"),"Lien ou dépendance hors profil / Relationship outside profile",false));
        foreach(var node in m.Nodes.Where(n=>n.Source.PartUri==main))
        {
            if(node.Name.StartsWith('#'))
            {
                if(node.Kind=="textValue" && !string.IsNullOrWhiteSpace(node.Text) &&
                    (node.ParentId is null || byId[node.ParentId].Name!="{"+W+"}t"))
                    issues.Add(new("TEXT_OUTSIDE_RUN",node.Source,"Texte hors structure qualifiée / Text outside qualified structure",true));
                continue;
            }
            string local=node.Name.StartsWith("{"+W+"}",StringComparison.Ordinal)?node.Name[(W.Length+2)..]:"";
            string? expectedParent=local switch{"body"=>"document","p"=>"body","r"=>"p","t" or "tab" or "br" or "cr"=>"r",_=>null};
            if(expectedParent is not null && (node.ParentId is null || byId[node.ParentId].Name!="{"+W+"}"+expectedParent))
                issues.Add(new("STRUCTURE_OUTSIDE_PROFILE",node.Source,"Structure hors profil / Structure outside profile",true));
            if(!Plain.Contains(local))
            {
                bool formatting=Formatting.Contains(local);
                issues.Add(new(formatting?"FORMATTING_PREVIEW_ONLY":"CONTENT_NOT_RENDERED",node.Source,
                    formatting?"Style non interprété : aperçu du texte seulement / Text preview only":"Contenu hors profil : rendu bloqué / Content outside rendering profile",!formatting));
            }
            foreach(var key in node.Attributes.Keys)
            {
                bool ns=key.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal);
                bool accepted=key=="{http://www.w3.org/XML/1998/namespace}space" && local=="t" || key=="{"+W+"}type" && local=="br";
                if(!ns&&!accepted && !Formatting.Contains(local))
                    issues.Add(new("ATTRIBUTE_UNMAPPED",node.Source,"Propriété hors profil / Attribute outside profile: "+key,false));
            }
            if(local!="p")continue;
            var buffer=new StringBuilder();bool pageBreak=false;
            void Walk(InventoryNode n)
            {
                if(n.Name=="{"+W+"}t")foreach(var text in children[n.Id].Where(c=>c.Kind=="textValue"))buffer.Append(text.Text);
                else if(n.Name=="{"+W+"}tab")buffer.Append('\t');
                else if(n.Name is "{"+W+"}br" or "{"+W+"}cr")
                {
                    if(n.Attributes.GetValueOrDefault("{"+W+"}type")=="page")
                    {
                        // A page break inside a paragraph needs a separate source segment.
                        paragraphs.Add(new(node.Id+"-"+paragraphs.Count.ToString(CultureInfo.InvariantCulture),node.Source,buffer.ToString(),true));
                        buffer.Clear();pageBreak=true;
                    }
                    else if(n.Attributes.GetValueOrDefault("{"+W+"}type") is null or "textWrapping")buffer.Append('\n');
                    else issues.Add(new("BREAK_UNMAPPED",n.Source,"Saut de colonne non qualifié / Column break not qualified",true));
                }
                else foreach(var child in children[n.Id])Walk(child);
            }
            Walk(node);
            if(buffer.Length>0||!pageBreak)paragraphs.Add(new(node.Id,node.Source,buffer.ToString()));
        }
        if(paragraphs.Count==0 && issues.Count==0)paragraphs.Add(new("empty",new(main,"/"),""));
        return new(m.SourceSha256!,paragraphs,issues);
    }
}
