
namespace LecteurWord.Flow;

internal static partial class PageComposer
{
    sealed record PendingStoryField(SemanticField Field,PageFieldSyntax? Syntax,TextParagraph Paragraph,TextStyle Style,
        Utf16Span? StoredRange=null,string? PropertyValue=null)
    {
        internal bool Inline {get;init;}
    }

    static PendingStoryField? ReadInlineStoryField(SemanticDocument model,SemanticField field,TextParagraph paragraph,
        TypographySession typography,IReadOnlySet<string> parentFields,Action check,bool requireFollowingContent=false)
    {
        if(field is not {StructureVerified:true,Locked:false,ParentFieldId:null,
            Command:"PAGE" or "NUMPAGES" or "SECTIONPAGES",EndId:{} end}||parentFields.Contains(field.Id)||
            Ancestor(model,field.Id,SemanticKind.Paragraph)!=paragraph.Id||Ancestor(model,end,SemanticKind.Paragraph)!=paragraph.Id||
            PageFieldSyntax.Read(model,field) is not {Relative:false,Hyperlink:false} syntax||
            paragraph.RightToLeft||paragraph.Fragments.Any(f=>f.BidiLevel%2!=0))
            return null;
        var run=Ancestor(model,field.SeparatorId??field.Id,SemanticKind.Run);
        var style=run is null?paragraph.MarkStyle:typography.PageFieldStyle(run);
        if(!field.StoredResultIds.IsEmpty)
        {
            if(ReadFieldTextCache(model,field,paragraph,check,terminal:false) is not {} cache||
                requireFollowingContent&&cache.Range.EndExclusive==paragraph.ContextLength||
                cache.Range.Start>0&&!char.IsWhiteSpace(paragraph.Context[cache.Range.Start-1])||
                cache.Range.EndExclusive<paragraph.ContextLength&&!char.IsWhiteSpace(paragraph.Context[cache.Range.EndExclusive]))
                return null;
            int first=model.Element(field.Id).Order,last=model.Element(end).Order;
            if(paragraph.Controls.Any(c=>model.Element(c.Source.NodeId).Order>first&&model.Element(c.Source.NodeId).Order<last))
                return null;
            style=cache.Style;
        }
        return style.SuppressText||style.RightToLeft||style.Complex||style.Underline!="none"||style.Background is not null?
            null:new(field,syntax,paragraph,style){Inline=true};
    }

    static string? ReadPropertyOperand(SemanticDocument model,SemanticField field)
    {
        if(!PageFieldSyntax.ValidFlags(model,field)||PageFieldSyntax.Instruction(model,field) is not {} instruction)return null;
        var syntax=instruction.AsSpan().Trim();
        int separator=syntax.IndexOfAny(" \t\r\n");
        if(separator<0)return null;
        var operand=syntax[separator..].Trim();
        if(operand.Length>=2&&operand[0]=='"'&&operand[^1]=='"')operand=operand[1..^1];
        else if(operand.IndexOfAny(" \t\r\n")>=0)return null;
        return operand.IsEmpty||operand.IndexOfAny('"','\\')>=0?null:operand.ToString();
    }

    static PendingStoryField? ReadStoryField(SemanticDocument model,SemanticField field,
        PaginatedDocument layout,IReadOnlyDictionary<string,TextParagraph> text,TypographySession typography,
        IReadOnlySet<string> parentFields,Action check,string? propertyValue=null)
    {
        var syntax=field.Command=="DOCPROPERTY"?null:PageFieldSyntax.Read(model,field,allowCachedPageStyle:true);
        if(field is not {StructureVerified:true,Locked:false,ParentFieldId:null,
            Command:"PAGE" or "NUMPAGES" or "SECTIONPAGES" or "PAGEREF" or "REF" or "DOCPROPERTY",EndId:{} endId}||parentFields.Contains(field.Id)||
            propertyValue is null&&syntax is not {Relative:false,Hyperlink:false}||
            Ancestor(model,field.Id,SemanticKind.Paragraph) is not {} id||
            Ancestor(model,endId,SemanticKind.Paragraph)!=id||!text.TryGetValue(id,out var paragraph)||
            paragraph.RightToLeft||paragraph.Fragments.Any(f=>f.BidiLevel%2!=0))return null;

        if(field.Command is not ("PAGE" or "SECTIONPAGES" or "DOCPROPERTY")&&(field.InstructionAttribute is null||!field.StoredResultIds.IsEmpty||
            layout.Tables.Count!=0||paragraph.ContextLength!=0||
            !model.BlocksOf(id).Select(e=>e.Id).SequenceEqual([field.Id])))return null;

        int order=model.Element(field.Id).Order;
        var cache=field.StoredResultIds.ToHashSet(StringComparer.Ordinal);
        if(paragraph.Controls.Any(c=>cache.Contains(c.Source.NodeId)||model.Element(c.Source.NodeId).Order>=order))return null;
        var prefix=paragraph.Fragments.TakeWhile(f=>f.Sources.All(s=>model.Element(s.Source.NodeId).Order<order)).ToArray();
        var result=paragraph.Fragments.Skip(prefix.Length).ToArray();
        if(syntax?.RetainCachedStyle==true||result.Any(f=>f.Sources.Any(s=>!cache.Contains(s.Source.NodeId)))||
            cache.Count>0&&(!field.StoredResultIds.All(n=>model.Element(n).Kind==SemanticKind.Text)||
                model.StoredText(field)!=string.Concat(result.Select(f=>f.Text))))
        {
            if(layout.Tables.Count!=0||ReadTerminalFieldCache(model,field,paragraph,check) is not {} terminal||
                terminal.Range.Start>0&&paragraph.Context[terminal.Range.Start-1] is not (' ' or '\t' or '\n')||
                !SupportedStyle(terminal.Style))return null;
            if(result.SelectMany(f=>f.Sources).Any(s=>!cache.Contains(s.Source.NodeId)&&model.Element(s.Source.NodeId).Order>=order))
                return null;
            return new(field,syntax,paragraph,terminal.Style,terminal.Range,propertyValue);
        }

        int offset=result.Length==0?paragraph.ContextLength:result[0].ContextOffset;
        if(offset>0&&paragraph.Context[offset-1] is not (' ' or '\t' or '\n'))return null;
        var runId=field.InstructionAttribute is not null?model.Children(field.Id).FirstOrDefault(e=>e.Kind==SemanticKind.Run).Id:
            Ancestor(model,field.Id,SemanticKind.Run);
        if(field.SeparatorId is {} separator)
        {
            runId=Ancestor(model,separator,SemanticKind.Run);
            for(int i=model.Element(separator).Order+1;i<model.Element(endId).Order;i++)
            {
                check();
                var element=model.Elements[i];
                if(element.Kind!=SemanticKind.Run||element.Id==model.ParentId(endId))continue;
                runId=element.Id;
                break;
            }
        }
        if(result.Length>0)
        {
            var resultRuns=result.SelectMany(f=>f.Sources).Select(s=>s.RunId).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if(resultRuns is not [var cachedRun]||cachedRun is null)return null;
            runId=cachedRun;
        }
        var style=runId is null?paragraph.MarkStyle:typography.PageFieldStyle(runId);
        if(!SupportedStyle(style))return null;

        if(result.Length>0)
            paragraph=paragraph with
            {
                Context=paragraph.Context[..offset],ContextLength=offset,Fragments=[..prefix],
                Breaks=[..paragraph.Breaks.Where(b=>b.Offset<=offset)],
                VisualFragmentOrder=[..paragraph.VisualFragmentOrder.Where(i=>i<prefix.Length)]
            };
        return new(field,syntax,paragraph,style,PropertyValue:propertyValue);

        static bool SupportedStyle(TextStyle style)=>!style.SuppressText&&!style.RightToLeft&&!style.Complex&&style.Underline=="none";
    }
}

internal sealed partial class TypographySession
{
    internal TextParagraph ShapeInlineStoryFields(TextParagraph source,IEnumerable<TextToken> original,
        IReadOnlyList<GeneratedPageField> generated,IReadOnlySet<string> replacedCache)
    {
        var fields=generated.ToDictionary(f=>f.Source.NodeId,StringComparer.Ordinal);
        var tokens=original.Where(t=>!replacedCache.Contains(t.Source.NodeId)).Concat(generated.Select(f=>new TextToken(f.Source,null,source.Id,source.StoryId,
            TextRole.Control,null,"InlinePageField",f.Style))).OrderBy(t=>model.Element(t.Source.NodeId).Order);
        var paragraph=BuildParagraph(model.Element(source.Id),tokens,source.MarkStyle);
        return paragraph with
        {
            Controls=[..paragraph.Controls.Select(c=>c.Kind=="InlinePageField"?
                c with{Symbol=fields[c.Source.NodeId].Measured}:c)]
        };
    }
}
