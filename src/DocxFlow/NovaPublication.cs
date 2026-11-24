using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LecteurWord.Flow.PublicationPackages;

namespace LecteurWord.Flow;

/// <summary>Native OpusNova v27 contract from 8633f8fb457396c9015304228694afc72ee7ded6.</summary>
internal static class NovaPublication
{
    const int Version=27;
    const string Marker="\ufffc";
    static readonly JsonSerializerOptions Json=new(){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
    static readonly UTF8Encoding StrictUtf8=new(false,true);
    static readonly string[] Builtins=["Partie","Chapitre","Titre 1","Titre 2","Titre 3","Titre 4","Titre 5","Titre 6",
        "Titre 7","Titre 8","Titre 9","Titre 10","Sous-titre","Corps de texte","Citation","Légende"];

    sealed record Range(int start,int end);
    sealed record Characters(bool bold=false,bool italic=false,bool underline=false,string? family=null,
        int? size=null,int[]? highlight=null,int[]? foreground=null,string? language=null);
    sealed record Span(int start,int end,Characters attributes);
    sealed record Paragraph(int start,object attributes);
    sealed record RichBody(string text,IReadOnlyList<Span> styles,IReadOnlyList<Paragraph> paragraphs);
    sealed record Bookmark(string id,string name,Range range);
    sealed record Link(string id,Range range,object target);
    sealed record InternalTarget(string project,string target,string kind="internal");
    sealed record Resource(string id,string path,string media_type,string sha256,bool document_image=true);

    internal static IReadOnlyList<Entry> Build(StructuredPublication book,CancellationToken token)=>new Builder(book,token).Build();

    sealed class Builder
    {
        readonly StructuredPublication book;
        readonly SemanticDocument model;
        readonly CancellationToken token;
        readonly SemanticSource root;
        readonly string project,document;
        readonly List<object> definitions=[],units=[],forest=[],blocks=[],calls=[],notes=[];
        readonly Dictionary<string,Resource> resources=new(StringComparer.Ordinal);
        readonly List<Entry> entries=[];
        readonly List<Bookmark> bookmarks=[];
        readonly List<Link> links=[];
        readonly Dictionary<string,(string Id,string Name,int Start,SemanticSource Source)> openMarks=new(StringComparer.Ordinal);
        readonly HashSet<string> retainedNotes=new(StringComparer.Ordinal);
        readonly HashSet<int> newPages=[];
        readonly Dictionary<string,string> auxiliaryTitles=new(StringComparer.Ordinal);
        readonly HashSet<string> activeNotes=new(StringComparer.Ordinal);
        readonly Dictionary<string,StructuredPublication.Inline> auxiliaryImages=new(StringComparer.Ordinal);
        readonly Dictionary<string,SemanticStyle> styles;
        readonly HashSet<string> listSequences=new(StringComparer.Ordinal);
        readonly Dictionary<string,string> namedStyles=new(StringComparer.Ordinal);
        readonly HashSet<string> referencedBookmarks;
        string? part,chapter;
        int footnoteCount,endnoteCount;
        int usableWidth=160_000,usableHeight=247_000;

        internal Builder(StructuredPublication book,CancellationToken token)
        {
            this.book=book;
            this.token=token;
            model=book.Model;
            root=model.Source(model.Stories.First(s=>s.Kind=="body").RootId);
            project=Id("project");
            document=Id("document");
            styles=model.Styles.Where(s=>s.Type=="paragraph").GroupBy(s=>s.DeclaredId,StringComparer.Ordinal)
                .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
            referencedBookmarks=model.References.Where(r=>r.Kind!="bookmarkEnd").Select(r=>r.TargetId)
                .Concat(model.Fields.Select(f=>f.BookmarkId))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
        }

        internal IReadOnlyList<Entry> Build()
        {
            if(!ValidLabel(book.Metadata.Title,4096)||Encoding.UTF8.GetByteCount(book.Metadata.Author??"")>4096||
                book.Metadata.Author?.Contains('\0')==true)
                throw new InvalidDataException("Titre ou auteur incompatible avec le profil NOVA.");
            long created=Epoch(book.Metadata.Created),modified=Epoch(book.Metadata.Modified??book.Metadata.Created);
            if(created<0||modified<created)
            {
                book.Issue("EXPORT_NOVA_METADATA_DATES",book.Metadata.Origin??root.Location,
                    "Les dates DOCX sont incompatibles avec NOVA 27. Elles doivent être postérieures à l’époque Unix et la modification ne peut précéder la création.");
                return [];
            }
            if(new[]{book.Metadata.Subject,book.Metadata.Keywords,book.Metadata.Description}.Any(s=>!string.IsNullOrWhiteSpace(s)))
                book.Issue("EXPORT_NOVA_METADATA_CONVERSION",root,
                    "Les propriétés sujet, mots-clés et description restent dans le DOCX. Le projet conserve le titre, l’auteur et les dates.",false);
            for(int i=0;i<Builtins.Length;i++)
                definitions.Add(new{id=Builtin(i+1),name=Builtins[i],characters=new Characters(),
                    paragraph=new{bullet=false,alignment="left",line_height="single",before=0,after=0,left=0,right=0,first=0}});

            var extensions=new Dictionary<string,object>();
            AddPageStyles(extensions);
            var manuscriptBlocks=model.BodyBlocks.ToList();
            foreach(var (title,auxiliary) in book.NovaAuxiliary())
            {
                auxiliaryTitles.TryAdd(auxiliary[0],title);
                manuscriptBlocks.AddRange(auxiliary);
                if(model.Element(auxiliary[0]).StoryId is {} storyId)book.Represent(storyId,PublicationRepresentation.Presentation);
            }
            var body=WithAuxiliaryImages(BuildBody(manuscriptBlocks,true));
            book.Represent(root.NodeId);
            foreach(var pending in openMarks.Values)
            {
                book.Reserve("EXPORT_NOVA_BOOKMARK_END",pending.Source,"Le signet sans fin résolue est conservé comme point d’ancrage.");
                bookmarks.Add(new(pending.Id,pending.Name,new(pending.Start,pending.Start)));
            }
            var destinations=bookmarks.Select(b=>b.id).ToHashSet(StringComparer.Ordinal);
            int unresolved=links.RemoveAll(link=>link.target is InternalTarget target&&!destinations.Contains(target.target));
            if(unresolved>0)
                book.Reserve("EXPORT_NOVA_LINK_DESTINATION",root,"Les libellés sont conservés pour les liens dont la destination ne peut être reproduite.");
            if(links.Count!=0||bookmarks.Count!=0)
                extensions.Add("org.opusnova.references",new{version=1,documents=new Dictionary<string,object>
                    {{document,new{links,bookmarks}}}});
            if(body.styles.Count>100_000||body.paragraphs.Count>100_000||units.Count>100_000||
                forest.Count>2048||resources.Count>128||blocks.Count>384||notes.Count>1024||
                links.Count+bookmarks.Count>4096||definitions.Count>128||
                entries.Sum(e=>e.Data.LongLength)>16*1024*1024)
                throw new InvalidDataException("Budget structurel NOVA dépassé.");

            string textPath="documents/"+document+".txt";
            string stylePath="styles/"+document+".json";
            string anchorPath="anchors/"+document+".json";
            string? blockPath=blocks.Count>0?"blocks/"+document+".json":null;
            string? notesPath=notes.Count>0?"notes.json":null;
            entries.Add(StringEntry(textPath,body.text));
            Add(stylePath,new{version=9,spans=body.styles,paragraphs=body.paragraphs},4*1024*1024);
            Add(anchorPath,new{version=3,owner=new{project,document},units,objects=Array.Empty<object>()},32*1024*1024);
            if(blockPath is not null)Add(blockPath,blocks,8*1024*1024);
            if(notesPath is not null)Add(notesPath,new{version=1,calls,bodies=notes},4*1024*1024);
            Add("manifest.json",new
            {
                format="opus-nova",version=Version,project_id=project,active_document=document,
                metadata=new{title=book.Metadata.Title,author=book.Metadata.Author??"",created_utc=created,modified_utc=modified},
                documents=new[]{new{id=document,title=book.Metadata.Title,path=textPath,styles=stylePath,anchor_store=anchorPath,blocks=blockPath}},
                notes=notesPath,
                manuscript=forest.Count==0?null:new{schema=3,nodes=forest},
                text_styles=new{schema=3,definitions},
                resources=resources.Count==0?null:resources.Values,
                extensions
            },1024*1024);
            if(entries.Sum(e=>e.Data.LongLength)>64*1024*1024||
                entries.Single(e=>e.Path==textPath).Data.Length>32*1024*1024)
                throw new InvalidDataException("Budget de l’archive NOVA dépassé.");

            book.Feature("nova-text",PublicationRetention.Native,root.Location,"Manuscrit éditable, styles natifs et plages Unicode scalaires.");
            if(forest.Count>0)book.Feature("nova-manuscript",PublicationRetention.Native,root.Location,
                "Arborescence issue des styles explicites du DOCX, sans titre généré.");
            if(bookmarks.Count>0||links.Count>0)book.Feature("nova-references",PublicationRetention.Native,root.Location,"Signets et liens natifs.");
            return entries;
        }

        void Add(string name,object data,int limit)
        {
            byte[] encoded=JsonSerializer.SerializeToUtf8Bytes(data,Json);
            if(encoded.Length>limit)throw new InvalidDataException("Budget NOVA dépassé pour "+name+".");
            entries.Add(new(name,encoded));
        }

        void AddPageStyles(Dictionary<string,object> extensions)
        {
            var sections=book.Snapshot.Sections;
            if(sections.IsEmpty)return;
            var first=sections[0];
            foreach(var section in sections)
            {
                var location=section.Source??root;
                if(section.Format!=first.Format||section.MirrorMargins!=first.MirrorMargins||section.Gutter!=first.Gutter||
                    section.Columns is >1||section.PageNumberStart is not null and not 1||section.PageNumberFormat!="decimal")
                    book.Reserve("EXPORT_NOVA_SECTION_FORMAT",location,"Cette section reprend la géométrie et la numérotation communes du projet.");
                if(section.Semantic.Number<=1)continue;
                if(section.Start!="continuous"&&section.Semantic.StartBlock<model.BodyBlocks.Length)
                {
                    newPages.Add(section.Semantic.StartBlock);
                    if(section.Start!="nextPage")
                        book.Reserve("EXPORT_NOVA_SECTION_TRANSITION",location,"La transition de section est adaptée en saut de page.");
                }
                else
                    book.Issue("EXPORT_NOVA_CONTINUOUS_SECTION",location,"Section continue à géométrie commune intégrée au même document.",false);
            }

            var g=first.Format;
            bool landscape=g.Width>g.Height;
            int width=Microns(Math.Min(g.Width,g.Height)),height=Microns(Math.Max(g.Width,g.Height));
            int gutter=first.Gutter is {} rawGutter?Microns(PageComposer.Points(rawGutter)):0;
            if(width is <10000 or >2_000_000||height is <10000 or >2_000_000||
                g.Left<0||g.Right<0||g.Top<0||g.Bottom<0||gutter<0||
                Microns(g.Left)+Microns(g.Right)+gutter+1000>Microns(g.Width)||
                Microns(g.Top)+Microns(g.Bottom)+1000>Microns(g.Height))
            {
                book.Reserve("EXPORT_NOVA_PAGE_STYLE",first.Source??root,"Les dimensions sortent du profil OpusNova. Son format de page par défaut s’applique.");
                return;
            }
            string id=Id("page-style");
            usableWidth=Microns(g.Width)-Microns(g.Left)-Microns(g.Right)-gutter;
            usableHeight=Microns(g.Height)-Microns(g.Top)-Microns(g.Bottom);
            var disabled=new{enabled=false,text="",edge_distance=0};
            var style=new{id,name="Page source",format=new{kind="custom",width,height},orientation=landscape?"landscape":"portrait",
                margins=new{top=Microns(g.Top),bottom=Microns(g.Bottom),inner=Microns(g.Left),outer=Microns(g.Right),gutter},
                facing_pages=first.MirrorMargins,typography=new{family="DejaVu Serif",size_tenths_pt=120,line_height_permille=1000},
                header=disabled,footer=disabled,notes=(object?)null};
            extensions.Add("org.opusnova.page_styles",new{version=1,active=id,styles=new[]{style}});
            book.Feature("nova-page-style",PublicationRetention.Native,(first.Source??root).Location,"Géométrie source et marges du projet.");
        }

        RichBody BuildBody(IEnumerable<string> ids,bool main,int baseOffset=0,bool textFrame=false)
        {
            var text=new StringBuilder();
            var spans=new List<Span>();
            var paragraphs=new List<Paragraph>();
            int offset=baseOffset,index=0;
            string[] sequence=ids.ToArray();
            foreach(string id in sequence)
            {
                token.ThrowIfCancellationRequested();
                if(index>0)Append("\n");
                bool pageBefore=main&&!textFrame&&newPages.Contains(index);
                index++;
                var source=model.Source(id);
                if(main&&!textFrame&&auxiliaryTitles.TryGetValue(id,out var auxiliaryTitle))
                {
                    Append(auxiliaryTitle);
                    Append("\n");
                }
                int start=offset;
                if(book.LayoutTable(id) is {} table)
                {
                    if(main&&AddTable(table,offset))
                    {
                        if(pageBefore)paragraphs.Add(new(start,new {typography=new{page_before=true},bullet=false,
                            alignment="left",line_height="single",before=0,after=0,left=0,right=0,first=0}));
                        Append(Marker);
                        book.Represent(id);
                    }
                    else
                    {
                        book.Reserve("EXPORT_NOVA_TABLE_TEXT",source,"Les contenus des cellules restent éditables. La grille est reprise en texte délimité.");
                        Append(TableText(table));
                        book.Represent(id,PublicationRepresentation.Presentation);
                    }
                    continue;
                }
                if(book.Paragraph(id) is not {} paragraph)
                {
                    book.Reserve("EXPORT_NOVA_STRUCTURE",source,"Ce bloc hors profil reste dans le DOCX.");
                    continue;
                }
                var p=paragraph.Properties;
                var inlines=book.ReadInlines(id);
                var (style,kind)=StyleOf(paragraph);
                var paragraphAttributes=Attributes(paragraph,style,pageBefore,
                    modelSide(index-2),modelSide(index));
                paragraphs.Add(new(start,paragraphAttributes));
                string? literalMarker=ListText(paragraph);
                if(literalMarker is not null)Append(literalMarker);
                int lineStart=start, headingEnd=-1;
                bool separateImage=false;
                bool afterFrame=false;
                bool pendingPage=false;
                int lastReal=Array.FindLastIndex(inlines,i=>i.Image is not null||i.Note is not null||
                    i.TextFrame is not null||
                    i.Value is {} value&&value.Any(c=>!char.IsWhiteSpace(c)));
                for(int inlineIndex=0;inlineIndex<inlines.Length;inlineIndex++)
                {
                    var inline=inlines[inlineIndex];
                    if(inline.TextFrame is {} frame)
                    {
                        if(headingEnd<0)headingEnd=offset;
                        if(offset>lineStart||separateImage||afterFrame)Append("\n");
                        var frameBody=BuildBody(book.TextFrameBlocks(frame),main,offset,textFrame:true);
                        if(frameBody.paragraphs.Count>0&&paragraphs.Count>0&&paragraphs[^1].start==offset)
                            paragraphs.RemoveAt(paragraphs.Count-1);
                        Append(frameBody.text);
                        spans.AddRange(frameBody.styles);
                        paragraphs.AddRange(frameBody.paragraphs);
                        book.Represent(frame.StoryId,PublicationRepresentation.Presentation);
                        book.Represent(inline.Source.NodeId,PublicationRepresentation.Presentation);
                        lineStart=offset;
                        separateImage=false;
                        afterFrame=true;
                        continue;
                    }
                    if(afterFrame&&(inline.Value is not null||inline.Image is not null||inline.Note is not null))
                    {
                        Append("\n");
                        lineStart=offset;
                        paragraphs.Add(new(lineStart,Attributes(paragraph,null,false,includeList:false)));
                        afterFrame=false;
                    }
                    if(inline.Field=="BREAK")
                    {
                        if(offset!=lineStart)
                        {
                            if(headingEnd<0)headingEnd=offset;
                            Append("\n");
                        }
                        lineStart=offset;
                        pendingPage=true;
                        separateImage=false;
                        continue;
                    }
                    if(inline.Image is not null)
                    {
                        var alternative=book.ObjectText(inline.Source.NodeId);
                        if(main)
                        {
                            if(separateImage||offset>lineStart)
                            {
                                if(headingEnd<0)headingEnd=offset;
                                Append("\n");
                                lineStart=offset;
                                book.Reserve("EXPORT_NOVA_IMAGE_ANCHOR",inline.Source,
                                    "L’image reste intégrée dans un paragraphe dédié, à son emplacement dans le flux.");
                            }
                            if(pendingPage)SetPageBefore();
                            if(AddImage(inline,p,offset))
                            {
                                Append(Marker);
                                book.RepresentImage(inline);
                                separateImage=true;
                            }
                            else if(alternative is not null)Append(alternative);
                            if(inline.Link is not null)
                                book.Reserve("EXPORT_NOVA_IMAGE_LINK",inline.Source,"Le lien attaché à l’image reste dans le DOCX.");
                        }
                        else
                        {
                            auxiliaryImages.TryAdd(inline.Source.NodeId,inline);
                            book.Reserve("EXPORT_NOVA_IMAGE_IN_AUXILIARY",inline.Source,
                                "La figure est intégrée en annexe. Son emplacement d’origine reste indiqué dans la note ou cellule.");
                            Append(alternative??"[Illustration en annexe]");
                        }
                        continue;
                    }
                    if(separateImage)
                    {
                        Append("\n");
                        lineStart=offset;
                        separateImage=false;
                        book.Reserve("EXPORT_NOVA_IMAGE_ANCHOR",inline.Source,
                            "Le texte qui suit l’image reste éditable dans un paragraphe séparé.");
                    }
                    if(pendingPage)SetPageBefore();
                    string story=model.Element(inline.Source.NodeId).StoryId??"";
                    if(inline.Anchor is {} mark)
                    {
                        string? name=model.Attribute(inline.Source.NodeId,"name");
                        string? declared=model.Attribute(inline.Source.NodeId,"id");
                        if(!main&&name is not null&&IsOpusGeneratedAnchor(name)&&!referencedBookmarks.Contains(inline.Source.NodeId))
                        {
                            book.Issue("EXPORT_NOVA_NOTE_TECHNICAL_MARK",inline.Source,
                                "Marque technique d’export Word d’OpusNova non référencée. Le corps de note conserve son ancrage natif.",false);
                            continue;
                        }
                        if(!main||name is null||!ValidLabel(name,4096)||declared is null||
                            !openMarks.TryAdd(story+":"+declared,(Id("bookmark:"+mark),name,offset,inline.Source)))
                            book.Reserve("EXPORT_NOVA_BOOKMARK",inline.Source,"Ce signet ne peut être reproduit dans cette partie du projet.");
                        continue;
                    }
                    if(inline.AnchorEnd is {} end)
                    {
                        if(main&&openMarks.Remove(story+":"+end,out var markData))
                            bookmarks.Add(new(markData.Id,markData.Name,new(markData.Start,offset)));
                        continue;
                    }
                    if(inline.Note is {} note)
                    {
                        if(main&&AddNote(inline,note,offset))Append(Marker);
                        else if(note.TargetId is {} target&&activeNotes.Add(target))
                        {
                            book.Reserve("EXPORT_NOVA_NESTED_NOTE",inline.Source,"Le corps de note est repris au point d’appel comme texte éditable.");
                            try
                            {
                                book.LoadStoryLayout(target,book.Snapshot.Sections.FirstOrDefault()?.Format??new());
                                string noteText=BuildBody(model.BlocksOf(target).Select(e=>e.Id),false).text;
                                Append(" ["+noteText.Replace('\n',' ') +"]");
                                book.Represent(target,PublicationRepresentation.Presentation);
                                book.Represent(inline.Source.NodeId,PublicationRepresentation.Presentation);
                            }
                            finally{activeNotes.Remove(target);}
                        }
                        continue;
                    }
                    if(inline.Value is not {} value)continue;
                    if(inlineIndex>lastReal&&value.Contains('\t'))
                    {
                        value=value.Replace('\t',' ');
                        book.Reserve("EXPORT_NOVA_TRAILING_TABS",inline.Source,
                            "Les tabulations de remplissage en fin de paragraphe deviennent des espaces éditables.");
                    }
                    if(value.Contains('\0')||value.Contains('\ufffc'))
                        book.Issue("EXPORT_NOVA_CONTROL",inline.Source,"Caractère de contrôle réservé par le format NOVA.");
                    if(value.Contains('\n')||inline.Style is {Tracking:not 0} or {RightToLeft:true}||
                        inline.Style is {} size&&Math.Abs(size.Size*10-MathF.Round(size.Size*10))>.01f)
                        book.Reserve("EXPORT_NOVA_INLINE",inline.Source,"Le texte reste éditable. Espacement, sens des caractères ou coupure interne adaptés.");
                    int begin=offset;
                    int breakAt=value.IndexOf('\n');
                    if(headingEnd<0&&breakAt>=0)headingEnd=offset+value[..breakAt].EnumerateRunes().Count();
                    Append(value);
                    if(value.Contains('\n'))lineStart=offset-value[(value.LastIndexOf('\n')+1)..].EnumerateRunes().Count();
                    if(inline.Style is {} format&&begin<offset)
                    {
                        string? language=format.Language switch{"fr" or "fr-FR"=>"fr","en-US"=>"en-us","en-GB"=>"en-gb","und"=>"und",_=>null};
                        if(language is null)
                            book.Issue("EXPORT_NOVA_LANGUAGE",inline.Source,"Le texte est conservé. La langue de correction du passage dépasse le profil NOVA.",false);
                        var attributes=new Characters(format.Bold,format.Italic,format.Underline=="single",
                            StructuredPublication.Family(inline),(int)MathF.Round(format.Size*10),Rgb(format.Background),Rgb(format.Color),language);
                        if(spans.Count>0&&spans[^1].end==begin&&Same(spans[^1].attributes,attributes))
                            spans[^1]=spans[^1] with{end=offset};
                        else spans.Add(new(begin,offset,attributes));
                    }
                    if(inline.Link is {} link)
                    {
                        if(main)AddLink(inline,link,begin,offset);
                        else book.Reserve("EXPORT_NOVA_LINK_IN_AUXILIARY",inline.Source,"Le libellé du lien reste dans la note ou cellule.");
                    }
                    else if(inline.Target is {} destination)
                    {
                        if(main)AddLink(inline,new(inline.Source.NodeId,"bookmark",null,null,destination,null,ReferenceStatus.Resolved),begin,offset);
                        else book.Reserve("EXPORT_NOVA_LINK_IN_AUXILIARY",inline.Source,"Le libellé du renvoi reste dans la note ou cellule.");
                    }
                }
                if(pendingPage)SetPageBefore();
                book.Represent(id,PublicationRepresentation.Compatible);
                if(main&&!textFrame&&kind is not null&&style is not null)
                    AddNode(kind,style,id,source,start,headingEnd>=0?headingEnd:offset);

                void SetPageBefore()
                {
                    if(paragraphs[^1].start==lineStart)paragraphs[^1]=new(lineStart,Attributes(paragraph,style,true));
                    else paragraphs.Add(new(lineStart,Attributes(paragraph,null,true,includeList:false)));
                    pendingPage=false;
                }

                LayoutParagraph? modelSide(int position)=>position>=0&&position<sequence.Length?book.Paragraph(sequence[position]):null;
            }
            return new(text.ToString(),spans,paragraphs);

            void Append(string value)
            {
                if(text.Length+value.Length>32*1024*1024)throw new InvalidDataException("Budget de texte NOVA dépassé.");
                text.Append(value);
                offset+=value.EnumerateRunes().Count();
            }
        }

        RichBody WithAuxiliaryImages(RichBody body)
        {
            if(auxiliaryImages.Count==0)return body;
            var text=new StringBuilder(body.text);
            const string heading="Illustrations des notes et tableaux";
            int offset=body.text.EnumerateRunes().Count();
            int count=0;
            foreach(var image in auxiliaryImages.Values)
            {
                token.ThrowIfCancellationRequested();
                string prefix=count==0?"\n"+heading+"\n":"\n";
                int anchor=offset+prefix.EnumerateRunes().Count();
                if(!AddImage(image,new(),anchor))continue;
                text.Append(prefix).Append(Marker);
                offset=anchor+1;
                count++;
                book.RepresentImage(image);
                book.Represent(image.Source.NodeId,PublicationRepresentation.Presentation);
            }
            return body with{text=text.ToString()};
        }

        (string? Style,string? Kind) StyleOf(LayoutParagraph paragraph)
        {
            if(styles.TryGetValue(paragraph.StyleId,out var definition)&&model.ChildId(definition.Id,"name") is {} nameId)
            {
                string name=model.Attribute(nameId,"val")??"";
                var semantic=name.ToLowerInvariant() switch
                {
                    "partie" or "part" => (Builtin(1),"part"),
                    "chapitre" or "chapter" => (Builtin(2),"chapter"),
                    "scène" or "scene" => (Builtin(5),"scene"),
                    _ => (null,null)
                };
                if(semantic.Item1 is not null)return semantic;
                if(paragraph.Properties.Heading==0&&ValidLabel(name,256))
                {
                    if(!namedStyles.TryGetValue(definition.Id,out var id))
                    {
                        id=Id("style:"+definition.Id);
                        namedStyles.Add(definition.Id,id);
                        definitions.Add(new{id,name,characters=new Characters(),
                            paragraph=new{bullet=false,alignment="left",line_height="single",before=0,after=0,left=0,right=0,first=0}});
                    }
                    return (id,null);
                }
            }
            return (paragraph.Properties.Heading is >=1 and <=10?Builtin(paragraph.Properties.Heading+2):null,null);
        }

        object Attributes(LayoutParagraph paragraph,string? style,bool pageBefore,LayoutParagraph? previous=null,LayoutParagraph? next=null,bool includeList=true)
        {
            var p=paragraph.Properties;
            int left=Pixels(p.Left),right=Pixels(p.Right),first=Pixels(p.FirstLine);
            int before=p.ContextualSpacing&&SameStyle(previous)?0:Pixels(p.Before);
            int after=p.ContextualSpacing&&SameStyle(next)?0:Pixels(p.After);
            if(p.ContextualSpacing)
                book.Issue("EXPORT_NOVA_CONTEXTUAL_SPACING",model.Source(paragraph.Id),
                    "Les espacements effectifs sont fixés selon les paragraphes voisins du DOCX.",false);
            if(p.HasBorders||p.Shading is not null||
                new[]{left,right,before,after}.Any(n=>n is <0 or >300)||first is <-300 or >300||left+first<0||
                p.Alignment is not ("left" or "right" or "center" or "both" or "justify"))
                book.Reserve("EXPORT_NOVA_PARAGRAPH_STYLE",model.Source(paragraph.Id),"Le texte est conservé. Bordure, trame, espacement ou retrait adapté au profil NOVA.");
            left=Math.Clamp(left,0,300);right=Math.Clamp(right,0,300);
            before=Math.Clamp(before,0,300);after=Math.Clamp(after,0,300);first=Math.Clamp(first,-left,300);
            int spacing=(int)MathF.Round(p.Line*1000/240);
            if(p.LineRule!="auto"||spacing is <1000 or >3000)
                book.Reserve("EXPORT_NOVA_LINE_SPACING",model.Source(paragraph.Id),"L’interligne est converti dans la plage relative OpusNova.");
            if(p.LineRule!="auto")spacing=(int)MathF.Round(p.Line/20/12*1000);
            spacing=Math.Clamp(spacing,1000,3000);
            var tabs=p.Tabs.Select(t=>new
            {
                position_um=Microns(t.Position),
                alignment=t.Alignment=="numbering"?"left":t.Alignment,
                leader=t.Leader switch{"dot"=>"dots","hyphen"=>"dashes","underscore"=>"underline",_=>"none"},
                @decimal="."
            }).OrderBy(t=>t.position_um).ToArray();
            int tabLimit=Math.Clamp(usableWidth-(left+right)*25400/96-250,0,2_000_000);
            if(tabs.Length>32||tabs.Select(t=>t.position_um).Distinct().Count()!=tabs.Length||
                tabs.Any(t=>t.position_um<1||t.position_um>tabLimit||t.alignment is not ("left" or "right" or "center" or "decimal"))||
                p.Tabs.Any(t=>t.Leader is not ("" or "none" or "dot" or "hyphen" or "underscore")))
                book.Reserve("EXPORT_NOVA_TABS",model.Source(paragraph.Id),"Les tabulations restent dans le texte. Certains taquets ou caractères de conduite sont adaptés.");
            tabs=tabs.Where(t=>t.position_um>=1&&t.position_um<=tabLimit&&t.alignment is ("left" or "right" or "center" or "decimal"))
                .DistinctBy(t=>t.position_um).Take(32).ToArray();
            if(tabs.Any(t=>t.alignment=="decimal"))
                book.Reserve("EXPORT_NOVA_DECIMAL_TAB",model.Source(paragraph.Id),"Séparateur de tabulation décimale non établi pour le projet NOVA.");
            var (list,format)=includeList?ListOf(paragraph):(null,null);
            return new
            {
                style,typography=new{keep_next=p.KeepNext,keep_together=p.KeepLines,page_before=pageBefore||p.PageBreakBefore,
                    widows=p.Widow?2:1,orphans=p.Widow?2:1},
                list,list_format=format,line_spacing=spacing is >=1000 and <=3000?spacing:(int?)null,tabs,
                bullet=false,alignment=p.Alignment switch{"both" or "justify"=>"justified","right" or "center"=>p.Alignment,_=>"left"},
                line_height="single",before,after,left,right,first
            };

            bool SameStyle(LayoutParagraph? neighbor)=>neighbor is not null&&neighbor.Section==paragraph.Section&&neighbor.StyleId==paragraph.StyleId;
        }

        readonly Dictionary<string,(object? List,object? Format)> listPlans=new(StringComparer.Ordinal);

        string? ListText(LayoutParagraph paragraph)=>
            ListOf(paragraph).List is null&&paragraph.List is {Text.Length:>0} label?label.Text+(label.Suffix=="nothing"?"":" "):null;

        (object? List,object? Format) ListOf(LayoutParagraph paragraph)
        {
            if(listPlans.TryGetValue(paragraph.Id,out var previous))return previous;
            var plan=BuildList(paragraph);
            listPlans.Add(paragraph.Id,plan);
            return plan;
        }

        (object? List,object? Format) BuildList(LayoutParagraph paragraph)
        {
            if(paragraph.List is not {} label||label.Numbering is not {} numbering||
                numbering.LevelId is not {} level)
            {
                if(paragraph.Properties.NumId is not null and not "0")
                    book.Reserve("EXPORT_NOVA_LIST",model.Source(paragraph.Id),"Le texte est conservé. La définition ou le marqueur de la liste est indisponible.");
                return (null,null);
            }
            string? Val(string name)=>model.ChildId(level,name) is {} child?model.Attribute(child,"val"):null;
            string format=Val("numFmt")??"decimal",pattern=Val("lvlText")??"";
            string? kind=format switch
            {
                "bullet"=>"bullet","decimal"=>"decimal","lowerLetter"=>"lower_alpha","upperLetter"=>"upper_alpha",
                "lowerRoman"=>"lower_roman","upperRoman"=>"upper_roman",_=>null
            };
            string counter="%"+(label.Level+1);
            string closing=pattern==counter+"."?"dot":pattern==counter+")"?"parenthesis":"none";
            var p=paragraph.Properties;
            int gap=Pixels(-p.FirstLine),step=Pixels(p.Left)/(label.Level+1);
            int? restart=null;
            if(listSequences.Add(label.NumId+":"+label.Level))
            {
                string? start=numbering.OverrideId is {} o&&model.ChildId(o,"startOverride") is {} s?model.Attribute(s,"val"):Val("start");
                if(int.TryParse(start,NumberStyles.None,CultureInfo.InvariantCulture,out int n)&&n!=1)restart=n;
            }
            if(kind is null||label.Level is <0 or >9||restart is <1 or >1_000_000||
                format!="bullet"&&pattern!=counter&&closing=="none"||format=="bullet"&&!ValidLabel(pattern,96)||
                label.Suffix!="tab"||gap is <8 or >150||step is <8 or >100||
                model.ChildId(level,"lvlRestart") is not null)
            {
                book.Reserve("EXPORT_NOVA_LIST",model.Source(paragraph.Id),"Le libellé de liste est repris dans le texte éditable. Sa numérotation n’est plus dynamique.");
                return (null,null);
            }
            // NOVA places the list indent itself. Its projection is qualified only for this regular grid.
            if(Pixels(p.Left)!=(label.Level+1)*step)
                book.Reserve("EXPORT_NOVA_LIST_INDENT",model.Source(paragraph.Id),"Le retrait de liste est adapté à la grille NOVA.");
            var alignment=label.Alignment is "center" or "right"?label.Alignment:"left";
            return (new{sequence=Id("sequence:"+label.NumId),id=Id("list:"+paragraph.Id),level=label.Level,restart},
                new{kind,marker=format=="bullet"?pattern:"•",closing,alignment,step,gap});
        }

        void AddNode(string kind,string style,string id,SemanticSource source,int start,int end)
        {
            if(start==end||kind=="scene"&&chapter is null)
            {
                book.Reserve("EXPORT_NOVA_MANUSCRIPT",source,"Le paragraphe est conservé. Son rattachement à l’arborescence n’est pas établi.");
                return;
            }
            string unit=Id("unit:"+id),node=Id("node:"+id);
            string? parent=kind=="chapter"?part:kind=="scene"?chapter:null;
            units.Add(new{id=unit,kind="paragraph",range=new Range(start,end)});
            forest.Add(new{id=node,kind,parent,document,unit,style});
            if(kind=="part"){part=node;chapter=null;}
            else if(kind=="chapter")chapter=node;
        }

        void AddLink(StructuredPublication.Inline inline,SemanticReference link,int start,int end)
        {
            if(start==end)return;
            object target;
            if(link is {Status:ReferenceStatus.Resolved,TargetId:{} node})
            {
                string bookmark=Id("bookmark:"+book.Id(node));
                target=new InternalTarget(project,bookmark);
            }
            else if(NativeAddress(link.Relation?.Target) is {} address)
                target=new{kind="external",address};
            else
            {
                book.Reserve("EXPORT_NOVA_LINK",inline.Source,"Le texte du lien reste éditable. Sa destination ne peut être activée.");
                return;
            }
            if(links.Count>0&&links[^1].range.end==start&&Equals(links[^1].target,target))
                links[^1]=links[^1] with{range=links[^1].range with{end=end}};
            else links.Add(new(Id("link:"+link.SourceId+":"+start.ToString(CultureInfo.InvariantCulture)),new(start,end),target));
        }

        bool AddNote(StructuredPublication.Inline inline,SemanticReference note,int offset)
        {
            if(note.TargetId is not {} target||!retainedNotes.Add(target))
            {
                book.Reserve("EXPORT_NOVA_NOTE",inline.Source,"L’appel de note partagé est conservé sous forme de texte.");
                return false;
            }
            var geometry=book.Snapshot.Sections.FirstOrDefault()?.Format??new();
            book.LoadStoryLayout(target,geometry);
            if(!activeNotes.Add(target))return false;
            RichBody body;
            try{body=BuildBody(model.BlocksOf(target).Select(e=>e.Id),false);}
            finally{activeNotes.Remove(target);}
            if(Encoding.UTF8.GetByteCount(body.text)>256*1024)
                book.Issue("EXPORT_NOVA_NOTE_SIZE",inline.Source,"Budget NOVA dépassé pour le corps de note.");
            string noteId=Id("note:"+target);
            bool end=note.Kind=="endnote";
            string kind=end?"book_endnote":"footnote";
            int number=end?++endnoteCount:++footnoteCount;
            if(book.NoteLabel(inline.Source.NodeId) is {} label&&label!=number.ToString(CultureInfo.InvariantCulture))
                book.Issue("EXPORT_NOVA_NOTE_RENUMBERED",inline.Source,
                    "Le corps et l’appel sont conservés. OpusNova applique sa numérotation native à cette note.",false);
            calls.Add(new{note_id=noteId,document_id=document,anchor=new{scalar_offset=offset}});
            notes.Add(new{id=noteId,owner_document_id=document,kind,body});
            book.Represent(target);
            book.Represent(inline.Source.NodeId);
            book.Feature("nova-notes",PublicationRetention.Native,inline.Source.Location,"Appel et corps de note éditable.");
            return true;
        }

        bool AddImage(StructuredPublication.Inline inline,ParagraphLayout p,int offset)
        {
            var image=inline.Image!;
            string alternative=book.ObjectText(inline.Source.NodeId)??"";
            var asset=book.AssetFor(image);
            int width=Microns(image.Width),height=Microns(image.Height);
            if(width>0&&height>0)
            {
                double fit=Math.Min((usableWidth-32d)/width,(usableHeight-32d)/height);
                if(fit<1)
                {
                    width=(int)(width*fit);
                    height=(int)(height*fit);
                    book.Reserve("EXPORT_NOVA_IMAGE_SIZE",inline.Source,"L’image est réduite proportionnellement à la place disponible.");
                }
            }
            if(asset is not {Mime:"image/png" or "image/jpeg"}||
                width is <1000 or >2_000_000||height is <1000 or >2_000_000||
                (long)image.PixelWidth*image.PixelHeight>32_000_000||asset.Data.Length>4*1024*1024||
                alternative.Contains('\0'))
            {
                book.Reserve("EXPORT_NOVA_IMAGE",inline.Source,"L’image ne peut être intégrée dans ce projet. Sa description est conservée lorsqu’elle existe.");
                return false;
            }
            if(Encoding.UTF8.GetByteCount(alternative)>4096)
            {
                book.Reserve("EXPORT_NOVA_IMAGE_DESCRIPTION",inline.Source,"La description de l’image est limitée par le format NOVA.");
                alternative=string.Concat(alternative.EnumerateRunes().Take(1000).Select(r=>r.ToString()));
            }
            string hash=Convert.ToHexStringLower(SHA256.HashData(asset.Data));
            if(!resources.TryGetValue(hash,out var resource))
            {
                string id=Id("resource:"+hash);
                resource=new(id,"resources/"+id+".bin",asset.Mime,hash);
                resources.Add(hash,resource);
                entries.Add(new(resource.path,asset.Data));
            }
            string? role=alternative.Length>0?"informative":image.Decorative==true?"decorative":null;
            string alignment=p.Alignment is "center" or "right"?p.Alignment:"left";
            blocks.Add(new{id=Id("image:"+inline.Source.NodeId),anchor=offset,image=new
                {resource=resource.id,width,height,alignment,lock_aspect=false,alternative,role}});
            book.Feature("nova-images",PublicationRetention.Native,inline.Source.Location,"Image intégrée, bloc et description éditables.");
            return true;
        }

        string TableText(LayoutTable table)
        {
            return string.Join("\n",table.Cells.Where(c=>c.MergeOrigin is null).GroupBy(c=>c.Row).OrderBy(g=>g.Key)
                .Select(row=>string.Join("\t",row.OrderBy(c=>c.Column).Select(cell=>
                    BuildBody(model.BlocksOf(cell.Id).Select(e=>e.Id),false).text.Replace('\n',' ')))));
        }

        bool AddTable(LayoutTable table,int offset)
        {
            int rowCount=table.Rows.Count,columnCount=table.Columns.Count;
            var source=model.Source(table.Id);
            if(rowCount is <1 or >1024||columnCount is <1 or >32||rowCount*columnCount>8192||
                blocks.Count>=384)return false;
            var origins=table.Cells.Where(c=>c.MergeOrigin is null).OrderBy(c=>c.Row).ThenBy(c=>c.Column).ToArray();
            var covered=new bool[rowCount,columnCount];
            foreach(var cell in origins)
            {
                if(cell.Row<0||cell.Column<0||cell.RowSpan<1||cell.ColumnSpan<1||
                    cell.RowSpan>rowCount-cell.Row||cell.ColumnSpan>columnCount-cell.Column)return false;
                for(int row=cell.Row;row<cell.Row+cell.RowSpan;row++)
                    for(int column=cell.Column;column<cell.Column+cell.ColumnSpan;column++)
                    {
                        if(covered[row,column])return false;
                        covered[row,column]=true;
                    }
            }

            int maxWidth=Math.Min(usableWidth-32,2_000_000);
            if(maxWidth<columnCount*4000)return false;
            int[] widths=table.Columns.Select(w=>Math.Clamp(Microns(w),4000,maxWidth)).ToArray();
            long total=widths.Sum(w=>(long)w);
            if(total>maxWidth)
            {
                double scale=(maxWidth-columnCount*4000d)/(total-columnCount*4000);
                widths=widths.Select(w=>(int)((w-4000)*scale)+4000).ToArray();
            }
            int headers=table.Rows.TakeWhile(r=>r.Header).Count();
            int retainedHeaders=origins.Where(c=>c.Row<headers&&c.Row+c.RowSpan>headers)
                .Select(c=>c.Row).DefaultIfEmpty(headers).Min();
            if(!widths.SequenceEqual(table.Columns.Select(Microns))||retainedHeaders!=headers||
                table.Rows.Skip(headers).Any(r=>r.Header)||table.Rows.Any(r=>r.HeightRule=="exact")||
                table.Alignment!="left"||Math.Abs(table.Indent)>.01f)
                book.Reserve("EXPORT_NOVA_TABLE_LAYOUT",source,"La grille et les fusions restent éditables. Largeurs, en-têtes ou alignement adaptés.");
            if(table.Rows.Any(r=>!r.CantSplit))
                book.Issue("EXPORT_NOVA_TABLE_ROW_LAYOUT",source,"OpusNova conserve chaque ligne en un bloc lors de sa repagination.",false);
            var rows=table.Rows.Select(r=>new{id=Id(table.Id+":row:"+r.Index),minimum=Math.Clamp(Microns(r.Height),0,2_000_000),indivisible=true});
            var columns=widths.Select((w,i)=>new{id=Id(table.Id+":column:"+i),width=w});
            var outer=BorderOf([table.Borders.Top,table.Borders.Right,table.Borders.Bottom,table.Borders.Left],source);
            var inner=BorderOf([table.Borders.InsideH,table.Borders.InsideV],source);
            var cells=new SortedDictionary<(int Row,int Column),object>();
            foreach(var cell in origins)
            {
                token.ThrowIfCancellationRequested();
                int maxPadding=(widths.Skip(cell.Column).Take(cell.ColumnSpan).Sum()-Math.Max(outer.width,inner.width)-1)/2;
                int padding=Math.Clamp(Microns(cell.Margins.Left),0,Math.Min(20000,maxPadding));
                if(Microns(cell.Margins.Left)!=padding||cell.Margins.Right!=cell.Margins.Left||
                    cell.Margins.Top!=cell.Margins.Left||cell.Margins.Bottom!=cell.Margins.Left)
                    book.Reserve("EXPORT_NOVA_TABLE_PADDING",model.Source(cell.Id),"Les marges intérieures de la cellule sont uniformisées.");
                if(!SameBorder(cell.Borders.Top,cell.Row==0?table.Borders.Top:table.Borders.InsideH)||
                    !SameBorder(cell.Borders.Bottom,cell.Row+cell.RowSpan==rowCount?table.Borders.Bottom:table.Borders.InsideH)||
                    !SameBorder(cell.Borders.Left,cell.Column==0?table.Borders.Left:table.Borders.InsideV)||
                    !SameBorder(cell.Borders.Right,cell.Column+cell.ColumnSpan==columnCount?table.Borders.Right:table.Borders.InsideV))
                    book.Reserve("EXPORT_NOVA_CELL_BORDER",model.Source(cell.Id),"La bordure particulière de la cellule reprend le style du tableau.");
                var body=BuildBody(model.BlocksOf(cell.Id).Select(e=>e.Id),false);
                if(Encoding.UTF8.GetByteCount(body.text)>256*1024)
                    book.Issue("EXPORT_NOVA_TABLE_CELL_LIMIT",model.Source(cell.Id),"La taille du contenu dépasse le budget NOVA par cellule.");
                string vertical=cell.VerticalAlignment;
                if(vertical is not ("top" or "center" or "bottom"))
                {
                    vertical="top";
                    book.Reserve("EXPORT_NOVA_TABLE_VERTICAL",model.Source(cell.Id),"L’alignement vertical est adapté.");
                }
                cells.Add((cell.Row,cell.Column),new{id=Id("cell:"+cell.Id),row=cell.Row,column=cell.Column,row_span=cell.RowSpan,
                    column_span=cell.ColumnSpan,body,padding,background=Rgb(cell.Fill),vertical});
            }
            var empty=new{text=""};
            bool filled=false;
            for(int row=0;row<rowCount;row++)
                for(int column=0;column<columnCount;column++)
                    if(!covered[row,column])
                    {
                        cells.Add((row,column),new{id=Id(table.Id+":empty:"+row+":"+column),row,column,row_span=1,
                            column_span=1,body=empty,padding=0,background=(int[]?)null,vertical="top"});
                        filled=true;
                    }
            if(filled)
                book.Reserve("EXPORT_NOVA_TABLE_GRID",source,"Les positions sans cellule dans la grille Word sont matérialisées par des cellules vides.");
            string? properties=model.ChildId(table.Id,"tblPr");
            string CaptionValue(string property)=>properties is not null&&model.ChildId(properties,property) is {} node?
                model.Attribute(node,"val")??"":"";
            var caption=new{id=Id("caption:"+table.Id),placement="above",title=new{text=CaptionValue("tblCaption")},
                description=new{text=CaptionValue("tblDescription")},source=empty,notes=empty};
            blocks.Add(new{id=Id("table:"+table.Id),anchor=offset,table=new{rows,columns,cells=cells.Values,
                header_rows=retainedHeaders,appearance=new{outer,inner},caption}});
            book.Feature("nova-tables",PublicationRetention.Native,source.Location,"Tableau éditable, grille et fusions natives.");
            return true;
        }

        sealed record NativeBorder(int width,int[] color);
        NativeBorder BorderOf(LayoutBorder?[] borders,SemanticSource source)
        {
            var edge=borders.OrderByDescending(b=>b?.Width??0).First();
            if(borders.Any(b=>!SameBorder(b,edge))||edge is {Width:>0,Style:not "single"}||
                edge is not null&&Microns(edge.Width)>3999)
                book.Reserve("EXPORT_NOVA_TABLE_BORDER",source,"Les bordures du tableau sont uniformisées dans le profil NOVA.");
            return new(Math.Clamp(Microns(edge?.Width??0),0,3999),Rgb(edge?.Color)??[0,0,0]);
        }

        string Id(string key)
        {
            byte[] hash=SHA256.HashData(Encoding.UTF8.GetBytes("lecteur-word:nova:v27:"+book.SourceHash+":"+key));
            hash[6]=(byte)((hash[6]&15)|80);
            hash[8]=(byte)((hash[8]&63)|128);
            return new Guid(hash.AsSpan(0,16),bigEndian:true).ToString("D");
        }
    }

    static string Builtin(int n)=>"00000000-0000-0000-0000-"+n.ToString("x12",CultureInfo.InvariantCulture);
    static bool IsOpusGeneratedAnchor(string name)=>
        name.StartsWith("OpusA",StringComparison.Ordinal)&&name.Length>5&&name.AsSpan(5).IndexOfAnyExceptInRange('0','9')<0||
        name.StartsWith("OpusN",StringComparison.Ordinal)&&name.Length==37&&name.AsSpan(5).ToArray().All(char.IsAsciiHexDigit);
    static bool ValidLabel(string value,int max)=>!string.IsNullOrWhiteSpace(value)&&Encoding.UTF8.GetByteCount(value)<=max&&
        !value.Any(char.IsControl);
    static int Pixels(float pt)=>(int)MathF.Round(pt*4/3);
    static int Microns(float pt)=>(int)MathF.Round(pt*(25400f/72));
    static long Epoch(DateTimeOffset? date)=>(date??DateTimeOffset.UnixEpoch).ToUnixTimeSeconds();

    static string? NativeAddress(string? target)
    {
        if(Publications.SafeUrl(target) is not {} address||address.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c)||c=='\\'))
            return null;
        byte[] bytes=Encoding.UTF8.GetBytes(address);
        if(bytes.Length>8192)return null;
        int decodedLength=0;
        for(int i=0;i<bytes.Length;i++)
        {
            byte value=bytes[i];
            if(value=='%')
            {
                if(i+2>=bytes.Length||!byte.TryParse(bytes.AsSpan(i+1,2),NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,out value)||value<32||value==127)return null;
                i+=2;
            }
            bytes[decodedLength++]=value;
        }
        try{_ = StrictUtf8.GetCharCount(bytes.AsSpan(0,decodedLength));}
        catch(DecoderFallbackException){return null;}

        var uri=new Uri(address);
        if(uri.Scheme is "http" or "https")return uri.Host.Length>0?address:null;
        if(address.Contains('#'))return null;
        int query=address.IndexOf('?');
        string mailbox=Uri.UnescapeDataString(address["mailto:".Length..(query<0?address.Length:query)]);
        int at=mailbox.IndexOf('@');
        return at>0&&at<mailbox.Length-1&&mailbox.LastIndexOf('@')==at&&
            mailbox.IndexOfAny(['/','#','<','>','"'])<0?address:null;
    }
    static bool SameBorder(LayoutBorder? a,LayoutBorder? b)=>a==b||(a?.Width??0)==0&&(b?.Width??0)==0;
    static bool Same(Characters a,Characters b)=>a.bold==b.bold&&a.italic==b.italic&&a.underline==b.underline&&a.family==b.family&&
        a.size==b.size&&a.language==b.language&&(a.highlight??[]).SequenceEqual(b.highlight??[])&&
        (a.foreground??[]).SequenceEqual(b.foreground??[]);
    static int[]? Rgb(string? color)=>color is not null&&uint.TryParse(color.TrimStart('#'),NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture,out uint rgb)&&rgb<=0xffffff?[(int)(rgb>>16),(int)(rgb>>8)&255,(int)rgb&255]:null;
}
