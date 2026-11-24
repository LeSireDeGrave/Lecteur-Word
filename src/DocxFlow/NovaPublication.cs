using System.Globalization;
using System.IO.Compression;
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
    sealed record Resource(string id,string path,string media_type,string sha256,bool document_image=true);

    internal static void Assess(StructuredPublication book,CancellationToken token)
    {
        try
        {
            _=new Builder(book,token).Build();
        }
        catch(InvalidDataException error)
        {
            book.Issue("EXPORT_NOVA_LIMIT",book.Model.Source(book.Model.Stories.First(s=>s.Kind=="body").RootId),
                error.Message);
        }
    }

    internal static void Write(string path,StructuredPublication book,ExportBudget budget,CancellationToken token) =>
        WriteEntries(path,new Builder(book,token).Build(),budget,null,token);

    internal static void Verify(string path,StructuredPublication book,CancellationToken token)
    {
        var expected=new Builder(book,token).Build();
        using var zip=ZipFile.OpenRead(path);
        if(zip.Entries.Count!=expected.Count)throw new InvalidDataException("EXPORT_NOVA_ENTRY_COUNT");
        foreach(var item in expected)
        {
            token.ThrowIfCancellationRequested();
            if(zip.GetEntry(item.Path) is not {} entry||entry.Length!=item.Data.Length)
                throw new InvalidDataException("EXPORT_NOVA_ENTRY");
            using var stream=entry.Open();
            byte[] bytes=new byte[item.Data.Length];
            stream.ReadExactly(bytes);
            if(!bytes.AsSpan().SequenceEqual(item.Data))
                throw new InvalidDataException("EXPORT_NOVA_ROUNDTRIP");
        }
    }

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
        readonly Dictionary<int,string> newPages=[];
        readonly Dictionary<string,SemanticStyle> styles;
        readonly HashSet<string> listSequences=new(StringComparer.Ordinal);
        readonly Dictionary<string,string> namedStyles=new(StringComparer.Ordinal);
        readonly HashSet<string> referencedBookmarks;
        string? part,chapter;
        int footnoteCount,endnoteCount;

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
            if(new[]{book.Metadata.Subject,book.Metadata.Keywords,book.Metadata.Description}.Any(s=>!string.IsNullOrWhiteSpace(s)))
                book.Issue("EXPORT_NOVA_METADATA_CONVERSION",root,
                    "Les propriétés sujet, mots-clés et description restent dans le DOCX. Le projet conserve le titre, l’auteur et les dates.",false);
            for(int i=0;i<Builtins.Length;i++)
                definitions.Add(new{id=Builtin(i+1),name=Builtins[i],characters=new Characters(),
                    paragraph=new{bullet=false,alignment="left",line_height="single",before=0,after=0,left=0,right=0,first=0}});

            var extensions=new Dictionary<string,object>();
            AddPageStyles(extensions);
            var body=BuildBody(model.BodyBlocks,true);
            foreach(var pending in openMarks.Values)
                book.Issue("EXPORT_NOVA_BOOKMARK_END",pending.Source,"Fin du signet NOVA introuvable.");
            var destinations=bookmarks.Select(b=>b.id).ToHashSet(StringComparer.Ordinal);
            if(unresolvedLinks.Any(l=>!destinations.Contains(l)))
                book.Issue("EXPORT_NOVA_LINK_DESTINATION",root,"Le projet NOVA ne peut résoudre une destination de lien.");
            if(links.Count!=0||bookmarks.Count!=0)
                extensions.Add("org.opusnova.references",new{version=1,documents=new Dictionary<string,object>
                    {{document,new{links,bookmarks}}}});
            if(body.styles.Count>100_000||body.paragraphs.Count>100_000||units.Count>100_000||
                forest.Count>2048||resources.Count>128||blocks.Count>384||notes.Count>1024||
                links.Count>4096||bookmarks.Count>4096||definitions.Count>128||
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
                metadata=new{title=book.Metadata.Title,author=book.Metadata.Author??"",created_utc=Epoch(book.Metadata.Created),
                    modified_utc=Epoch(book.Metadata.Modified??book.Metadata.Created)},
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
                    book.Issue("EXPORT_NOVA_SECTION_FORMAT",location,"La géométrie ou la numérotation propre à cette section dépasse le profil NOVA.");
                if(section.Semantic.Number<=1)continue;
                if(section.Start=="nextPage"&&section.Semantic.StartBlock<model.BodyBlocks.Length)
                    newPages.TryAdd(section.Semantic.StartBlock,model.BodyBlocks[section.Semantic.StartBlock]);
                else if(section.Start!="continuous")
                    book.Issue("EXPORT_NOVA_SECTION_TRANSITION",location,"Cette transition de section dépasse le profil NOVA.");
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
                book.Issue("EXPORT_NOVA_PAGE_STYLE",first.Source??root,"Dimensions ou marges NOVA invalides.");
                return;
            }
            string id=Id("page-style");
            var disabled=new{enabled=false,text="",edge_distance=0};
            var style=new{id,name="Page source",format=new{kind="custom",width,height},orientation=landscape?"landscape":"portrait",
                margins=new{top=Microns(g.Top),bottom=Microns(g.Bottom),inner=Microns(g.Left),outer=Microns(g.Right),gutter},
                facing_pages=first.MirrorMargins,typography=new{family="DejaVu Serif",size_tenths_pt=120,line_height_permille=1000},
                header=disabled,footer=disabled,notes=(object?)null};
            extensions.Add("org.opusnova.page_styles",new{version=1,active=id,styles=new[]{style}});
            book.Feature("nova-page-style",PublicationRetention.Native,(first.Source??root).Location,"Géométrie source et marges du projet.");
        }

        RichBody BuildBody(IEnumerable<string> ids,bool main)
        {
            var text=new StringBuilder();
            var spans=new List<Span>();
            var paragraphs=new List<Paragraph>();
            int offset=0,index=0;
            string[] sequence=ids.ToArray();
            foreach(string id in sequence)
            {
                token.ThrowIfCancellationRequested();
                if(index>0){text.Append('\n');offset++;}
                bool pageBefore=main&&newPages.ContainsKey(index);
                index++;
                var source=model.Source(id);
                int start=offset;
                if(main&&book.LayoutTable(id) is {} table)
                {
                    if(pageBefore)book.Issue("EXPORT_NOVA_SECTION_TABLE",source,"Vérification du saut de section sur un tableau indisponible.");
                    AddTable(table,offset);
                    Append(Marker);
                    continue;
                }
                if(book.Paragraph(id) is not {} paragraph)
                {
                    book.Issue("EXPORT_NOVA_STRUCTURE",source,"Bloc hors du profil NOVA à cet emplacement.");
                    continue;
                }
                var p=paragraph.Properties;
                var inlines=book.ReadInlines(id);
                var images=inlines.Where(i=>i.Image is not null).ToArray();
                if(images.Length>0)
                {
                    if(main&&images is [var image]&&inlines.Length==1&&image.Link is null)
                    {
                        AddImage(image,p,offset);
                        Append(Marker);
                        paragraphs.Add(new(start,Attributes(paragraph,null,pageBefore)));
                    }
                    else book.Issue("EXPORT_NOVA_IMAGE_ANCHOR",source,"Une image NOVA requiert un paragraphe isolé, sans autre contenu ni lien.");
                    continue;
                }

                var (style,kind)=StyleOf(paragraph);
                paragraphs.Add(new(start,Attributes(paragraph,style,pageBefore,
                    modelSide(index-2),modelSide(index))));
                foreach(var inline in inlines)
                {
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
                            !openMarks.TryAdd(declared,(Id("bookmark:"+mark),name,offset,inline.Source)))
                            book.Issue("EXPORT_NOVA_BOOKMARK",inline.Source,"Ce signet ne peut être conservé dans NOVA.");
                        continue;
                    }
                    if(inline.AnchorEnd is {} end)
                    {
                        if(main&&openMarks.Remove(end,out var markData))
                            bookmarks.Add(new(markData.Id,markData.Name,new(markData.Start,offset)));
                        continue;
                    }
                    if(inline.Note is {} note)
                    {
                        if(main)AddNote(inline,note,offset);
                        else book.Issue("EXPORT_NOVA_NESTED_NOTE",inline.Source,"Appel de note dans un contenu annexe hors du profil NOVA.");
                        Append(Marker);
                        continue;
                    }
                    if(inline.Value is not {} value)continue;
                    if(value.Contains('\0')||value.Contains('\ufffc')||value.Contains('\n')||
                        inline.Style is {Tracking:not 0} or {RightToLeft:true}||
                        inline.Style is {} size&&Math.Abs(size.Size*10-MathF.Round(size.Size*10))>.01f)
                        book.Issue("EXPORT_NOVA_INLINE",inline.Source,"Contrôle de texte, sens d’écriture ou attribut de caractère hors du profil NOVA.");
                    int begin=offset;
                    Append(value);
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
                        else book.Issue("EXPORT_NOVA_LINK_IN_AUXILIARY",inline.Source,"Lien dans une note ou une cellule hors du profil NOVA.");
                    }
                    else if(inline.Target is {} destination)
                    {
                        if(main)AddLink(inline,new(inline.Source.NodeId,"bookmark",null,null,destination,null,ReferenceStatus.Resolved),begin,offset);
                        else book.Issue("EXPORT_NOVA_LINK_IN_AUXILIARY",inline.Source,"Renvoi dans une note ou une cellule hors du profil NOVA.");
                    }
                }
                if(main&&kind is not null&&style is not null)
                    AddNode(kind,style,id,source,start,offset);

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

        object Attributes(LayoutParagraph paragraph,string? style,bool pageBefore,LayoutParagraph? previous=null,LayoutParagraph? next=null)
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
                book.Issue("EXPORT_NOVA_PARAGRAPH_STYLE",model.Source(paragraph.Id),"Bordure, trame, espacement ou retrait hors du profil NOVA.");
            int spacing=(int)MathF.Round(p.Line*1000/240);
            if(p.LineRule!="auto"||spacing is <1000 or >3000)
                book.Issue("EXPORT_NOVA_LINE_SPACING",model.Source(paragraph.Id),"Interligne absolu ou hors de la plage NOVA.");
            var tabs=p.Tabs.Select(t=>new
            {
                position_um=Microns(t.Position),
                alignment=t.Alignment=="numbering"?"left":t.Alignment,
                leader=t.Leader switch{"dot"=>"dots","hyphen"=>"dashes","underscore"=>"underline",_=>"none"},
                @decimal="."
            }).OrderBy(t=>t.position_um).ToArray();
            if(tabs.Length>32||tabs.Select(t=>t.position_um).Distinct().Count()!=tabs.Length||
                tabs.Any(t=>t.position_um is <1 or >2_000_000||t.alignment is not ("left" or "right" or "center" or "decimal"))||
                p.Tabs.Any(t=>t.Leader is not ("" or "none" or "dot" or "hyphen" or "underscore")))
                book.Issue("EXPORT_NOVA_TABS",model.Source(paragraph.Id),"Taquet ou caractère de conduite hors du profil NOVA.");
            if(tabs.Any(t=>t.alignment=="decimal"))
                book.Issue("EXPORT_NOVA_DECIMAL_TAB",model.Source(paragraph.Id),"Séparateur de tabulation décimale non établi pour le projet NOVA.");
            var (list,format)=ListOf(paragraph);
            return new
            {
                style,typography=new{keep_next=p.KeepNext,keep_together=p.KeepLines,page_before=pageBefore||p.PageBreakBefore,
                    widows=p.Widow?2:1,orphans=p.Widow?2:1},
                list,list_format=format,line_spacing=spacing is >=1000 and <=3000?spacing:(int?)null,tabs,
                bullet=false,alignment=p.Alignment is "both" or "justify"?"justified":p.Alignment,
                line_height="single",before,after,left,right,first
            };

            bool SameStyle(LayoutParagraph? neighbor)=>neighbor is not null&&neighbor.Section==paragraph.Section&&neighbor.StyleId==paragraph.StyleId;
        }

        (object? List,object? Format) ListOf(LayoutParagraph paragraph)
        {
            if(paragraph.List is not {} label||label.Numbering is not {} numbering||
                numbering.LevelId is not {} level)
            {
                if(paragraph.Properties.NumId is not null and not "0")
                    book.Issue("EXPORT_NOVA_LIST",model.Source(paragraph.Id),"Définition de liste NOVA introuvable.");
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
                book.Issue("EXPORT_NOVA_LIST",model.Source(paragraph.Id),"Motif, redémarrage ou géométrie de numérotation hors du profil NOVA.");
                return (null,null);
            }
            // NOVA places the list indent itself. Its projection is qualified only for this regular grid.
            if(Pixels(p.Left)!=(label.Level+1)*step)
                book.Issue("EXPORT_NOVA_LIST_INDENT",model.Source(paragraph.Id),"Retrait de liste hors de la grille NOVA.");
            var alignment=label.Alignment is "center" or "right"?label.Alignment:"left";
            return (new{sequence=Id("sequence:"+label.NumId),id=Id("list:"+paragraph.Id),level=label.Level,restart},
                new{kind,marker=format=="bullet"?pattern:"•",closing,alignment,step,gap});
        }

        void AddNode(string kind,string style,string id,SemanticSource source,int start,int end)
        {
            if(start==end||kind=="scene"&&chapter is null)
            {
                book.Issue("EXPORT_NOVA_MANUSCRIPT",source,"Titre vide ou scène sans chapitre explicite.");
                return;
            }
            string unit=Id("unit:"+id),node=Id("node:"+id);
            string? parent=kind=="chapter"?part:kind=="scene"?chapter:null;
            units.Add(new{id=unit,kind="paragraph",range=new Range(start,end)});
            forest.Add(new{id=node,kind,parent,document,unit,style});
            if(kind=="part"){part=node;chapter=null;}
            else if(kind=="chapter")chapter=node;
        }

        readonly HashSet<string> unresolvedLinks=new(StringComparer.Ordinal);

        void AddLink(StructuredPublication.Inline inline,SemanticReference link,int start,int end)
        {
            if(start==end)return;
            object target;
            if(link is {Status:ReferenceStatus.Resolved,TargetId:{} node})
            {
                string bookmark=Id("bookmark:"+book.Id(node));
                unresolvedLinks.Add(bookmark);
                target=new{kind="internal",project,target=bookmark};
            }
            else if(Publications.SafeUrl(link.Relation?.Target) is {} address&&
                !address.Any(c=>char.IsWhiteSpace(c)||c=='\\'))
                target=new{kind="external",address};
            else
            {
                book.Issue("EXPORT_NOVA_LINK",inline.Source,"Destination de lien hors du profil NOVA.");
                return;
            }
            if(links.Count>0&&links[^1].range.end==start&&Equals(links[^1].target,target))
                links[^1]=links[^1] with{range=links[^1].range with{end=end}};
            else links.Add(new(Id("link:"+link.SourceId+":"+start.ToString(CultureInfo.InvariantCulture)),new(start,end),target));
        }

        void AddNote(StructuredPublication.Inline inline,SemanticReference note,int offset)
        {
            if(note.TargetId is not {} target||!retainedNotes.Add(target))
            {
                book.Issue("EXPORT_NOVA_NOTE",inline.Source,"Appel de note partagé ou destination absente.");
                return;
            }
            var geometry=book.Snapshot.Sections.FirstOrDefault()?.Format??new();
            book.LoadStoryLayout(target,geometry);
            var body=BuildBody(model.BlocksOf(target).Select(e=>e.Id),false);
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
            book.Feature("nova-notes",PublicationRetention.Native,inline.Source.Location,"Appel et corps de note éditable.");
        }

        void AddImage(StructuredPublication.Inline inline,ParagraphLayout p,int offset)
        {
            var image=inline.Image!;
            string alternative=book.AlternativeText(inline.Source.NodeId)??"";
            var asset=book.AssetFor(image);
            int width=Microns(image.Width),height=Microns(image.Height);
            if(asset is not {Mime:"image/png" or "image/jpeg"}||
                width is <1000 or >2_000_000||height is <1000 or >2_000_000||
                (long)image.PixelWidth*image.PixelHeight>32_000_000||asset.Data.Length>4*1024*1024||
                Encoding.UTF8.GetByteCount(alternative)>4096||
                alternative.Contains('\0')||image.Decorative==true&&alternative.Length!=0||
                p.Alignment is not ("left" or "center" or "right"))
            {
                book.Issue("EXPORT_NOVA_IMAGE",inline.Source,"Ressource, dimensions ou accessibilité d’image hors du profil NOVA.");
                return;
            }
            string hash=Convert.ToHexStringLower(SHA256.HashData(asset.Data));
            if(!resources.TryGetValue(hash,out var resource))
            {
                string id=Id("resource:"+hash);
                resource=new(id,"resources/"+id+".bin",asset.Mime,hash);
                resources.Add(hash,resource);
                entries.Add(new(resource.path,asset.Data));
            }
            string? role=image.Decorative==true?"decorative":alternative.Length>0?"informative":null;
            blocks.Add(new{id=Id("image:"+inline.Source.NodeId),anchor=offset,image=new
                {resource=resource.id,width,height,alignment=p.Alignment,lock_aspect=false,alternative,role}});
            book.Feature("nova-images",PublicationRetention.Native,inline.Source.Location,"Image intégrée, bloc et description éditables.");
        }

        void AddTable(LayoutTable table,int offset)
        {
            int rowCount=table.Rows.Count,columnCount=table.Columns.Count;
            var source=model.Source(table.Id);
            if(rowCount is <1 or >1024||columnCount is <1 or >32||rowCount*columnCount>8192)
            {
                book.Issue("EXPORT_NOVA_TABLE_GRID",source,"Dimensions de grille hors du profil NOVA.");
                return;
            }
            int[] widths=table.Columns.Select(Microns).ToArray();
            int headers=table.Rows.TakeWhile(r=>r.Header).Count();
            if(widths.Any(w=>w is <4000 or >2_000_000)||widths.Sum(w=>(long)w)>2_000_000||
                table.Rows.Skip(headers).Any(r=>r.Header)||table.Rows.Any(r=>r.HeightRule=="exact")||
                table.Alignment!="left"||Math.Abs(table.Indent)>.01f)
                book.Issue("EXPORT_NOVA_TABLE_LAYOUT",source,"Géométrie, hauteur exacte ou alignement du tableau hors du profil NOVA.");
            if(table.Rows.Any(r=>!r.CantSplit))
                book.Issue("EXPORT_NOVA_TABLE_ROW_LAYOUT",source,"OpusNova conserve chaque ligne en un bloc lors de sa repagination.",false);
            var rows=table.Rows.Select(r=>new{id=Id(table.Id+":row:"+r.Index),minimum=Microns(r.Height),indivisible=true});
            var columns=widths.Select((w,i)=>new{id=Id(table.Id+":column:"+i),width=w});
            var outer=BorderOf([table.Borders.Top,table.Borders.Right,table.Borders.Bottom,table.Borders.Left],source);
            var inner=BorderOf([table.Borders.InsideH,table.Borders.InsideV],source);
            var cells=new List<object>();
            var covered=new bool[rowCount,columnCount];
            foreach(var cell in table.Cells.Where(c=>c.MergeOrigin is null))
            {
                token.ThrowIfCancellationRequested();
                if(cell.Row<0||cell.Column<0||cell.RowSpan<1||cell.ColumnSpan<1||
                    cell.RowSpan>rowCount-cell.Row||cell.ColumnSpan>columnCount-cell.Column)
                {
                    book.Issue("EXPORT_NOVA_TABLE_GRID",model.Source(cell.Id),"Cellule hors de la grille NOVA.");
                    continue;
                }
                for(int r=cell.Row;r<cell.Row+cell.RowSpan;r++)
                    for(int c=cell.Column;c<cell.Column+cell.ColumnSpan;c++)
                    {
                        if(covered[r,c])book.Issue("EXPORT_NOVA_TABLE_GRID",model.Source(cell.Id),"Chevauchement de cellules.");
                        covered[r,c]=true;
                    }
                int padding=Microns(cell.Margins.Left);
                if(padding<0||padding>20000||cell.Margins.Right!=cell.Margins.Left||
                    cell.Margins.Top!=0&&cell.Margins.Top!=cell.Margins.Left||
                    cell.Margins.Bottom!=0&&cell.Margins.Bottom!=cell.Margins.Left)
                    book.Issue("EXPORT_NOVA_TABLE_PADDING",model.Source(cell.Id),"Marge de cellule non représentable dans NOVA.");
                else if(padding>0&&(cell.Margins.Top==0||cell.Margins.Bottom==0))
                    book.Issue("EXPORT_NOVA_TABLE_PADDING_ADAPTED",model.Source(cell.Id),"Marge intérieure uniforme dans la cellule NOVA.",false);
                VerifyCellBorder(cell.Borders.Top,cell.Row==0?table.Borders.Top:table.Borders.InsideH);
                VerifyCellBorder(cell.Borders.Bottom,cell.Row+cell.RowSpan==rowCount?table.Borders.Bottom:table.Borders.InsideH);
                VerifyCellBorder(cell.Borders.Left,cell.Column==0?table.Borders.Left:table.Borders.InsideV);
                VerifyCellBorder(cell.Borders.Right,cell.Column+cell.ColumnSpan==columnCount?table.Borders.Right:table.Borders.InsideV);
                var body=BuildBody(model.BlocksOf(cell.Id).Select(e=>e.Id),false);
                if(Encoding.UTF8.GetByteCount(body.text)>256*1024||
                    widths.Skip(cell.Column).Take(cell.ColumnSpan).Sum(w=>(long)w)<=2L*padding+1000)
                    book.Issue("EXPORT_NOVA_TABLE_CELL_LIMIT",model.Source(cell.Id),"Budget de texte ou largeur disponible de cellule NOVA.");
                string vertical=cell.VerticalAlignment;
                if(vertical is not ("top" or "center" or "bottom"))
                    book.Issue("EXPORT_NOVA_TABLE_VERTICAL",model.Source(cell.Id),"Alignement vertical hors du profil NOVA.");
                cells.Add(new{id=Id("cell:"+cell.Id),row=cell.Row,column=cell.Column,row_span=cell.RowSpan,
                    column_span=cell.ColumnSpan,body,padding,background=Rgb(cell.Fill),vertical});

                void VerifyCellBorder(LayoutBorder? actual,LayoutBorder? inherited)
                {
                    if(actual is not null&&!SameBorder(actual,inherited))
                        book.Issue("EXPORT_NOVA_CELL_BORDER",model.Source(cell.Id),"Bordure propre à une cellule hors du profil NOVA.");
                }
            }
            if(covered.Cast<bool>().Any(c=>!c))
                book.Issue("EXPORT_NOVA_TABLE_GRID",source,"La grille NOVA exige une couverture complète.");
            var empty=new{text=""};
            var caption=new{id=Id("caption:"+table.Id),placement="above",title=empty,description=empty,source=empty,notes=empty};
            blocks.Add(new{id=Id("table:"+table.Id),anchor=offset,table=new{rows,columns,cells,header_rows=headers,appearance=new{outer,inner},caption}});
            book.Feature("nova-tables",PublicationRetention.Native,source.Location,"Tableau éditable, grille et fusions natives.");
        }

        object BorderOf(LayoutBorder?[] borders,SemanticSource source)
        {
            var edge=borders[0];
            if(borders.Any(b=>!SameBorder(b,edge))||edge is {Width:>0,Style:not "single"}||
                edge is not null&&Microns(edge.Width)>5000)
                book.Issue("EXPORT_NOVA_TABLE_BORDER",source,"Bordures de tableau non uniformes ou hors du profil NOVA.");
            return new{width=Microns(edge?.Width??0),color=Rgb(edge?.Color)??[0,0,0]};
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
    static long Epoch(DateTimeOffset? date)=>Math.Max(0,(date??DateTimeOffset.UnixEpoch).ToUnixTimeSeconds());
    static bool SameBorder(LayoutBorder? a,LayoutBorder? b)=>a==b||(a?.Width??0)==0&&(b?.Width??0)==0;
    static bool Same(Characters a,Characters b)=>a.bold==b.bold&&a.italic==b.italic&&a.underline==b.underline&&a.family==b.family&&
        a.size==b.size&&a.language==b.language&&(a.highlight??[]).SequenceEqual(b.highlight??[])&&
        (a.foreground??[]).SequenceEqual(b.foreground??[]);
    static int[]? Rgb(string? color)=>color is not null&&uint.TryParse(color.TrimStart('#'),NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture,out uint rgb)&&rgb<=0xffffff?[(int)(rgb>>16),(int)(rgb>>8)&255,(int)rgb&255]:null;
}
