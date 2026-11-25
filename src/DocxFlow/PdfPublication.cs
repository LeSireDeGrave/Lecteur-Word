using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

internal static class PdfPublication
{
    const string Profile="native-page-pdf-v1";
    sealed record Destination(string Name,int Page,float Left,float Top);
    internal sealed record Outline(string Name,string Title,int Level);
    sealed record Link(int Page,PageRect Rect,string Target,bool External);
    sealed record Navigation(IReadOnlyList<Destination> Destinations,IReadOnlyList<Outline> Outlines,IReadOnlyList<Link> Links,
        IReadOnlyList<FlowIssue> Issues);

    internal static PublicationPlan Prepare(PublicationSource source,PublicationRequest request,CancellationToken token)
    {
        if(source.Pagination is not {Rendering.Pagination:PageCoverage.Complete,Composition.Available:true})
            return Publications.Blocked("EXPORT_PDF_PAGES","La composition paginée complète est nécessaire pour publier le PDF.");
        if(Publications.Range(source,request) is not {} range)
            return Publications.Blocked("EXPORT_PDF_RANGE","La plage de feuilles saisie est invalide.");
        var features=ImmutableArray.CreateBuilder<PublicationFeature>();
        var issues=ImmutableArray.CreateBuilder<FlowIssue>();
        features.Add(new("pages",PublicationRetention.Native,null,"Dimensions et géométrie de la composition affichée."));
        var faces=new HashSet<(string,int)>();
        var shown=new HashSet<string>(StringComparer.Ordinal);
        foreach(var page in Selected(source,range))
        {
            token.ThrowIfCancellationRequested();
            if(page.NativePainter is null||page.DisplayLines.Any(l=>l.Fragments.Count>0))
                return Publications.Blocked("EXPORT_PDF_PAINT_SOURCE","Une feuille utilise une composition sans provenance typographique native.");
            foreach(var fragment in Fragments(page))
            {
                token.ThrowIfCancellationRequested();
                if(fragment.Face is {} face&&faces.Add((face.Sha256,face.FaceIndex))&&
                    !page.NativePainter.PermitsPdfEmbedding(face))
                    issues.Add(new("EXPORT_PDF_FONT_RIGHTS",fragment.Sources[0].Source.Location,
                        $"La face « {face.UsedFamily} » ne permet pas l’incorporation PDF avec sous-ensemble.",false));
                if(fragment.LegacySymbol is {} symbol&&shown.Add(symbol.Family+":"+symbol.Code)&&
                    symbol.Method is ("user" or "missing-marker"))
                    issues.Add(new("EXPORT_PDF_SYMBOL_VISUAL",fragment.Sources[0].Source.Location,
                        $"Caractère U+{symbol.Code:X4} représenté selon le choix ou le diagnostic du lecteur.",false,BlocksExport:false));
            }
        }
        if(source.Pagination.Rendering.MissingGlyphs>0)
        {
            features.Add(new("unresolved-glyph",PublicationRetention.Vector,null,"Repères du lecteur avec provenance des glyphes manquants."));
            issues.Add(new("EXPORT_PDF_UNRESOLVED_GLYPHS",null,
                $"Le document comporte {source.Pagination.Rendering.MissingGlyphs} glyphes non résolus. Les repères visibles sont conservés.",false,false));
        }
        features.Add(new("text",PublicationRetention.Native,null,"Glyphes positionnés, polices autorisées et texte PDF lorsque le mappage est disponible."));
        features.Add(new("drawing",PublicationRetention.Vector,null,"Formes, traits, chemins et SmartArt transmis au peintre PDF."));
        features.Add(new("image",PublicationRetention.Raster,null,request.PdfImageQuality==101?
            "Images sans compression avec perte ajoutée.":"Compression JPEG des images opaques selon la qualité choisie."));
        if(request.IncludePdfProvenance)
            features.Add(new("glyph-provenance",PublicationRetention.Structured,null,
                "Pièce jointe contenant les codes des glyphes de remplacement et leurs emplacements internes."));
        features.Add(new("layout-structure",PublicationRetention.Structured,null,"Régions, tableaux, notes, zones de texte et champs sous leur forme composée."));
        var semantic=source.Semantic!;
        foreach(var kind in new[]{SemanticKind.Revision,SemanticKind.CommentReference})
        {
            var omitted=semantic.Elements.FirstOrDefault(e=>e.Kind==kind&&e.Coverage!=SemanticCoverage.InactiveAlternative);
            if(omitted.Id is null)continue;
            issues.Add(new("EXPORT_PDF_EDITORIAL_HISTORY",semantic.Source(omitted.Id).Location,
                kind==SemanticKind.Revision?"Le PDF conserve l’état de lecture affiché. L’historique de révision reste dans le DOCX.":
                    "Les commentaires éditables restent dans le DOCX.",false,false));
            features.Add(new(kind.ToString(),PublicationRetention.Unsupported,semantic.Source(omitted.Id).Location,"État de lecture PDF."));
        }
        issues.AddRange(BuildNavigation(source,range,token).Issues);
        if(request.Policy==PublicationPolicy.Complete)
        {
            var reservations=issues.Where(issue=>!issue.BlocksExport).Select(issue=>issue with{BlocksExport=true}).ToImmutableArray();
            return new(Profile,features.ToImmutable(),[..issues.Where(issue=>issue.BlocksExport),..reservations])
                {Reservations=reservations};
        }
        return new(Profile,features.ToImmutable(),issues.ToImmutable());
    }

    static IEnumerable<ComposedPage> Selected(PublicationSource source,(int First,int Last) range)=>
        source.Pages.Skip(range.First-1).Take(range.Last-range.First+1);

    static IEnumerable<TextFragment> Fragments(ComposedPage page)
    {
        foreach(var line in page.MainDisplayLines.Concat(page.FloatingTextFrames.SelectMany(f=>f.Content.Lines)))
        {
            if(line.Native is {} native)
            {
                foreach(var part in native.Parts)
                    if(part.Fragment is {} fragment)yield return fragment;
                foreach(var item in native.Generated)yield return item.Fragment;
            }
            foreach(var image in line.Images)
                if(image.Image.Diagram is {} diagram)
                    foreach(var text in diagram.Shapes.SelectMany(s=>s.Text))yield return text.Fragment;
        }
        foreach(var frame in page.FloatingTextFrames)
            if(frame.Frame.Vector?.Diagram is {} diagram)
                foreach(var text in diagram.Shapes.SelectMany(s=>s.Text))yield return text.Fragment;
    }

    internal static void Write(string path,PublicationSource source,PublicationRequest request,ExportBudget budget,
        IProgress<PublicationProgress>? progress,CancellationToken token)
    {
        var range=Publications.Range(source,request)!.Value;
        var nav=BuildNavigation(source,range,token);
        if(nav.Issues.Any(i=>i.BlocksExport))throw new PublicationRefusal(nav.Issues);
        var destinations=nav.Destinations.ToLookup(x=>x.Page);
        var links=nav.Links.ToLookup(x=>x.Page);
        var metadata=source.Metadata;
        var settings=new SKDocumentPdfMetadata
        {
            Title=metadata.Title,Author=metadata.Author??"",Subject=metadata.Subject??metadata.Description??"",
            Keywords=metadata.Keywords??"",Creator="Lecteur Word",RasterDpi=144,EncodingQuality=request.PdfImageQuality,
            Creation=metadata.Created?.UtcDateTime,Modified=metadata.Modified?.UtcDateTime
        };
        int total=range.Last-range.First+1,done=0;
        using(var file=budget.Create(path,deferFailures:true))
        {
            using var pdf=SKDocument.CreatePdf(file,settings)??throw new IOException("EXPORT_PDF_CREATE");
            foreach(var page in Selected(source,range))
            {
                budget.Check(0);
                var canvas=pdf.BeginPage(page.Geometry.Width,page.Geometry.Height)??throw new IOException("EXPORT_PDF_PAGE");
                Composer.DrawPage(canvas,page);
                foreach(var destination in destinations[page.Number])
                    canvas.DrawNamedDestinationAnnotation(new(destination.Left,destination.Top),destination.Name);
                foreach(var link in links[page.Number])
                {
                    var box=link.Rect;
                    SKRect rect=new(box.Left,box.Top,box.Left+box.Width,box.Top+box.Height);
                    if(link.External)canvas.DrawUrlAnnotation(rect,link.Target);
                    else canvas.DrawLinkDestinationAnnotation(rect,link.Target);
                }
                pdf.EndPage();
                budget.ThrowIfFailed();
                progress?.Report(new("write",++done,total));
            }
            pdf.Close();
            budget.ThrowIfFailed();
            file.Flush();
            budget.ThrowIfFailed();
        }
        byte[]? provenance=null;
        if(request.IncludePdfProvenance)
        {
            var symbols=Selected(source,range).SelectMany(page=>Fragments(page)
                .Where(f=>f.LegacySymbol is {Method:"missing-marker" or "user"})
                .Select(f=>new{page=page.Number,source=f.Sources[0].Source.Location,original=f.Text,
                    codePoint=$"U+{f.LegacySymbol!.Code:X4}",requestedFont=f.LegacySymbol.Family,
                    representation=f.LegacySymbol.Method,replacementCode=f.LegacySymbol.DrawingCode})).Distinct().ToArray();
            if(symbols.Length>0)provenance=JsonSerializer.SerializeToUtf8Bytes(new{schema=1,symbols});
        }
        if(nav.Outlines.Count>0||provenance is not null)PdfNavigation.Append(path,nav.Outlines,provenance,budget);
    }

    internal static void Verify(string path,PublicationSource source,PublicationRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var range=Publications.Range(source,request)!.Value;
        PdfNavigation.Verify(path,range.Last-range.First+1);
    }

    static Navigation BuildNavigation(PublicationSource source,(int First,int Last) range,CancellationToken token)
    {
        var snapshot=source.Pagination!;
        var model=source.Semantic!;
        var anchors=PageComposer.PublicationBookmarks(snapshot,token.ThrowIfCancellationRequested);
        var destinations=new List<Destination>();
        var outline=new List<Outline>();
        var links=new List<Link>();
        var issues=new List<FlowIssue>();
        var headings=(snapshot.RulerModel?.Paragraphs??[]).Where(p=>p.Properties.Heading is >=1 and <=9)
            .ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var reference=model.References.Where(r=>r.Kind is "hyperlink" or "bookmark").ToDictionary(r=>r.SourceId,StringComparer.Ordinal);
        var lookup=new Dictionary<string,SemanticReference?>(StringComparer.Ordinal);
        var ids=new Dictionary<string,Destination>(StringComparer.Ordinal);
        foreach(var (id,position) in anchors.OrderBy(e=>model.Element(e.Key).Order))
        {
            token.ThrowIfCancellationRequested();
            if(position.Page<range.First||position.Page>range.Last)continue;
            Destination destination=new("b"+ids.Count.ToString(CultureInfo.InvariantCulture),position.Page,position.Left,position.Top);
            ids.Add(id,destination);
            destinations.Add(destination);
        }
        foreach(var page in Selected(source,range))
        {
            token.ThrowIfCancellationRequested();
            PageRect sheet=new(0,0,page.Geometry.Width,page.Geometry.Height);
            foreach(var line in page.DisplayLines)
            {
                if(headings.TryGetValue(line.ParagraphId,out var heading)&&seen.Add(heading.Id))
                {
                    string title=heading.Text.Trim();
                    if(title.Length>0)
                    {
                        string name="h"+outline.Count.ToString(CultureInfo.InvariantCulture);
                        outline.Add(new(name,title,heading.Properties.Heading));
                        destinations.Add(new(name,page.Number,Math.Clamp(line.X,0,page.Geometry.Width),
                            Math.Clamp(PageCoordinates.LineTop(line),0,page.Geometry.Height)));
                    }
                }
                foreach(var item in PageCoordinates.Clusters(line).Concat(PageCoordinates.Images(line)))
                {
                    var hit=item.Sources.Select(s=>ParentLink(s.NodeId)).OfType<SemanticReference>().Distinct().Take(2).ToArray();
                    if(hit is not [var link])continue;
                    var rect=item.Rect.Intersection(sheet);
                    if(rect.Empty)continue;
                    if(link is {Kind:"hyperlink",Status:ReferenceStatus.External}&&Publications.SafeUrl(link.Relation?.Target) is {} url)
                        Add(rect,url,true);
                    else if(link.TargetId is {} id&&ids.TryGetValue(id,out var target))Add(rect,target.Name,false);
                    else if(seen.Add(link.SourceId))
                        issues.Add(new("EXPORT_PDF_LINK_UNREPRESENTABLE",model.Source(link.SourceId).Location,
                            link.TargetId is {} known&&anchors.ContainsKey(known)?"Lien vers une feuille hors de la plage. Le texte reste visible.":
                                "La cible du lien est indisponible. Le texte du lien reste visible.",false,false));
                }
                foreach(var (id,bounds) in PageCoordinates.FieldLabels(line))
                    if(snapshot.PageLinkTargets.TryGetValue(id,out string? target)&&ids.TryGetValue(target,out var place))
                    {
                        var rect=bounds.Intersection(sheet);
                        if(!rect.Empty)Add(rect,place.Name,false);
                    }
            }

            void Add(PageRect rect,string target,bool external)
            {
                if(links.Count>0&&links[^1] is {} previous&&previous.Page==page.Number&&previous.Target==target&&
                    previous.External==external&&Math.Abs(previous.Rect.Top-rect.Top)<.01f&&
                    Math.Abs(previous.Rect.Height-rect.Height)<.01f&&Math.Abs(previous.Rect.Left+previous.Rect.Width-rect.Left)<.05f)
                    links[^1]=previous with{Rect=previous.Rect with{Width=previous.Rect.Width+rect.Width}};
                else links.Add(new(page.Number,rect,target,external));
                if(links.Count>100_000)throw new InvalidDataException("EXPORT_PDF_LINK_LIMIT");
            }
        }
        return new(destinations,outline,links,issues);

        SemanticReference? ParentLink(string id)
        {
            if(lookup.TryGetValue(id,out var existing))return existing;
            string? cursor=id;
            SemanticReference? ancestor=null;
            while(cursor is not null&&!(lookup.TryGetValue(cursor,out ancestor)||reference.TryGetValue(cursor,out ancestor)))
                cursor=model.ParentId(cursor);
            lookup.TryAdd(id,ancestor);
            return ancestor;
        }
    }
}
