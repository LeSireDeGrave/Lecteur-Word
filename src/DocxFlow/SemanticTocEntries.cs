using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using static LecteurWord.Flow.SemanticIndex;

namespace LecteurWord.Flow;

internal sealed record SemanticTocEntry(string FieldId,string ParagraphId,string Label,string? Group,int Level,bool OmitPageNumber,
    ImmutableArray<string> OperandFieldIds,ImmutableArray<string> SelectingTocIds);

internal static partial class SemanticReader
{
    sealed partial class Builder
    {
        const int TocInstructionLimit=4096;
        sealed record TocSelector(string Id,string StoryId,string? Group,int First,int Last);

        ImmutableArray<SemanticTocEntry> BuildTocEntries(ImmutableArray<SemanticField> fields)
        {
            if(!fields.Any(f=>f.Command=="TC"))return [];
            var operands=fields.Where(f=>f.ParentFieldId is not null).ToLookup(f=>f.ParentFieldId!,StringComparer.Ordinal);
            var selectors=fields.Where(f=>f.Command=="TOC"&&f.StructureVerified).Select(ReadSelector).OfType<TocSelector>()
                .ToLookup(s=>s.StoryId,StringComparer.Ordinal);
            var entries=ImmutableArray.CreateBuilder<SemanticTocEntry>();
            foreach(var field in fields)
            {
                Check();
                if(field.Command!="TC")continue;
                var children=operands[field.Id].Take(9).ToArray();
                if(ReadEntry(field,children,operands,selectors[field.StoryId]) is {} entry)entries.Add(entry);
                else Issue("SEMANTIC_TC_UNQUALIFIED",index.Nodes[field.Id],
                    "Le marqueur TC et ses opérandes restent inventoriés. Les paramètres de cette entrée sont hors profil.");
            }
            return entries.ToImmutable();
        }

        SemanticTocEntry? ReadEntry(SemanticField field,SemanticField[] operands,ILookup<string,SemanticField> children,
            IEnumerable<TocSelector> selectors)
        {
            if(!field.StructureVerified||field.ParentFieldId is not null||field.SeparatorId is not null||
                !field.StoredResultIds.IsEmpty||operands.Length>8||FindParagraph(field.Id) is not {} paragraph||
                field.EndId is not {} end||FindParagraph(end)!=paragraph||
                ReadTocInstruction(field) is not {} code)return null;

            int first=code.IndexOf('"'),last=first<0?-1:code.IndexOf('"',first+1);
            if(first<0||last<0||!code.AsSpan(0,first).Trim().Equals("TC",StringComparison.OrdinalIgnoreCase)||
                code.AsSpan(first+1,last-first-1).Contains('\\')||
                last+1<code.Length&&!char.IsWhiteSpace(code[last+1]))return null;
            int level=1;
            string? group=null;
            bool omit=false,levelDefined=false;
            var options=Words(code[(last+1)..]);
            for(int i=0;i<options.Length;i++)
            {
                switch(options[i].ToLowerInvariant())
                {
                    case "\\f" when group is null&&i+1<options.Length:
                        group=Group(options[++i]);
                        if(group is null)return null;
                        break;
                    case "\\l" when !levelDefined&&i+1<options.Length:
                        if(!ReadLevel(options[++i],out level))return null;
                        levelDefined=true;
                        break;
                    case "\\n" when !omit:
                        omit=true;
                        break;
                    default:
                        return null;
                }
            }
            var label=new StringBuilder();
            int previous=first+1,sourceOffset=0,instructionIndex=0;
            foreach(var operand in operands)
            {
                Check();
                while(instructionIndex<field.InstructionIds.Length&&
                    elements[field.InstructionIds[instructionIndex]].Order<elements[operand.Id].Order)
                    sourceOffset+=index.Nodes[field.InstructionIds[instructionIndex++]].Text?.Length??0;
                if(sourceOffset<previous||sourceOffset>last||FindParagraph(operand.Id)!=paragraph||
                    operand.EndId is not {} operandEnd||FindParagraph(operandEnd)!=paragraph||
                    children[operand.Id].Any()||ReadOperand(operand) is not {} value)return null;
                label.Append(code.AsSpan(previous,sourceOffset-previous)).Append(value);
                if(label.Length>TocInstructionLimit)return null;
                previous=sourceOffset;
            }
            label.Append(code.AsSpan(previous,last-previous));
            if(label.Length>TocInstructionLimit||string.IsNullOrWhiteSpace(label.ToString()))return null;
            var selected=ImmutableArray.CreateBuilder<string>();
            foreach(var selector in selectors)
            {
                Check();
                if((selector.Group is null||selector.Group==group)&&level>=selector.First&&level<=selector.Last)
                    selected.Add(selector.Id);
            }
            return new(field.Id,paragraph,label.ToString(),group,level,omit,
                [..operands.Select(o=>o.Id)],selected.ToImmutable());
        }

        string? ReadOperand(SemanticField field)
        {
            if(!field.StructureVerified||field.StoredResultIds.IsEmpty||ReadTocInstruction(field) is not {} instruction)return null;
            var words=Words(instruction);
            if(words.Length<2)return null;
            var stored=new StringBuilder();
            foreach(string id in field.StoredResultIds)
            {
                Check();
                if(elements[id].Kind!=SemanticKind.Text||index.Nodes[id].Text is not {} value||value.Length>TocInstructionLimit-stored.Length)
                    return null;
                stored.Append(value);
            }
            string result=stored.ToString();
            if(result.Length==0)return null;
            if(field.Command=="REF")
                return words.Length==2&&field.BookmarkId is not null?result:null;
            if(field.Command!="SEQ"||!words[1].All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')||
                !int.TryParse(result,NumberStyles.None,CultureInfo.InvariantCulture,out _))return null;
            bool reset=false,arabic=false;
            for(int i=2;i<words.Length;i++)
            {
                if(i+1>=words.Length)return null;
                if(words[i].Equals("\\r",StringComparison.OrdinalIgnoreCase)&&!reset&&
                    int.TryParse(words[i+1],NumberStyles.None,CultureInfo.InvariantCulture,out _))reset=true;
                else if(words[i]=="\\*"&&!arabic&&words[i+1].Equals("Arabic",StringComparison.OrdinalIgnoreCase))arabic=true;
                else return null;
                i++;
            }
            return result;
        }

        TocSelector? ReadSelector(SemanticField field)
        {
            if(ReadTocInstruction(field) is not {} code)return null;
            var words=Words(code);
            string? group=null;
            int first=1,last=9;
            bool filter=false,levels=false,hyperlinks=false;
            for(int i=1;i<words.Length;i++)
            {
                switch(words[i].ToLowerInvariant())
                {
                    case "\\h" when !hyperlinks:
                        hyperlinks=true;
                        break;
                    case "\\f" when !filter:
                        filter=true;
                        if(i+1<words.Length&&!words[i+1].StartsWith('\\'))
                        {
                            group=Group(words[++i]);
                            if(group is null)return null;
                        }
                        first=1;last=9;
                        break;
                    case "\\l" when !levels&&i+1<words.Length:
                        var range=words[++i].Trim('"').Split('-');
                        if(range.Length!=2||!ReadLevel(range[0],out first)||!ReadLevel(range[1],out last)||first>last)return null;
                        levels=true;
                        break;
                    default:
                        return null;
                }
            }
            return filter?new(field.Id,field.StoryId,group,first,last):null;
        }

        string? ReadTocInstruction(SemanticField field)
        {
            if(field.InstructionAttribute is {} value)return value.Length<=TocInstructionLimit?value:null;
            var code=new StringBuilder();
            foreach(string id in field.InstructionIds)
            {
                Check();
                string? text=index.Nodes[id].Text;
                if(text is null||text.Length>TocInstructionLimit-code.Length)return null;
                code.Append(text);
            }
            return code.ToString();
        }

        string? FindParagraph(string id)
        {
            for(var node=index.Nodes[id];node is not null;node=index.Parent(node))
                if(Word(node,"p"))return node.Id;
            return null;
        }

        static string[] Words(string text)=>text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        static string? Group(string value)
        {
            if(value.Length==3&&value[0]=='"'&&value[2]=='"')value=value[1..2];
            return value.Length==1&&char.IsAsciiLetter(value[0])?value.ToUpperInvariant():null;
        }

        static bool ReadLevel(string value,out int level)=>
            int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out level)&&level is >=1 and <=9;
    }
}
