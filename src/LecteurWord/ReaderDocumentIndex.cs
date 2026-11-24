using System.Globalization;
using System.Text;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

namespace LecteurWord;

internal enum DocumentCategory { Table, Cell, Image, Drawing, Chart, Equation, TextBox, Note, Link, Field, Running, Section, Review, Other }
internal enum DocumentTarget { None, Block, Container, Cell, Note, Running, Page }
internal sealed record DocumentCell(string TableId,int Row,int? Column,int? ColumnSpan,string? MergeOrigin);
internal sealed record MarkTextSource(string Text,SemanticSource Source,int Start);
internal sealed record DocumentElementRow(SemanticSource Source, DocumentCategory Category, string Type, string? Preview,
    string Position, string Coverage, DocumentTarget Target, string? Anchor, int? Page, int? Section, string Detail)
{
    internal DocumentCell? Cell { get; init; }
}

/// <summary>Projection for the viewer over an accepted inventory. The emitted rows keep semantic identities.</summary>
internal sealed class ReaderDocumentIndex
{
    const string W = SemanticIndex.W;
    const string A = SemanticIndex.Drawing;
    const string M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    const string C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    const string Cx = "http://schemas.microsoft.com/office/drawing/2014/chartex";
    const string Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    const string Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    const string V = SemanticIndex.Vml;
    internal IReadOnlyList<DocumentElementRow> Rows { get; init; } = [];
    internal SemanticDocument? Semantic { get; init; }
    internal IReadOnlyDictionary<string,int> BodyIndices { get; init; } = new Dictionary<string,int>();
    internal IReadOnlyDictionary<string,SemanticSource> Breaks { get; init; } = new Dictionary<string,SemanticSource>();
    internal IReadOnlyDictionary<string,string> BreakKinds { get; init; } = new Dictionary<string,string>();
    internal IReadOnlyDictionary<string,SemanticSource> Paragraphs { get; init; } = new Dictionary<string,SemanticSource>();
    internal IReadOnlyDictionary<string,SemanticSource> SectionClosers { get; init; } = new Dictionary<string,SemanticSource>();
    internal IReadOnlyDictionary<string,SemanticSource> Tabs { get; init; } = new Dictionary<string,SemanticSource>();
    internal IReadOnlyDictionary<string,List<MarkTextSource>> MarkTexts { get; init; } = new Dictionary<string,List<MarkTextSource>>();
    internal IReadOnlyDictionary<PageLine,SemanticSource> PageBreakSources { get; init; } = new Dictionary<PageLine,SemanticSource>();
    internal long Words { get; init; }
    internal long Characters { get; init; }
    internal string? Language { get; init; }
    internal string? Limitation { get; init; }

    internal static ReaderDocumentIndex Build(InspectionResult input, ReadableDocument reading, Composition pages,CancellationToken token,SemanticDocument? native=null)
    {
        var result=native is not null&&native.SnapshotId==input.Manifest.SourceSha256?new SemanticResult(native,[]):SemanticReader.Build(input,token);
        var body = reading.Presentation.BodyBlocks;
        var indices = new Dictionary<string,int>(StringComparer.Ordinal);
        var blocks = new Dictionary<string,ReadableBlock>(StringComparer.Ordinal);
        long words=0, chars=0;
        for(int i=0;i<body.Count;i++)
        {
            Count(body[i]);
            foreach(var b in ReadablePresentation.Walk([body[i]]))
            {
                token.ThrowIfCancellationRequested();
                indices.TryAdd(b.Id,i);
                blocks.TryAdd(b.Id,b);
            }
        }
        if(result.Document is not {} model)
            return new(){BodyIndices=indices,Words=words,Characters=chars,Limitation=result.Issues.FirstOrDefault()?.Code};
        var sectionClosers=model.Sections.Where(s=>s.ClosingParagraphId is not null&&s.PropertiesId is not null)
            .ToDictionary(s=>s.ClosingParagraphId!,s=>model.Source(s.PropertiesId!),StringComparer.Ordinal);
        var paragraphs=new Dictionary<string,SemanticSource>(StringComparer.Ordinal);
        var tabs=new Dictionary<string,SemanticSource>(StringComparer.Ordinal);
        var markTexts=new Dictionary<string,List<MarkTextSource>>(StringComparer.Ordinal);
        var breaksByParagraph=new Dictionary<string,List<(string Kind,SemanticSource Source)>>(StringComparer.Ordinal);
        var breaks=new Dictionary<string,SemanticSource>(StringComparer.Ordinal);
        var kinds=new Dictionary<string,string>(StringComparer.Ordinal);
        var rows=new List<DocumentElementRow>();
        var notes=reading.Presentation.Notes.Values.Where(n=>n.Verified).ToDictionary(n=>n.ReferenceId,StringComparer.Ordinal);
        var references=model.References.ToLookup(r=>r.SourceId);
        var fields=model.Fields.ToDictionary(f=>f.Id,StringComparer.Ordinal);
        var tables=model.Tables.ToDictionary(t=>t.Id,StringComparer.Ordinal);
        var cells=new Dictionary<string,DocumentCell>(StringComparer.Ordinal);
        foreach(var table in model.Tables)
            for(int row=0;row<table.Rows.Length;row++)
                foreach(var cell in table.Rows[row].Cells)
                    cells.TryAdd(cell.Id,new(table.Id,row+1,cell.Column is {} column?column+1:null,
                        cell.ColumnSpan,cell.VerticalOriginId??cell.HorizontalOriginId));
        var readableCells=new Dictionary<string,(string TableId,int RowIndex)>(StringComparer.Ordinal);
        foreach(var table in body.Where(b=>b.Kind=="table"))
            for(int row=0;row<table.Children.Count;row++)
                foreach(var cell in table.Children[row].Children.Where(b=>b.Kind=="cell"))
                    readableCells.TryAdd(cell.Id,(table.Id,row));
        var bookmarks=reading.Bookmarks.GroupBy(b=>b.Source).ToDictionary(g=>g.Key,g=>g.First());
        var sections=model.Sections.Where(s=>s.PropertiesId is not null).ToDictionary(s=>s.PropertiesId!,StringComparer.Ordinal);
        var bodySections=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var s in model.Sections)
            for(int i=s.StartBlock;i<s.EndBlockExclusive;i++)bodySections[model.BodyBlocks[i]]=s.Number;
        var stories=model.Stories.ToDictionary(s=>s.RootId,StringComparer.Ordinal);
        var renderedStories=reading.Presentation.Sections.SelectMany(s=>s.Stories.Where(st=>st.Verified).Select(st=>(st.Source,s.Number)))
            .ToLookup(x=>x.Source,x=>x.Number);
        var positions=new Dictionary<string,int>(StringComparer.Ordinal);
        var placedImages=new Dictionary<string,int>(StringComparer.Ordinal);
        var sectionPages=new Dictionary<int,int>();
        foreach(var pg in pages.Available ? pages.Pages : [])
        {
            token.ThrowIfCancellationRequested();
            foreach(var placement in pg.RunningStories)
                if(placement.Selection.Reference.TargetId is {} root)positions.TryAdd(root,pg.Number);
            foreach(var cell in pg.DisplayCells.Where(c=>!c.IsRepeated))
            {
                positions.TryAdd(cell.TableId,pg.Number);
                positions.TryAdd(cell.CellId,pg.Number);
                foreach(string merged in cell.MergedSourceIds)positions.TryAdd(merged,pg.Number);
            }
            foreach(var line in pg.DisplayLines.Where(l=>!l.IsRepeated))
            {
                positions.TryAdd(line.ParagraphId,pg.Number);
                sectionPages.TryAdd(line.Section,pg.Number);
                foreach(var image in line.Images.Where(im=>!im.IsRepeated))placedImages.TryAdd(image.RunId,pg.Number);
            }
        }
        foreach(var e in model.Elements)
        {
            token.ThrowIfCancellationRequested();
            if(e.Coverage==SemanticCoverage.InactiveAlternative)continue;
            string name=model.Name(e.Id);
            if(e.Kind==SemanticKind.Paragraph)paragraphs.Add(e.Id,model.Source(e.Id));
            if(e.Kind==SemanticKind.Tab)tabs.Add(e.Id,model.Source(e.Id));
            if(e.Kind is SemanticKind.Text or SemanticKind.DeletedText && model.RawText(e.Id) is {} text &&
                model.ParentId(e.Id) is {} owner && model.Name(owner) is "{" + W + "}t" or "{" + W + "}delText")
            {
                if(!markTexts.TryGetValue(owner,out var sources))markTexts.Add(owner,sources=[]);
                int start=sources.Count==0?0:sources[^1].Start+sources[^1].Text.Length;
                sources.Add(new(text,model.TextSource(e.Id),start));
            }
            if(e.Kind==SemanticKind.Break)
            {
                var origin=model.Source(e.Id);
                string kindOfBreak=model.Attribute(e.Id,"type")??"line";
                breaks.Add(e.Id,origin);
                kinds.Add(e.Id,kindOfBreak);
                for(string? parent=model.ParentId(e.Id);parent is not null&&model.Element(parent).StoryId==e.StoryId;parent=model.ParentId(parent))
                {
                    if(model.Element(parent).Kind!=SemanticKind.Paragraph)continue;
                    if(!breaksByParagraph.TryGetValue(parent,out var sequence))breaksByParagraph.Add(parent,sequence=[]);
                    sequence.Add((kindOfBreak,origin));break;
                }
            }
            var category=Category(e,name);
            if(category is not {} kind)continue;
            var source=model.Source(e.Id);
            var story=e.StoryId is {} sid?stories.GetValueOrDefault(sid):null;
            string position=story is {Kind:"body"}?"body":story?.Kind??"package";
            string type=name[(name.IndexOf('}')+1)..];
            string? preview=null, anchor=null;
            var target=DocumentTarget.None;
            int? page=positions.TryGetValue(e.Id,out int p)?p:null;
            int? section=story?.Kind=="body"?BodySection(e.Id):null;
            string detail=name;
            if(kind==DocumentCategory.Table && tables.TryGetValue(e.Id,out var table))
            {
                detail += $" · {table.Rows.Length} × {table.GridColumns.Length}";
                var tableProperties=model.ChildId(e.Id,"tblPr");
                if(tableProperties is not null && model.ChildId(tableProperties,"tblCaption") is {} caption)
                    preview=Short(model.Attribute(caption,"val")??"");
                if(preview is null && blocks.GetValueOrDefault(e.Id) is {} readingTable)
                {
                    var firstRow=readingTable.Children.FirstOrDefault();
                    preview=Short(string.Join(" · ",(firstRow?.Children??[]).Take(3)
                        .Select(reading.Presentation.TextFor).Where(value=>!string.IsNullOrWhiteSpace(value))));
                }
                preview??=$"{table.Rows.Length} × {table.GridColumns.Length}";
            }
            if(fields.TryGetValue(e.Id,out var field))
            {
                preview=Short(field.Command);
                if(!model.TocMarkerFields.Contains(field.Id)&&
                    field.StoredResultIds.All(id=>model.ParentId(id) is {} owner&&
                        !reading.Presentation.HiddenNodes.Contains(owner)&&!reading.Presentation.FieldDetailNodes.Contains(owner))&&
                    model.StoredText(field) is {Length:>0} value)preview=Short(field.Command+" · "+value);
                detail+=field.StructureVerified?" · structure vérifiée":" · structure incertaine";
            }
            if(notes.TryGetValue(e.Id,out var note))
            {
                target=DocumentTarget.Note;anchor=e.Id;preview=Short(note.Number+" · "+string.Join(" ",note.Content.Take(1).Select(reading.Presentation.TextFor)));
            }
            else if(e.Kind==SemanticKind.Story && story?.Kind is "hdr" or "ftr" && renderedStories[source.Location].FirstOrDefault() is >0 and var number)
            {
                target=DocumentTarget.Running;section=number;
            }
            if(sections.TryGetValue(e.Id,out var s))
            {
                section=s.Number;
                if(sectionPages.TryGetValue(s.Number,out int placed))page=placed;
                if(s.ClosingParagraphId is {} closing && indices.ContainsKey(closing))
                {
                    anchor=closing;
                    target=DocumentTarget.Container;
                }
            }
            if(e.Kind==SemanticKind.BookmarkStart && bookmarks.TryGetValue(source.Location,out var bookmark) && indices.ContainsKey(bookmark.BlockId))
            {
                preview=Short(bookmark.Label);target=DocumentTarget.Container;anchor=bookmark.BlockId;
            }
            var related=references[e.Id].FirstOrDefault();
            if(related is not null)
            {
                detail+=" · "+related.Kind+" · "+related.Status;
                preview??=Short(related.DeclaredId??"");
            }
            preview??=Short(model.Attribute(e.Id,"name")??model.Attribute(e.Id,"descr","")??"");
            if(blocks.GetValueOrDefault(e.Id) is {} b)
            {
                preview??=Short(b.Label??(b.Kind is "link" or "image"?reading.Presentation.TextFor(b):""));
                if(target==DocumentTarget.None && IsVisibleBlock(b))
                {
                    anchor=b.Id;
                    target=body[indices[b.Id]].Id==b.Id?DocumentTarget.Block:DocumentTarget.Container;
                }
            }
            var cellInfo=cells.GetValueOrDefault(e.Id);
            if(kind==DocumentCategory.Cell)
            {
                if(blocks.GetValueOrDefault(e.Id) is {} readingCell)
                    preview=Short(reading.Presentation.TextFor(readingCell));
                if(readableCells.ContainsKey(e.Id))
                {
                    target=DocumentTarget.Cell;anchor=e.Id;
                }
            }
            if(kind==DocumentCategory.Image && target==DocumentTarget.None && DrawingOwner(e.Id) is {} drawing)
            {
                if(placedImages.TryGetValue(drawing,out int imagePage)){page=imagePage;anchor=drawing;}
                if(indices.ContainsKey(drawing) && blocks.GetValueOrDefault(drawing)?.Kind=="image")
                {
                    anchor=drawing;target=DocumentTarget.Container;
                }
            }
            if(page is not null)target=DocumentTarget.Page;
            if(target==DocumentTarget.Container && anchor is {} a && indices.TryGetValue(a,out int bodyIndex))
                anchor=body[bodyIndex].Id;
            rows.Add(new(source,kind,type,preview,position,e.Coverage.ToString(),target,anchor,page,section,detail){Cell=cellInfo});
        }
        foreach(var s in model.Sections.Where(s=>s.PropertiesId is null))
        {
            string? first=model.BodyBlocks.ElementAtOrDefault(s.StartBlock);
            string bodyId=model.Stories.First(st=>st.Kind=="body").RootId;
            int? page=sectionPages.TryGetValue(s.Number,out int placed)?placed:null;
            string? anchor=first is not null&&indices.ContainsKey(first)?first:null;
            rows.Add(new(model.Source(first??bodyId),DocumentCategory.Section,"section",null,"body","Represented",
                page is not null?DocumentTarget.Page:anchor is not null?DocumentTarget.Container:DocumentTarget.None,
                anchor,page,s.Number,"Section finale implicite / Implicit final section"));
        }

        var pageBreakSources=new Dictionary<PageLine,SemanticSource>(ReferenceEqualityComparer.Instance);
        if(pages.Available)
        {
            foreach(var line in pages.Pages.SelectMany(p=>p.Lines))
                if(line.Native?.Parts.LastOrDefault()?.Control is {Kind:"Break"} control)
                    pageBreakSources[line]=control.Source;
            foreach(var group in pages.Pages.SelectMany(p=>p.Lines).Where(l=>l.Native is null&&!l.IsRepeated&&(l.HasExplicitPageBreak||l.SourceBreak.Length>0))
                .GroupBy(l=>l.ParagraphId))
            {
                if(!breaksByParagraph.TryGetValue(group.Key,out var sequence))continue;
                var lines=group.ToArray();
                if(lines.Length!=sequence.Count||lines.Where((l,i)=>l.HasExplicitPageBreak?sequence[i].Kind!="page":
                    sequence[i].Kind is not ("line" or "textWrapping" or "")).Any())continue;
                for(int i=0;i<lines.Length;i++)pageBreakSources.Add(lines[i],sequence[i].Source);
            }
        }
        return new(){Semantic=model,Rows=rows,BodyIndices=indices,ReadableCells=readableCells,Breaks=breaks,BreakKinds=kinds,Paragraphs=paragraphs,
            Tabs=tabs,MarkTexts=markTexts,PageBreakSources=pageBreakSources,
            SectionClosers=sectionClosers,Words=words,Characters=chars,Language=LanguageOf(input,model)};

        void Count(ReadableBlock b)
        {
            token.ThrowIfCancellationRequested();
            if(b.Kind=="note")return;
            if(b.Kind is not ("heading" or "paragraph"))
            {
                foreach(var child in b.Children)Count(child);
                return;
            }
            bool inWord=false;
            foreach(var part in CountableText(b))
            foreach(var rune in part.EnumerateRunes())
            {
                chars++;
                var uc=Rune.GetUnicodeCategory(rune);
                bool isWord=Rune.IsLetterOrDigit(rune) || inWord && uc is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
                if(isWord&&!inWord)words++;
                inWord=isWord;
            }
        }

        IEnumerable<string> CountableText(ReadableBlock b)
        {
            if (b.Kind is "note" or "image" or "section" || reading.Presentation.FieldDetailNodes.Contains(b.Id) ||
                b.Kind=="field"&&!reading.Presentation.Fields.ContainsKey(b.Id))yield break;
            if(b.Kind=="break"){yield return "\n";yield break;}
            if(b.Children.Count==0)yield return b.Text;
            foreach(var child in b.Children)
                foreach(string text in CountableText(child))yield return text;
        }

        DocumentCategory? Category(SemanticElement e,string name)
        {
            if(e.StoryId is null)return null;
            if(fields.ContainsKey(e.Id))return DocumentCategory.Field;
            return e.Kind switch
            {
                SemanticKind.Table => DocumentCategory.Table,
                SemanticKind.Cell => DocumentCategory.Cell,
                SemanticKind.Section when sections.ContainsKey(e.Id) => DocumentCategory.Section,
                SemanticKind.NoteCall => DocumentCategory.Note,
                SemanticKind.BookmarkStart or SemanticKind.Link => DocumentCategory.Link,
                SemanticKind.CommentReference when name==Q(W,"commentReference") => DocumentCategory.Review,
                SemanticKind.Revision => DocumentCategory.Review,
                SemanticKind.Story => name switch
                {
                    "{" + W + "}hdr" or "{" + W + "}ftr" => DocumentCategory.Running,
                    "{" + W + "}txbxContent" => DocumentCategory.TextBox,
                    "{" + W + "}comment" => DocumentCategory.Review,
                    "{" + W + "}footnote" or "{" + W + "}endnote" when model.Attribute(e.Id,"type") is null or "normal" => DocumentCategory.Note,
                    _ => null,
                },
                SemanticKind.ResourceReference when name is "{" + A + "}blip" or "{" + V + "}imagedata" => DocumentCategory.Image,
                SemanticKind.Drawing when !HasSpecificDrawing(e.Id) => DocumentCategory.Drawing,
                SemanticKind.ContentControl when name==Q(W,"sdt") => DocumentCategory.Other,
                SemanticKind.Opaque => name switch
                {
                    "{" + C + "}chart" or "{" + Cx + "}chart" or "{" + Dgm + "}relIds" => DocumentCategory.Chart,
                    "{" + M + "}oMathPara" => DocumentCategory.Equation,
                    "{" + M + "}oMath" when model.ParentId(e.Id) is not {} parent || model.Name(parent)!=Q(M,"oMathPara") => DocumentCategory.Equation,
                    "{" + Wps + "}wsp" or "{" + V + "}shape" when !HasSpecificDrawing(e.Id) => DocumentCategory.Drawing,
                    "{" + W + "}object" or "{" + W + "}altChunk" or "{" + W + "}subDoc" => DocumentCategory.Other,
                    _ when model.ParentId(e.Id) is {} owner && model.Element(owner).Kind is
                        SemanticKind.Paragraph or SemanticKind.Run or SemanticKind.Story or SemanticKind.Cell
                        && !name.StartsWith('#') && !name.StartsWith("{"+M+"}",StringComparison.Ordinal) => DocumentCategory.Other,
                    _ => null,
                },
                _ => null
            };
        }

        bool HasSpecificDrawing(string id)
        {
            var pending=new Stack<string>();
            pending.Push(id);
            while(pending.TryPop(out string? node))
            {
                token.ThrowIfCancellationRequested();
                foreach(var child in model.Children(node))
                {
                    string name=model.Name(child.Id);
                    if(name is "{" + A + "}blip" or "{" + V + "}imagedata" or "{" + C + "}chart" or "{" + Cx + "}chart" or
                        "{" + Dgm + "}relIds" or "{" + W + "}txbxContent" or "{" + Wps + "}wsp" or "{" + V + "}shape")
                        return true;
                    pending.Push(child.Id);
                }
            }
            return false;
        }

        int? BodySection(string id)
        {
            for(string? parent=id;parent is not null;parent=model.ParentId(parent))
                if(bodySections.TryGetValue(parent,out int section))return section;
            return null;
        }

        string? DrawingOwner(string id)
        {
            for(string? parent=model.ParentId(id);parent is not null;parent=model.ParentId(parent))
            {
                var kind=model.Element(parent).Kind;
                if(kind==SemanticKind.Drawing)return parent;
                if(kind==SemanticKind.Paragraph)break;
            }
            return null;
        }
    }
    internal IReadOnlyDictionary<string,(string TableId,int RowIndex)> ReadableCells { get; init; } =
        new Dictionary<string,(string,int)>();

    internal SemanticSource? SpaceSource(string owner,int offset,char character)
    {
        if(!MarkTexts.TryGetValue(owner,out var sources))return null;
        foreach(var item in sources)
        {
            int local=offset-item.Start;
            if(local>=0&&local<item.Text.Length&&item.Text[local]==character)
                return item.Source with {Span=new Utf16Span(local,local+1)};
        }
        return null;
    }
    static bool IsVisibleBlock(ReadableBlock b) => !b.Hidden&&b.Kind is "table" or "cell" or "image" or "link" or "field" or "placeholder" or "revision" or "section";
    static string Q(string ns,string local)=>SemanticIndex.QName(ns,local);
    static string? Short(string text)
    {
        text=text.Trim();
        if(text.Length==0)return null;
        if(text.Length<=110)return text;
        int end=char.IsHighSurrogate(text[109])?109:110;
        return text[..end]+"…";
    }

    static string? LanguageOf(InspectionResult input, SemanticDocument model)
    {
        const string Dublin="{http://purl.org/dc/elements/1.1/}language";
        var nodes=input.Manifest.Nodes;
        string? core=input.Manifest.Relationships.Where(r=>r.SourcePart=="/" &&
            r.Type=="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" && r.Mode=="Internal")
            .Select(r=>r.ResolvedPart).Take(2).ToArray() is [var destination]?destination:null;
        string? v=core is null?null:nodes.Where(n=>n.Source.PartUri==core && n.Name==Dublin)
            .Select(n=>string.Concat(model.Children(n.Id).Select(c=>model.RawText(c.Id)))).Distinct().Take(2).ToArray() is [var given]?given:null;
        if(string.IsNullOrWhiteSpace(v))
        {
            string? part=model.RelatedPart("styles");
            if(part is not null)
                v=nodes.Where(n=>n.Source.PartUri==part && n.Name==Q(W,"rPrDefault"))
                    .Select(n=>model.ChildId(n.Id,"rPr")).Where(id=>id is not null).Select(id=>model.ChildId(id!,"lang"))
                    .Where(id=>id is not null).Select(id=>model.Attribute(id!,"val")).Distinct().Take(2).ToArray() is [var lang]?lang:null;
        }
        return v is {Length:>0 and <=48} && v.Split('-').All(p=>p.Length>0 && p.All(char.IsAsciiLetterOrDigit)) ? v : null;
    }
}
