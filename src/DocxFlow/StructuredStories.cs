using System.Xml.Linq;

namespace LecteurWord.Flow;

internal sealed partial class StructuredPublication
{
    internal readonly XElement OdtPageDefinitions=new(Office+"automatic-styles");
    internal readonly XElement OdtMasters=new(Office+"master-styles");
    internal readonly XElement OdtNamedStyles=new(Office+"styles");
    readonly Dictionary<string,int> odtSectionStarts=new(StringComparer.Ordinal);
    readonly Dictionary<string,string> odtNamedStyles=new(StringComparer.Ordinal);
    readonly HashSet<string> odtFloatingIds=new(StringComparer.Ordinal);
    IReadOnlyList<PlacedFloatingTextFrame> activeOdtFrames=[];
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
            if(number>1&&section.Start!="nextPage")
                Issue("EXPORT_ODT_SECTION_KIND",section.Source??model.Source(model.BodyBlocks[section.Semantic.StartBlock]),
                    "Transition de section continue, paire, impaire ou en colonne hors du profil ODT.");
            if(section.Semantic.StartBlock<model.BodyBlocks.Length&&model.BodyBlocks[section.Semantic.StartBlock] is {} first)
                odtSectionStarts.TryAdd(first,number);
            string name="LwPage"+number;
            var properties=new XElement(Style+"page-layout-properties",
                new XAttribute(Fo+"page-width",Pt(geometry.Width)),new XAttribute(Fo+"page-height",Pt(geometry.Height)),
                new XAttribute(Fo+"margin-left",Pt(geometry.Left)),new XAttribute(Fo+"margin-right",Pt(geometry.Right)),
                new XAttribute(Style+"print-orientation",geometry.Width>geometry.Height?"landscape":"portrait"));
            if(section.ColumnLayout is { Columns.Length:>1 })
                Issue("EXPORT_ODT_COLUMNS",section.Source??model.Source(model.BodyBlocks[section.Semantic.StartBlock]),
                    "Disposition de la section en colonnes hors profil.");
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
                        activeOdtFrames=source.Pages.FirstOrDefault(p=>p.Number==physical)?.FloatingTextFrames??[];
                        foreach(var frame in activeOdtFrames)odtFloatingIds.Add(frame.Frame.Id);
                        content=AuxiliaryBlocks(root,geometry);
                        activeOdtFrames=[];
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
    }

    void AppendOdtDrawing(XElement paragraph,SemanticSource sourceId)
    {
        foreach(var placement in activeOdtFrames.Where(f=>f.Frame.Id==sourceId.NodeId))
        {
            var frame=placement.Frame;
            if(frame.Rotation!=0)
            {
                Issue("EXPORT_ODT_FRAME_ROTATION",sourceId,"Rotation de zone de texte auxiliaire hors profil ODT.");
                continue;
            }
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
                new XAttribute(SvgOdt+"x",Pt(frame.X)),new XAttribute(SvgOdt+"y",Pt(frame.Y)),
                new XAttribute(SvgOdt+"width",Pt(frame.Width)),new XAttribute(SvgOdt+"height",Pt(frame.Height)));
            if(frame.Vector is {} vector&&AssetFor(vector) is {} asset)
                graphic.Add(new XElement(Draw+"image",new XAttribute(Xlink+"href",asset.Path),new XAttribute(Xlink+"type","simple"),
                    new XAttribute(Xlink+"show","embed"),new XAttribute(Xlink+"actuate","onLoad")));
            else if(frame.StoryId.Length>0)
            {
                var saved=activeOdtFrames;
                activeOdtFrames=[];
                try{graphic.Add(new XElement(Draw+"text-box",AuxiliaryBlocks(frame.StoryId,frame.Geometry)));}
                finally{activeOdtFrames=saved;}
            }
            else if(frame.Vector is not null)
            {Issue("EXPORT_ODT_VECTOR_UNSUPPORTED",sourceId,"Dessin flottant sans ressource vectorielle qualifiée.");continue;}
            paragraph.Add(graphic);
        }
    }

    XElement[] BuildReflowStories(bool epub)
    {
        var blocks=new List<XElement>();
        foreach(var region in snapshot.StoryRegions.Where(r=>r.Status!="Inactive"&&r.Selection?.Reference.TargetId is not null)
            .DistinctBy(r=>r.Selection!.Reference.TargetId,StringComparer.Ordinal))
        {
            string root=region.Selection!.Reference.TargetId!;
            if(!HasAuxiliaryContent(root,ignoreFolios:epub))continue;
            if(!epub)
            {
                Issue("EXPORT_RUNNING_STORY",model.Source(root),"Histoire auxiliaire hors du profil natif NOVA.");
                continue;
            }
            reflowStory=true;
            try
            {
                var layout=snapshot.Sections[region.Section-1].Format;
                blocks.Add(new XElement(Html+"aside",new XAttribute(Epub+"type","sidebar"),
                    new XAttribute("aria-label",region.Kind=="header"?"En-tête du document":"Pied du document"),
                    AuxiliaryBlocks(root,layout)));
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

    bool IsDecorativeDrawing(string id)=>placedImages.GetValueOrDefault(id)?.Decorative==true;
}
