using System.Text;
using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    internal readonly XElement OdtPageDefinitions=new(Office+"automatic-styles");
    internal readonly XElement OdtMasters=new(Office+"master-styles");
    internal readonly XElement OdtNamedStyles=new(Office+"styles");
    readonly Dictionary<string,int> odtSectionStarts=new(StringComparer.Ordinal);
    readonly HashSet<int> odtPageBreaks=[];
    readonly Dictionary<string,string> odtNamedStyles=new(StringComparer.Ordinal);
    readonly HashSet<string> odtFloatingIds=new(StringComparer.Ordinal);
    IReadOnlyList<PlacedFloatingTextFrame>? activeOdtFrames;
    bool reflowStory;
    LayoutReader.Reader? auxiliaryReader;

    int? OdtMasterFor(string block)=>odtSectionStarts.TryGetValue(block,out int section)?section:null;
    static string OdtMasterName(int section)=>section==1?"Standard":"LwMaster"+section;

    string? NamedOdtStyle(LayoutParagraph paragraph)
    {
        if(paragraph.StyleId.Length==0)return null;
        if(odtNamedStyles.TryGetValue(paragraph.StyleId,out var previous))return previous;
        var source=model.Styles.FirstOrDefault(s=>s.DeclaredId==paragraph.StyleId&&s.Type=="paragraph");
        if(source is null)return null;
        var name=model.ChildId(source.Id,"name") is {} id?model.Attribute(id,"val"):null;
        string key="LwNamed"+odtNamedStyles.Count;
        OdtNamedStyles.Add(new XElement(Style+"style",new XAttribute(Style+"name",key),
            new XAttribute(Style+"display-name",name??paragraph.StyleId),new XAttribute(Style+"family","paragraph")));
        odtNamedStyles.Add(paragraph.StyleId,key);
        return key;
    }

    XElement[] BuildOdtPageStyles()
    {
        foreach(var section in snapshot.Sections)
        {
            token.ThrowIfCancellationRequested();
            int number=section.Semantic.Number;
            var geometry=section.Format;
            if(section.Semantic.StartBlock<model.BodyBlocks.Length&&model.BodyBlocks[section.Semantic.StartBlock] is {} first)
            {
                var previous=number>1?snapshot.Sections[number-2]:null;
                bool inline=section.Start=="continuous"&&previous is not null&&previous.Format==section.Format&&
                    previous.MirrorMargins==section.MirrorMargins&&previous.Semantic.RunningStories.SequenceEqual(section.Semantic.RunningStories)&&
                    section.PageNumberStart is null;
                if(!inline)odtSectionStarts.TryAdd(first,number);
                if(number>1&&!inline)odtPageBreaks.Add(number);
                if(number>1&&!inline&&section.Start!="nextPage")
                    Reserve("EXPORT_ODT_SECTION_KIND",section.Source??model.Source(first),
                        "Les propriétés de section sont conservées. Cette transition est adaptée en saut de page.");
            }
            string name="LwPage"+number;
            var properties=new XElement(Style+"page-layout-properties",
                new XAttribute(Fo+"page-width",Pt(geometry.Width)),new XAttribute(Fo+"page-height",Pt(geometry.Height)),
                new XAttribute(Fo+"margin-left",Pt(geometry.Left)),new XAttribute(Fo+"margin-right",Pt(geometry.Right)),
                new XAttribute(Style+"print-orientation",geometry.Width>geometry.Height?"landscape":"portrait"));
            if(section.ColumnLayout is { Columns.Length:>1 })
                Reserve("EXPORT_ODT_COLUMNS",section.Source??model.Source(model.BodyBlocks[section.Semantic.StartBlock]),
                    "La section en colonnes est conservée dans le flux éditable sur une colonne.");
            var layout=new XElement(Style+"page-layout",new XAttribute(Style+"name",name),properties);
            if(section.MirrorMargins)layout.SetAttributeValue(Style+"page-usage","mirrored");
            var master=new XElement(Style+"master-page",new XAttribute(Style+"name",OdtMasterName(number)),
                new XAttribute(Style+"page-layout-name",name));
            foreach(string kind in new[]{"header","footer"})
            {
                bool header=kind=="header";
                var variants=snapshot.StoryRegions.Where(r=>r.Section==number&&r.Kind==kind&&r.Status!="Inactive").ToArray();
                var visible=variants.Where(r=>r.Selection?.Reference.TargetId is {} target&&HasAuxiliaryContent(target)).ToArray();
                float margin=header?geometry.Top:geometry.Bottom;
                long? distance=header?section.HeaderDistance:section.FooterDistance;
                if(visible.Length>0&&distance is {} edge)
                {
                    float flow=visible.Select(r=>PageComposer.Points(r.FlowHeight??0)).DefaultIfEmpty(0).Max();
                    float declared=PageComposer.Points(edge);
                    // An anchor-only story has no flow height, but needs an ODF header/footer paragraph to own its shapes.
                    if(flow>0)
                    {
                        float gap=Math.Max(0,margin-declared-flow);
                        margin=declared;
                        layout.Add(new XElement(Style+(kind+"-style"),new XElement(Style+"header-footer-properties",
                            new XAttribute(Fo+"min-height",Pt(flow)),new XAttribute(Fo+(header?"margin-bottom":"margin-top"),Pt(gap)))));
                    }
                }
                properties.SetAttributeValue(Fo+(header?"margin-top":"margin-bottom"),Pt(margin));
                if(visible.Length==0)continue;
                foreach(var variant in variants.OrderBy(v=>v.Variant=="default"?0:v.Variant=="even"?1:2))
                {
                    string suffix=variant.Variant switch{"even"=>"-left","first"=>"-first",_=>""};
                    if(variant.Variant=="even"&&!section.EvenAndOdd||variant.Variant=="first"&&!section.TitlePage)continue;
                    XElement[] content=[];
                    if(variant.Selection?.Reference.TargetId is {} root&&HasAuxiliaryContent(root))
                    {
                        var physical=variant.PhysicalPages.FirstOrDefault();
                        var page=source.Pages.FirstOrDefault(p=>p.Number==physical)??PageComposer.PlaceMeasuredStoryFrames(variant,section);
                        activeOdtFrames=page?.FloatingTextFrames??[];
                        foreach(var frame in activeOdtFrames)odtFloatingIds.Add(frame.Frame.Id);
                        try{content=AuxiliaryBlocks(root,geometry);}
                        finally{activeOdtFrames=null;}
                        Represent(root);
                    }
                    master.Add(new XElement(Style+(kind+suffix),content.Length==0?[new XElement(Text+"p")]:content));
                }
                Feature("running-stories",PublicationRetention.Structured,visible[0].Selection?.Reference.Relation is {} relation?
                    new LecteurWord.DocxCore.SourceLocation(relation.SourcePart,"/"):null,"Variantes auxiliaires et champs éditables dans les styles de page.");
            }
            OdtPageDefinitions.Add(layout);
            OdtMasters.Add(master);
        }
        return [];
    }

    XElement[] AuxiliaryBlocks(string root,PageGeometry geometry)
    {
        LoadStoryLayout(root,geometry);
        return Blocks(model.BlocksOf(root).Select(e=>e.Id));
    }

    internal void LoadStoryLayout(string root,PageGeometry geometry)
    {
        token.ThrowIfCancellationRequested();
        var story=model.Stories.Single(s=>s.RootId==root);
        if(auxiliaryReader is null)
        {
            var cascade=new TableStyleCascade(model,token);
            auxiliaryReader=new(source.Inspection,token,model,cascade,nativeComposition:true,
                calculatedBodyFields:model.Fields.Select(f=>f.Id).ToHashSet(StringComparer.Ordinal));
            _=auxiliaryReader.Read();
        }
        var layout=auxiliaryReader.ReadStory(story,geometry);
        foreach(var paragraph in layout.Paragraphs)paragraphs.TryAdd(paragraph.Id,paragraph);
        foreach(var table in layout.Tables)tables.TryAdd(table.Id,table);
        foreach(var run in layout.Paragraphs.SelectMany(p=>p.Runs))
            if(run.Image is {} image)storyImages.TryAdd(run.Id,image);
    }

    void AppendOdtDrawing(XElement paragraph,SemanticSource sourceId)
    {
        var frames=NativeOdtFrames(sourceId.NodeId);
        if(frames.Length==0)return;
        var candidates=activeOdtFrames??floatingObjects[sourceId.NodeId].Page.FloatingTextFrames;
        var stacking=candidates.OrderBy(f=>f.Frame.BehindText?0:1).ThenBy(f=>f.Frame.Order)
            .ThenBy(f=>f.Frame.Id,StringComparer.Ordinal).ThenBy(f=>f.Frame.ChildOrder).ToList();
        var output=frames.Length>1?new XElement(Draw+"g",new XAttribute(Text+"anchor-type","paragraph"),
            new XAttribute(Draw+"z-index",stacking.IndexOf(frames[0]))):paragraph;
        foreach(var placement in frames.OrderBy(f=>f.Frame.BehindText?0:1).ThenBy(f=>f.Frame.Order).ThenBy(f=>f.Frame.ChildOrder))
        {
            var frame=placement.Frame;
            var props=new XElement(Style+"graphic-properties",
                new XAttribute(Style+"horizontal-pos","from-left"),new XAttribute(Style+"horizontal-rel","page"),
                new XAttribute(Style+"vertical-pos","from-top"),new XAttribute(Style+"vertical-rel","page"),
                new XAttribute(Style+"wrap","run-through"),new XAttribute(Style+"run-through",frame.BehindText?"background":"foreground"),
                new XAttribute(Draw+"fill",frame.Fill is null?"none":"solid"),
                new XAttribute(Draw+"stroke",frame.Stroke is null?"none":"solid"));
            if(frame.Fill is uint color)props.SetAttributeValue(Draw+"fill-color",$"#{color:X6}");
            if(frame.Stroke is {} pen)
            {
                props.SetAttributeValue(SvgOdt+"stroke-color",$"#{pen.Color:X6}");
                props.SetAttributeValue(SvgOdt+"stroke-width",Pt(pen.Width));
            }
            foreach(var (edge,value) in new[]{("left",frame.Left),("top",frame.Top),("right",frame.Right),("bottom",frame.Bottom)})
                props.SetAttributeValue(Fo+("padding-"+edge),Pt(value));
            if(frame.Center||frame.BottomAligned)props.SetAttributeValue(Draw+"textarea-vertical-align",frame.Center?"middle":"bottom");
            var graphic=new XElement(Draw+(frame.StoryId.Length>0||frame.Vector is not null?"frame":"rect"),
                new XAttribute(Draw+"style-name",Register("graphic",props)),new XAttribute(Text+"anchor-type","paragraph"),
                new XAttribute(Draw+"z-index",stacking.IndexOf(placement)),
                new XAttribute(SvgOdt+"x",Pt(frame.X)),new XAttribute(SvgOdt+"y",Pt(frame.Y)),
                new XAttribute(SvgOdt+"width",Pt(frame.Width)),new XAttribute(SvgOdt+"height",Pt(frame.Height)));
            if(frame.Vector is {} vector&&AssetFor(vector) is {} asset)
                graphic.Add(new XElement(Draw+"image",new XAttribute(Xlink+"href",asset.Path),new XAttribute(Xlink+"type","simple"),
                    new XAttribute(Xlink+"show","embed"),new XAttribute(Xlink+"actuate","onLoad")));
            else if(frame.StoryId.Length>0)
            {
                var content=AuxiliaryBlocks(frame.StoryId,frame.Geometry);
                if(activeOdtFrames is not null&&content is [var fieldParagraph]&&fieldParagraph.Name==Text+"p"&&
                    fieldParagraph.Descendants().Any(n=>n.Name==Text+"page-number"||n.Name==Text+"page-count"))
                {
                    // Writer drops page fields when it saves a text box inside a running story.
                    if(paragraph.FirstNode is null&&fieldParagraph.Attribute(Text+"style-name") is {} style)
                        paragraph.SetAttributeValue(Text+"style-name",style.Value);
                    else if(paragraph.FirstNode is not null)paragraph.Add(new XElement(Text+"line-break"));
                    paragraph.Add(fieldParagraph.Nodes());
                    Represent(frame.StoryId,PublicationRepresentation.Presentation);
                    Represent(sourceId.NodeId,PublicationRepresentation.Presentation);
                    Reserve("EXPORT_ODT_RUNNING_FRAME_PAGE_FIELD",sourceId,
                        "Le champ de pagination reste dynamique dans l’en-tête ou le pied. Son paragraphe est déplacé hors du cadre.");
                    if(frame.Fill is null&&frame.Stroke is null)continue;
                    graphic.Add(new XElement(Draw+"text-box",new XElement(Text+"p")));
                }
                else
                {
                    graphic.Add(new XElement(Draw+"text-box",content));
                    Represent(frame.StoryId);
                }
            }
            else if(frame.Vector is not null)
            {Issue("EXPORT_ODT_VECTOR_UNSUPPORTED",sourceId,"Dessin flottant sans ressource vectorielle qualifiée.");continue;}
            output.Add(graphic);
            Represent(sourceId.NodeId,frame.Vector is null?PublicationRepresentation.Native:PublicationRepresentation.Compatible);
        }
        if(output!=paragraph&&output.HasElements)paragraph.Add(output);
    }

    XElement[] BuildReflowStories(bool epub)
    {
        var blocks=new List<XElement>();
        if(!epub)return [];
        foreach(var region in snapshot.StoryRegions.Where(r=>r.Status!="Inactive"&&r.Selection?.Reference.TargetId is not null)
            .DistinctBy(r=>r.Selection!.Reference.TargetId,StringComparer.Ordinal))
        {
            string root=region.Selection!.Reference.TargetId!;
            if(!HasAuxiliaryContent(root,ignoreFolios:epub))continue;
                var layout=snapshot.Sections[region.Section-1].Format;
                if(RepeatedAuxiliary(root,region.Kind,layout))continue;
            reflowStory=true;
            try
            {
                blocks.Add(new XElement(Html+"aside",new XAttribute(Epub+"type","sidebar"),
                    new XAttribute("aria-label",region.Kind=="header"?"En-tête du document":"Pied du document"),
                    AuxiliaryBlocks(root,layout)));
                Represent(root,PublicationRepresentation.Presentation);
                Issue("EXPORT_EPUB_AUXILIARY_ONCE",model.Source(root),
                    "Le contenu auxiliaire utile est placé une seule fois dans l’ordre de lecture, sans folios récurrents.",false);
            }
            finally{reflowStory=false;}
        }
        return blocks.ToArray();
    }

    bool HasAuxiliaryContent(string root,bool ignoreFolios=false)
    {
        foreach(var element in model.BlocksOf(root))
        {
            if(element.Kind!=SemanticKind.Paragraph)return true;
            var fields=fieldsByParagraph[element.Id].ToArray();
            var omitted=ignoreFolios?fields.Where(f=>f.Command is "PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF")
                .SelectMany(f=>f.StoredResultIds).ToHashSet(StringComparer.Ordinal):[];
            if(fields.Any(f=>!ignoreFolios||f.Command is not ("PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF")))return true;
            if(tokens[element.Id].Any(t=>Visible(t)&&!omitted.Contains(t.Source.NodeId)&&
                (t.Control=="Drawing"&&(!ignoreFolios||!IsDecorativeDrawing(t.Source.NodeId))||
                    !string.IsNullOrWhiteSpace(t.Original))))return true;
        }
        return false;
    }

    internal IEnumerable<(string Title,string[] Blocks)> NovaAuxiliary()
    {
        foreach(var region in snapshot.StoryRegions.Where(r=>r.Status!="Inactive"&&r.Selection?.Reference.TargetId is not null)
            .DistinctBy(r=>r.Selection!.Reference.TargetId,StringComparer.Ordinal))
        {
            string root=region.Selection!.Reference.TargetId!;
            if(!HasAuxiliaryContent(root,ignoreFolios:true))continue;
            var layout=snapshot.Sections[region.Section-1].Format;
            if(RepeatedAuxiliary(root,region.Kind,layout))continue;
            LoadStoryLayout(root,layout);
            string[] blocks=model.BlocksOf(root).Select(e=>e.Id).ToArray();
            if(blocks.Length==0)continue;
            Reserve("EXPORT_NOVA_AUXILIARY",model.Source(root),"Le contenu de l’en-tête ou du pied reste éditable en annexe du manuscrit.");
            yield return (region.Kind=="header"?"En-tête du document":"Pied du document",blocks);
        }
    }

    readonly HashSet<(string Kind,string Signature)> reflowAuxiliary=[];

    bool RepeatedAuxiliary(string root,string kind,PageGeometry geometry)
    {
        var signature=new StringBuilder();
        if(!AppendStory(root,geometry,allowFrames:true)||reflowAuxiliary.Add((kind,signature.ToString())))return false;
        foreach(var element in InventoriedContent([root],followNotes:false))
            Represent(element.Id,PublicationRepresentation.Presentation);
        Issue("EXPORT_REFLOW_AUXILIARY_REPEAT",model.Source(root),
            "Ce contenu et ses styles sont déjà représentés dans une autre variante de même rôle.",false);
        return true;

        bool AppendStory(string story,PageGeometry dimensions,bool allowFrames)
        {
            LoadStoryLayout(story,dimensions);
            Add("story");
            foreach(var block in model.BlocksOf(story))
            {
                token.ThrowIfCancellationRequested();
                if(block.Kind!=SemanticKind.Paragraph||fieldsByParagraph[block.Id].Any()||marks[block.Id].Any()||
                    Paragraph(block.Id) is not {List:null,Properties:{Heading:0,NumId:null}} paragraph)
                    return false;
                var visible=tokens[block.Id].Where(Visible).ToArray();
                bool anchorOnly=allowFrames&&visible.Length>0&&visible.All(item=>item.Control=="Drawing");
                var p=paragraph.Properties;
                if(anchorOnly)Add("drawing-anchor");
                else
                {
                    if(p.HasBorders||p.Shading is not null||p.Tabs.Count!=0)return false;
                    Add(FormattableString.Invariant($"p:{p.Alignment}:{p.Left}:{p.Right}:{p.FirstLine}:{p.Before}:{p.After}:{p.Line}:{p.LineRule}:{p.KeepNext}:{p.KeepLines}:{p.Widow}:{p.ContextualSpacing}:{p.PageBreakBefore}"));
                    Add(paragraph.StyleId);
                }
                foreach(var item in visible)
                {
                    if(LinkFor(item.Source.NodeId) is not null)return false;
                    if(item.Role==TextRole.Source&&item.Original is {} value)
                    {
                        Add("text");Add(value);
                        if(item.Style is {} style)
                        {
                            Add(CssStyle(style,style.Slots.Ascii?.Value));
                            Add(FormattableString.Invariant($"{style.Language}:{style.RightToLeft}:{style.Complex}:{style.HorizontalScale}:{style.Shadow}"));
                        }
                    }
                    else if(item.Control=="Drawing"&&allowFrames&&floatingObjects.GetValueOrDefault(item.Source.NodeId) is
                        {Pictures.Length:0,Frames.Length:>0} drawing&&drawing.Frames.All(f=>f.Frame.StoryId.Length>0&&f.Frame.Vector is null))
                    {
                        foreach(var placed in drawing.Frames.OrderBy(f=>f.Frame.ChildOrder))
                        {
                            var frame=placed.Frame;
                            Add(FormattableString.Invariant($"frame:{frame.Fill}:{frame.Stroke}"));
                            if(!AppendStory(frame.StoryId,frame.Geometry,allowFrames:false))return false;
                        }
                    }
                    else return false;
                    if(signature.Length>32_768)return false;
                }
            }
            Add("end-story");
            return true;
        }

        void Add(string value)=>signature.Append(value.Length).Append(':').Append(value);
    }

    internal bool IsRunningStory(SemanticField field)=>runningStoryIds.Contains(field.StoryId);
    readonly HashSet<string> runningStoryIds=source.Semantic!.Stories.GroupBy(s=>source.Semantic!.Source(s.RootId).Location.PartUri)
        .Where(part=>part.Any(s=>s.Kind is "hdr" or "ftr")).SelectMany(part=>part.Select(s=>s.RootId)).ToHashSet(StringComparer.Ordinal);
    bool IsDecorativeDrawing(string id)=>placedImages.GetValueOrDefault(id)?.Decorative==true||
        floatingObjects.GetValueOrDefault(id) is {Pictures.Length:>0,Frames.Length:0} drawing&&
        drawing.Pictures.All(p=>p.Picture.Image.Decorative==true);
}
