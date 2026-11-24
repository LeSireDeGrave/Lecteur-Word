using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace LecteurWord.Flow;

internal sealed record PageFieldSyntax(string? Bookmark,bool Hyperlink,bool Relative,string? Format,int DecimalPlaces)
{
    internal static PageFieldSyntax? Read(SemanticDocument model,SemanticField field)
    {
        if(!ValidFlags(model,field))return null;
        if(Instruction(model,field) is not {} instruction)return null;
        string[] words=instruction.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(words.Length==0||!words[0].Equals(field.Command,StringComparison.OrdinalIgnoreCase))return null;
        int i=1,places=0;
        string? name=null,format=null;
        bool hyperlink=false,relative=false;
        if(field.Command is "PAGEREF" or "REF")
        {
            if(i==words.Length)return null;
            name=words[i++];
            if(name.Length>=2&&name[0]=='"'&&name[^1]=='"')name=name[1..^1];
            if(name.Length==0||name.Contains('"')||name.Contains('\\'))return null;
        }
        for(;i<words.Length;i++)
        {
            switch(words[i].ToLowerInvariant())
            {
                case "\\h" when name is not null&&!hyperlink:
                    hyperlink=true;
                    break;
                case "\\p" when field.Command=="PAGEREF"&&!relative:
                    relative=true;
                    break;
                case "\\*" when field.Command!="REF"&&format is null&&places==0&&i+1<words.Length:
                    format=words[++i] switch
                    {
                        "Arabic" or "ARABIC" or "arabic"=>"decimal",
                        "Roman" or "ROMAN"=>"upperRoman",
                        "roman"=>"lowerRoman",
                        "Alphabetic" or "ALPHABETIC"=>"upperLetter",
                        "alphabetic"=>"lowerLetter",
                        _=>null
                    };
                    if(format is null)return null;
                    break;
                case "\\#" when field.Command!="REF"&&format is null&&places==0&&i+1<words.Length:
                    string picture=words[++i];
                    if(picture.Length>=2&&picture[0]=='"'&&picture[^1]=='"')picture=picture[1..^1];
                    if(picture.Length is <1 or >16||picture.Any(c=>c!='0'))return null;
                    places=picture.Length;
                    break;
                default:
                    return null;
            }
        }
        return new(name,hyperlink,relative,format,places);

    }

    internal static bool ValidFlags(SemanticDocument model,SemanticField field)=>
        ValidFlag(model.Attribute(field.Id,"dirty"))&&ValidFlag(model.Attribute(field.Id,"fldLock"));

    static bool ValidFlag(string? value)=>value is null or "0" or "1" or "true" or "false" or "on" or "off";

    internal static string? Instruction(SemanticDocument model,SemanticField field)
    {
        const int limit=1024;
        if(field.InstructionAttribute is {} simple)return simple.Length<=limit?simple:null;
        var instruction=new StringBuilder();
        foreach(string id in field.InstructionIds)
        {
            string? value=model.RawText(id);
            if(value is null||value.Length>limit-instruction.Length)return null;
            instruction.Append(value);
        }
        return instruction.ToString();
    }
}

internal sealed record BodyPageField(SemanticField Field,PageFieldSyntax Syntax,string ParagraphId,TextStyle Style,
    string? StoredValue,Utf16Span? StoredRange)
{
    internal GeneratedTocRow? TocRow {get;init;}
    internal string? PropertyValue {get;init;}
    internal string ResultId=>TocRow?.Id??Field.Id;
}
internal sealed record PageBookmark(string Id,string ParagraphId,int Offset,int ParagraphLength)
{
    internal BookmarkText? Text {get;init;}
}
internal sealed record BookmarkText(string Value,TextStyle Style);
internal readonly record struct PlacedPageBookmark(int Page,string ParagraphId,float Top,float Left);
internal sealed record PageFieldValue(string FieldId,string? BookmarkId,int? PhysicalPage,string? Text,string? Diagnostic);
internal sealed record PageFieldPass(int Number,int Pages,int Lines,string? FirstObstacle,SemanticSource? ObstacleSource,
    ImmutableArray<PageFieldValue> Fields);

internal static partial class PageComposer
{
    internal const int MaxFieldPasses=4;
    const int MaxBodyPageFields=128;

    static Dictionary<string,BodyPageField> ReadBodyFields(SemanticDocument model,TypographyDocument measured,
        TypographySession typography,Action check)
    {
        var candidates=new Dictionary<string,BodyPageField>(StringComparer.Ordinal);
        var ambiguous=new HashSet<string>(StringComparer.Ordinal);
        if(!model.Fields.Any(f=>f.Command is "PAGEREF" or "REF" or "PAGE" or "NUMPAGES" or "SECTIONPAGES" or "DOCPROPERTY"))return candidates;
        var byField=model.Fields.ToDictionary(f=>f.Id,StringComparer.Ordinal);
        var nested=model.Fields.Where(f=>f.ParentFieldId is not null).Select(f=>f.ParentFieldId!).ToHashSet(StringComparer.Ordinal);
        var text=measured.Paragraphs.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        string? body=model.Stories.FirstOrDefault(s=>s.Kind=="body")?.RootId;
        var properties=model.Fields.Any(f=>f.StoryId==body&&f.Command=="DOCPROPERTY")?model.TextDocumentProperties(check):null;
        foreach(var field in model.Fields)
        {
            check();
            if(field.StoryId!=body||field.Command is not ("PAGEREF" or "REF" or "PAGE" or "NUMPAGES" or "SECTIONPAGES" or "DOCPROPERTY")||
                !field.StructureVerified||field.Locked||nested.Contains(field.Id))continue;
            bool property=field.Command=="DOCPROPERTY";
            string? propertyValue=property&&ReadPropertyOperand(model,field) is {} operand?properties?.GetValueOrDefault(operand):null;
            var instruction=property?propertyValue is null?null:new PageFieldSyntax(null,false,false,null,0):PageFieldSyntax.Read(model,field);
            if(instruction is not {} syntax||
                property&&field.ParentFieldId is not null||
                field.ParentFieldId is {} parent&&(!byField.TryGetValue(parent,out var outer)||!outer.StructureVerified||
                    outer.Locked||outer.ParentFieldId is not null||outer.Command!="TOC"||outer.SeparatorId is not {} outerSeparator||
                    model.Element(outerSeparator).Order>=model.Element(field.Id).Order))continue;
            if(Ancestor(model,field.Id,SemanticKind.Paragraph) is not {} paragraph||
                model.ParentId(paragraph)!=body||!text.TryGetValue(paragraph,out var p)||
                p.RightToLeft||p.Fragments.Any(f=>f.BidiLevel%2!=0))continue;
            if(field.EndId is not {} endId||Ancestor(model,endId,SemanticKind.Paragraph)!=paragraph)continue;
            int fieldStart=model.Element(field.Id).Order;
            int end=model.Element(endId).Order;
            string? original=null;
            Utf16Span? storedRange=null;
            var storedIds=field.StoredResultIds.ToHashSet(StringComparer.Ordinal);
            var slices=p.Fragments.SelectMany(f=>f.Sources.Select(s=>(Slice:s,Start:f.ContextOffset+s.FragmentRange.Start,
                End:f.ContextOffset+s.FragmentRange.EndExclusive))).ToArray();
            if(storedIds.Count>0)
            {
                if(ReadTerminalFieldCache(model,field,p,check,property) is not {} cache)continue;
                original=cache.Value;
                storedRange=cache.Range;
            }
            int contentEnd=storedRange?.Start??p.ContextLength;
            if(slices.Any(s=>!storedIds.Contains(s.Slice.Source.NodeId)&&(s.End>contentEnd||
                    model.Element(s.Slice.Source.NodeId).Order>=fieldStart))||
                p.Controls.Any(c=>model.Element(c.Source.NodeId).Order>=fieldStart)||
                contentEnd>0&&p.Context[contentEnd-1] is not (' ' or '\t' or '\n' or '\ufffc'))continue;
            if(contentEnd>0&&p.Context[contentEnd-1]=='\ufffc'&&
                !p.Controls.Any(c=>c.Offset==contentEnd-1&&c.Kind=="Tab"))continue;

            TextStyle style=p.MarkStyle;
            string? run=null;
            if(field.InstructionAttribute is not null)
            {
                run=model.Children(field.Id).FirstOrDefault(e=>e.Kind==SemanticKind.Run).Id;
            }
            else if(field.SeparatorId is {} separator)
            {
                run=Ancestor(model,separator,SemanticKind.Run);
                for(int order=model.Element(separator).Order+1;order<end;order++)
                {
                    check();
                    var e=model.Elements[order];
                    if(e.Kind!=SemanticKind.Run||e.Id==model.ParentId(endId))continue;
                    run=e.Id;
                    break;
                }
            }
            if(storedRange is not null)style=slices.First(s=>storedIds.Contains(s.Slice.Source.NodeId)).Slice.Style;
            else if(run is not null)style=typography.PageFieldStyle(run);
            if(style.SuppressText||style.RightToLeft||style.Complex||style.Underline!="none")continue;
            if(syntax.Relative&&!EnglishRelative(style.Language))continue;
            if(!candidates.TryAdd(paragraph,new(field,syntax,paragraph,style,original,storedRange){PropertyValue=propertyValue}))
                ambiguous.Add(paragraph);
            if(candidates.Count>MaxBodyPageFields)throw new InvalidDataException("PAGE_BODY_FIELD_LIMIT");
        }
        foreach(string id in ambiguous)candidates.Remove(id);
        return candidates;
    }

    static (string Value,Utf16Span Range,TextStyle Style)? ReadTerminalFieldCache(
        SemanticDocument model,SemanticField field,TextParagraph paragraph,Action check,bool property=false)
        =>ReadFieldTextCache(model,field,paragraph,check,terminal:true,property);

    static (string Value,Utf16Span Range,TextStyle Style)? ReadFieldTextCache(
        SemanticDocument model,SemanticField field,TextParagraph paragraph,Action check,bool terminal,bool property=false)
    {
        string? value=model.StoredText(field);
        if(value is null||value.Length==0||(property?value.Length>1024||value.Any(char.IsControl):
            value.Length>16||!value.All(char.IsAsciiLetterOrDigit)))return null;
        var ids=field.StoredResultIds.ToHashSet(StringComparer.Ordinal);
        var slices=paragraph.Fragments.SelectMany(f=>f.Sources.Where(s=>ids.Contains(s.Source.NodeId))
            .Select(s=>(Slice:s,Start:f.ContextOffset+s.FragmentRange.Start,End:f.ContextOffset+s.FragmentRange.EndExclusive)))
            .OrderBy(s=>s.Start).ToArray();
        if(slices.Length==0)return null;
        int offset=slices[0].Start,end=slices[^1].End;
        if(terminal&&end!=paragraph.ContextLength||end-offset!=value.Length||
            !paragraph.Context.AsSpan(offset,value.Length).SequenceEqual(value))return null;
        var style=slices[0].Slice.Style;
        int cursor=offset;
        foreach(var entry in slices)
        {
            check();
            if(entry.Start!=cursor||entry.Slice.Style!=style)return null;
            cursor=entry.End;
        }
        if(!slices.Select(s=>s.Slice.Source.NodeId).ToHashSet(StringComparer.Ordinal).SetEquals(ids)||
            !SafeBoundary(offset)||!SafeBoundary(end))
            return null;
        return (value,new(offset,cursor),style);

        bool SafeBoundary(int boundary)=>!paragraph.Fragments.Any(f=>f.ContextOffset<boundary&&
            f.ContextOffset+f.Text.Length>boundary&&
            (f.Measure is null||!f.Measure.Clusters.Any(c=>f.ContextOffset+c.Range.Start==boundary&&!c.UnsafeBefore)));
    }

    static bool EnglishRelative(string language)=>language.Equals("en",StringComparison.OrdinalIgnoreCase)||
        language.Equals("en-US",StringComparison.OrdinalIgnoreCase)||language.Equals("en-GB",StringComparison.OrdinalIgnoreCase);

    static string? Ancestor(SemanticDocument model,string id,SemanticKind kind)
    {
        for(string? at=id;at is not null;at=model.ParentId(at))
            if(model.Element(at).Kind==kind)return at;
        return null;
    }

    static Dictionary<string,PageBookmark> IndexPageBookmarks(SemanticDocument model,
        IReadOnlyDictionary<string,TextParagraph> text,IEnumerable<BodyPageField> bodyFields,Action check)
    {
        var result=new Dictionary<string,PageBookmark>(StringComparer.Ordinal);
        var running=model.Stories.Where(s=>s.Kind is "hdr" or "ftr").Select(s=>s.RootId).ToHashSet(StringComparer.Ordinal);
        var fields=bodyFields.Select(f=>f.Field).Concat(model.Fields.Where(f=>running.Contains(f.StoryId)&&
                f is {StructureVerified:true,Locked:false,StoredResultIds.IsEmpty:true,ParentFieldId:null,InstructionAttribute:not null}))
            .Where(f=>f.Command is ("PAGEREF" or "REF")&&f.BookmarkId is not null).ToArray();
        var requested=fields.Select(f=>f.BookmarkId!).ToHashSet(StringComparer.Ordinal);
        var textTargets=fields.Where(f=>f.Command=="REF").Select(f=>f.BookmarkId!).ToHashSet(StringComparer.Ordinal);
        if(requested.Count==0)return result;
        if(requested.Count>MaxBodyPageFields+MaxStoryTemplates)throw new InvalidDataException("PAGE_BOOKMARK_INDEX_LIMIT");
        var ends=model.References.Where(r=>r.Kind=="bookmarkEnd"&&r.Status==ReferenceStatus.Resolved&&r.TargetId is not null)
            .ToLookup(r=>r.TargetId!,StringComparer.Ordinal);
        string? body=model.Stories.FirstOrDefault(s=>s.Kind=="body")?.RootId;
        var numericMarkers=new Dictionary<int,int>();
        foreach(var e in model.Elements)
        {
            check();
            if(e.StoryId==body&&e.Coverage==SemanticCoverage.Represented&&e.Kind is SemanticKind.BookmarkStart or SemanticKind.BookmarkEnd&&
                BookmarkNumber(e.Id) is {} number)
                numericMarkers[number]=numericMarkers.GetValueOrDefault(number)+1;
        }
        foreach(string id in requested)
        {
            check();
            var start=model.Element(id);
            var end=SemanticIndex.Unique(ends[id]);
            if(start.StoryId!=body||end is null||BookmarkNumber(id) is not {} number||numericMarkers.GetValueOrDefault(number)!=2||
                model.Revision(id)!="unchanged"||model.Revision(end.SourceId)!="unchanged"||
                model.Attribute(id,"colFirst") is not null||model.Attribute(id,"colLast") is not null||
                Ancestor(model,id,SemanticKind.Paragraph) is not {} paragraph||!text.TryGetValue(paragraph,out var p))continue;
            int after=p.ContextLength,before=0;
            foreach(var f in p.Fragments)
            foreach(var s in f.Sources)
            {
                check();
                Track(s.Source.NodeId,f.ContextOffset+s.FragmentRange.Start,f.ContextOffset+s.FragmentRange.EndExclusive);
            }
            foreach(var c in p.Controls)
            {
                check();
                Track(c.Source.NodeId,c.Offset,c.Offset+1);
            }
            if(before==after)result.Add(id,new(id,paragraph,after,p.ContextLength)
            {
                Text=textTargets.Contains(id)?ReadBookmarkText(model,id,end.SourceId,p,after):null
            });

            void Track(string node,int left,int right)
            {
                if(model.Element(node).Order<start.Order)before=Math.Max(before,right);
                else after=Math.Min(after,left);
            }
        }
        return result;

        int? BookmarkNumber(string id)=>int.TryParse(model.Attribute(id,"id"),NumberStyles.None,
            CultureInfo.InvariantCulture,out int value)?value:null;
    }

    static BookmarkText? ReadBookmarkText(SemanticDocument model,string start,string end,TextParagraph paragraph,int offset)
    {
        if(model.ParentId(start)!=paragraph.Id||model.ParentId(end)!=paragraph.Id)return null;
        int first=model.Element(start).Order,last=model.Element(end).Order;
        var members=model.Children(paragraph.Id).Where(e=>e.Order>first&&e.Order<last&&
            e.Kind!=SemanticKind.Metadata).Take(2).ToArray();
        if(members is not [{Kind:SemanticKind.Run} run]||model.Revision(run.Id)!="unchanged"||
            model.BlocksOf(run.Id).Take(2).ToArray() is not [var text]||
            model.Name(text.Id)!=SemanticIndex.QName(SemanticIndex.W,"t")||
            model.Children(text.Id).Take(2).ToArray() is not [{Kind:SemanticKind.Text} source]||
            model.RawText(source.Id) is not {Length:>0 and <=64} value||
            !value.All(character=>char.IsAsciiLetterOrDigit(character)||character is '-' or '_')||
            offset>paragraph.ContextLength-value.Length||!paragraph.Context.AsSpan(offset,value.Length).SequenceEqual(value))
            return null;
        var matching=paragraph.Fragments.SelectMany(f=>f.Sources).Where(s=>s.Source.NodeId==source.Id).Take(2).ToArray();
        return matching is [{Role:TextRole.Source,Source.Span:{Start:0} span} slice]&&span.EndExclusive==value.Length?
            new(value,slice.Style):null;
    }

    static string? ResolveBookmarkText(PageBookmark? bookmark,TextStyle style)
    {
        if(bookmark?.Text is not {} text)return null;
        return (text.Style with{Origins=ImmutableDictionary<string,SemanticSource>.Empty})==
            (style with{Origins=ImmutableDictionary<string,SemanticSource>.Empty})?text.Value:null;
    }

    static Dictionary<string,PlacedPageBookmark> PlaceBookmarks(Composition pages,IReadOnlyDictionary<string,PageBookmark> bookmarks,Action check)
    {
        var placed=new Dictionary<string,PlacedPageBookmark>(StringComparer.Ordinal);
        if(bookmarks.Count==0)return placed;
        var byParagraph=bookmarks.Values.ToLookup(b=>b.ParagraphId,StringComparer.Ordinal);
        foreach(var page in pages.Pages)
        foreach(var line in page.Lines)
        {
            check();
            if(line.IsRepeated||line.Native is not {} native)continue;
            foreach(var bookmark in byParagraph[line.ParagraphId])
            {
                check();
                var range=native.ContextRange;
                if(range.Start<=bookmark.Offset&&bookmark.Offset<range.EndExclusive||
                    bookmark.Offset==bookmark.ParagraphLength&&range.EndExclusive==bookmark.Offset&&
                        native.Cut==LineCutKind.End)
                    placed.TryAdd(bookmark.Id,new(page.Number,line.ParagraphId,line.NativeTop,line.X));
            }
        }
        return placed;
    }

    static ImmutableArray<PageFieldValue> ResolveBodyFields(Composition trial,ImmutableArray<PageSection> sections,
        IReadOnlyDictionary<string,BodyPageField> fields,IReadOnlyDictionary<string,PageBookmark> bookmarks,
        IReadOnlyDictionary<string,PlacedPageBookmark> anchors,SemanticDocument model,bool complete,Action check)
    {
        var occurrences=new Dictionary<string,(int Page,int Section)>(StringComparer.Ordinal);
        var sectionTotals=complete&&fields.Values.Any(f=>f.Field.Command=="SECTIONPAGES")?
            SectionPageTotals(trial,check):null;
        foreach(var page in trial.Pages)
        foreach(var line in page.Lines)
        {
            check();
            if(!line.IsRepeated&&line.Native is {} native)
                foreach(var field in native.Generated.Where(d=>d.Kind=="pageField"))
                    occurrences.Add(field.ResultId??field.Cause.NodeId,(page.Number,line.Section));
        }
        var result=ImmutableArray.CreateBuilder<PageFieldValue>(fields.Count);
        foreach(var plan in fields.Values)
        {
            check();
            var field=plan.Field;
            string? target=plan.TocRow?.Destination.NodeId??field.BookmarkId;
            int? page=null;
            string? label=null,reason=null;
            bool reference=plan.TocRow is not null||field.Command is "PAGEREF" or "REF";
            if(reference&&(target is null||!bookmarks.ContainsKey(target)))
                reason="PAGE_FIELD_BOOKMARK_UNRESOLVED";
            else if(reference&&!anchors.ContainsKey(target!))
                reason="PAGE_FIELD_BOOKMARK_UNPLACED";
            else if(!occurrences.TryGetValue(plan.ResultId,out var origin))
                reason="PAGE_FIELD_UNPLACED";
            else if(plan.PropertyValue is {} propertyValue)
            {
                page=origin.Page;
                label=propertyValue;
            }
            else if(field.Command=="REF")
            {
                page=anchors[target!].Page;
                label=ResolveBookmarkText(bookmarks.GetValueOrDefault(target!),plan.Style);
                if(label is null)reason="PAGE_FIELD_REF_TEXT_UNQUALIFIED";
            }
            else if(field.Command=="NUMPAGES")
            {
                if(complete)label=FormatPageNumber(trial.Pages.Count,"decimal",plan.Syntax);
                else reason="PAGE_FIELD_TOTAL_UNRESOLVED";
            }
            else if(field.Command=="SECTIONPAGES")
            {
                if(sectionTotals is not null&&sectionTotals.TryGetValue(origin.Section,out int total))
                    label=FormatPageNumber(total,"decimal",plan.Syntax);
                else reason="PAGE_FIELD_SECTION_TOTAL_UNRESOLVED";
            }
            else
            {
                page=target is null?origin.Page:anchors[target].Page;
                var targetPage=trial.Pages[page.Value-1];
                string format=sections[targetPage.Section-1].PageNumberFormat;
                label=FormatPageNumber(targetPage.PrintedPageNumber,format,plan.Syntax);
                if(plan.Syntax.Relative&&label is not null)
                    label=page!=origin.Page?"on page "+label:
                        model.Element(target!).Order<model.Element(field.Id).Order?"above":"below";
            }
            if(label is null)reason??="PAGE_FIELD_NUMBER_UNRESOLVED";
            result.Add(new(plan.ResultId,target,page,label,reason));
        }
        return result.MoveToImmutable();
    }

    static IEnumerable<int> PhysicalPageSections(ComposedPage page) =>
        page.Lines.Select(line=>line.Section).Append(page.Section).Distinct().Order();

    static Dictionary<int,int>? SectionPageTotals(Composition composition,Action check)
    {
        var totals=new Dictionary<int,int>();
        foreach(var page in composition.Pages)
        {
            check();
            // Ownership of an implicit parity sheet for SECTIONPAGES still needs a reference.
            if(page.IntercalarySectionBreak is not null)return null;
            foreach(int section in PhysicalPageSections(page))
                totals[section]=totals.GetValueOrDefault(section)+1;
        }
        return totals;
    }

    static string FieldState(Composition trial,ImmutableArray<PageFieldValue> values,
        IReadOnlyDictionary<string,PlacedPageBookmark> anchors,string? obstacle,Action check)
    {
        var state=new StringBuilder();
        state.AppendLine(obstacle);
        foreach(var value in values)
            state.AppendLine(FormattableString.Invariant($"{value.FieldId}|{value.PhysicalPage}|{value.Text}|{value.Diagnostic}"));
        foreach(var (id,anchor) in anchors.OrderBy(x=>x.Key,StringComparer.Ordinal))
            state.AppendLine(FormattableString.Invariant($"{id}|{anchor.Page}|{anchor.Top:R}|{anchor.Left:R}"));
        foreach(var page in trial.Pages)
        {
            check();
            state.AppendLine(FormattableString.Invariant(
                $"{page.Number}|{page.Section}|{page.PrintedPageNumber}|{page.IntercalarySectionBreak?.NodeId}"));
            foreach(var line in page.Lines)
            {
                check();
                var native=line.Native!;
                state.AppendLine(FormattableString.Invariant(
                    $"{line.ParagraphId}|{line.Section}|{line.FlowRegion?.Band}|{line.FlowRegion?.Column}|{line.IsRepeated}|{native.Index}|{native.ContextRange.Start}|{native.ContextRange.EndExclusive}|{native.Cut}|{native.Resume.ConsecutiveHyphens}|{native.Region.Left}|{native.Region.Width}|{line.NativeOffsetX:R}|{line.NativeTop:R}|{line.Height:R}|{native.Occupied}|{native.ListMarker?.Label.NumId}|{native.ListMarker?.Label.Level}|{native.ListMarker?.Label.Text}"));
                foreach(var part in native.Parts)
                    if(part.Control is {Kind:"Drawing" or "NoteCall"} control)
                        state.AppendLine(FormattableString.Invariant($"{control.Kind}|{control.Source.NodeId}|{control.Offset}"));
                if(line.SideExclusion is {} exclusion)
                    state.AppendLine(FormattableString.Invariant($"{exclusion.Left}|{exclusion.Top}|{exclusion.Width}|{exclusion.Height}"));
                if(line.ParagraphBorder is {} border)
                    state.AppendLine(FormattableString.Invariant($"{border.Border.Source}|{border.Left:R}|{border.Right:R}|{border.Top:R}|{border.Border.Width:R}|{border.Border.Color}"));
                foreach(var field in native.Generated.Where(g=>g.Kind=="pageField"))
                    state.AppendLine(FormattableString.Invariant($"{field.Cause.NodeId}|{field.X}|{field.Fragment.Measure!.Metrics.Width}"));
            }
            foreach(var cell in page.Cells)
            {
                check();
                state.AppendLine(FormattableString.Invariant($"{cell.CellId}|{cell.X:R}|{cell.Y:R}|{cell.Width:R}|{cell.Height:R}|{cell.RowFragment?.RowId}|{cell.RowFragment?.Index}"));
                if(cell.CellFragment is {} fragment)
                    state.AppendLine(FormattableString.Invariant($"{fragment.FirstRow}|{fragment.LastRow}|{fragment.First}|{fragment.Last}"));
            }
            foreach(var note in page.Footnotes)
            {
                check();
                state.AppendLine(FormattableString.Invariant($"{note.Source.NodeId}|{note.Call?.NodeId}|{note.Continued}|{note.Region.Top}|{note.Region.Height}"));
                foreach(var line in note.Lines)
                {
                    state.AppendLine(FormattableString.Invariant($"{line.ParagraphId}|{line.Native?.Index}|{line.NativeTop:R}|{line.Height:R}"));
                    if(line.NoteRule is {} rule)
                        state.AppendLine(FormattableString.Invariant($"{rule.Source.NodeId}|{rule.Width:R}"));
                }
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }

    static string? FormatPageNumber(int? value,string inherited,PageFieldSyntax syntax)
    {
        if(value is not {} number)return null;
        if(syntax.DecimalPlaces>0)return number.ToString(new string('0',syntax.DecimalPlaces),CultureInfo.InvariantCulture);
        string format=syntax.Format??inherited;
        if(format=="decimal")return number.ToString(CultureInfo.InvariantCulture);
        if(format is "upperLetter" or "lowerLetter"&&number is >=1 and <=3328)
        {
            char first=format=="lowerLetter"?'a':'A';
            return new((char)(first+(number-1)%26),(number-1)/26+1);
        }
        if(format is not ("upperRoman" or "lowerRoman")||number is <1 or >3999)return null;
        var label=new StringBuilder();
        foreach(var (amount,symbol) in new (int,string)[]
            {(1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I")})
            while(number>=amount){label.Append(symbol);number-=amount;}
        return format=="lowerRoman"?label.ToString().ToLowerInvariant():label.ToString();
    }
}
