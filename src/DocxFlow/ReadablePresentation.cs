using System.Globalization;
using System.Diagnostics;
using System.Text;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public sealed record ReadableNoteLink(string ReferenceId,string OriginBlockId,string Kind,string Number,bool Verified,SourceLocation Source,IReadOnlyList<ReadableBlock> Content);
public sealed record ReadableFieldValue(string Id,string Name,string Code,bool Known,bool Matched,bool HasStoredValue,string StoredValue,SourceLocation Source);
public sealed record ReadableStory(string Id,int Section,string Kind,string Variant,bool Inherited,bool Verified,SourceLocation Source,IReadOnlyList<ReadableBlock> Content);
public sealed record ReadableSection(int Number,string? ClosingBlockId,IReadOnlyList<ReadableStory> Stories);

/// <summary>Reading aids only. Source blocks and the diagnostic inventory are retained verbatim.</summary>
public sealed class ReadablePresentation
{
    public IReadOnlyList<ReadableBlock> BodyBlocks {get;init;}=[];
    public IReadOnlyDictionary<string,ReadableNoteLink> Notes {get;init;}=new Dictionary<string,ReadableNoteLink>();
    public IReadOnlyDictionary<string,ReadableFieldValue> Fields {get;init;}=new Dictionary<string,ReadableFieldValue>();
    public IReadOnlySet<string> FieldDetailNodes {get;init;}=new HashSet<string>();
    public IReadOnlyList<ReadableSection> Sections {get;init;}=[];
    public IReadOnlyList<FlowIssue> Issues {get;init;}=[];
    public string TextFor(ReadableBlock block)
    {
        if(block.Kind=="note"&&Notes.TryGetValue(block.Id,out var note)&&note.Verified)return "";
        if(FieldDetailNodes.Contains(block.Id))return "";
        if(block.Kind=="field")return Fields.GetValueOrDefault(block.Id) is {Matched:true}?string.Concat(block.Children.Select(TextFor)):"";
        string own=block.Children.Count==0?block.Text:block.Kind is "paragraph" or "heading" or "run" or "link"?"":block.Text;
        return own+string.Concat(block.Children.Select(TextFor));
    }
    static readonly HashSet<string> KnownFields=new(StringComparer.OrdinalIgnoreCase){"PAGE","NUMPAGES","SECTION","SECTIONPAGES","DATE","TIME","CREATEDATE","SAVEDATE","PRINTDATE","AUTHOR","TITLE","SUBJECT","KEYWORDS","FILENAME","DOCPROPERTY","REF","PAGEREF","HYPERLINK","STYLEREF","SEQ","TOC","TC","NUMWORDS","NUMCHARS"};
    public static IEnumerable<ReadableBlock> Walk(IEnumerable<ReadableBlock> blocks)
    {foreach(var b in blocks){yield return b;foreach(var c in Walk(b.Children))yield return c;}}

    internal static ReadablePresentation Create(InspectionResult input,ReadableDocument document,SemanticIndex index,CancellationToken token)
        =>new Builder(input,document,index,token).Build();
    sealed class Builder
    {
        const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        readonly InspectionResult input;readonly ReadableDocument doc;readonly CancellationToken token;
        readonly IReadOnlyDictionary<string,InventoryNode> nodes;readonly SemanticIndex index;
        readonly Dictionary<string,ReadableBlock> blocks;
        readonly List<FlowIssue> issues=[];
        readonly Dictionary<string,ReadableFieldValue> fields=[];readonly HashSet<string> details=[];
        readonly Stopwatch timer=Stopwatch.StartNew();
        string main="";
        public Builder(InspectionResult input,ReadableDocument doc,SemanticIndex index,CancellationToken token)
        {this.input=input;this.doc=doc;this.token=token;this.index=index;nodes=index.Nodes;blocks=Walk(doc.Blocks).GroupBy(b=>b.Id).ToDictionary(g=>g.Key,g=>g.First());}
        void Check(){token.ThrowIfCancellationRequested();if(timer.ElapsedMilliseconds>10_000)throw new InvalidOperationException("Reading aids time budget exceeded");}
        static bool Is(InventoryNode n,string local)=>n.Name=="{"+W+"}"+local;
        static string Attr(InventoryNode n,string local)=>n.Attributes.GetValueOrDefault("{"+W+"}"+local)??"";
        InventoryNode? Parent(InventoryNode n)=>n.ParentId is {} id?nodes.GetValueOrDefault(id):null;
        void Issue(string code,SourceLocation source,string message)=>issues.Add(new(code,source,message,false));
        public ReadablePresentation Build()
        {
            main=index.Main;
            var body=doc.Blocks.Where(b=>b.Source.PartUri==main).ToArray();
            var notes=new Dictionary<string,ReadableNoteLink>();var numbers=new Dictionary<string,string>();int foot=0,end=0;
            foreach(var top in body)
            foreach(var b in Walk([top]).Where(b=>b.Kind=="note"))
            {
                Check();var n=nodes.GetValueOrDefault(b.Id);string kind=b.NoteKind??"";
                string? part=n is null?null:index.Related(n.Source.PartUri,kind+"s")?.ResolvedPart;
                var definition=index.Definition(part,kind,b.NoteId??"");
                bool verified=n is not null&&n.Source.PartUri==main&&Parent(n) is {} parent&&Is(parent,"r")&&definition is not null&&b.Children.Count>0&&
                    Attr(definition,"type") is "" or "normal"&&Attr(n,"customMarkFollows") is not ("1" or "true" or "on")&&
                    int.TryParse(b.NoteId,NumberStyles.Integer,CultureInfo.InvariantCulture,out int id)&&id>0;
                // Nested note calls cannot be assigned a safe main-story return anchor.
                if(top.Source.PartUri!=b.Source.PartUri)verified=false;
                string number="";string key=part+":"+kind+":"+b.NoteId;
                if(verified&&!numbers.TryGetValue(key,out number!)){number=(kind=="footnote"?++foot:++end).ToString(CultureInfo.InvariantCulture);numbers[key]=number;}
                notes[b.Id]=new(b.Id,top.Id,kind,number,verified,b.Source,b.Children);
                if(!verified)Issue("READ_NOTE_LINK_UNVERIFIED",b.Source,"Correspondance de note incertaine ; aucun appel numéroté n’est inventé.");
            }
            // Parse fields per story, never across distinct note bodies.
            foreach(var root in input.Manifest.Nodes.Where(n=>Is(n,"body")||Is(n,"hdr")||Is(n,"ftr")||Is(n,"footnote")||Is(n,"endnote")))
                if(!index.IsUnderInactiveBranch(root))ScanFields(root);
            return new(){BodyBlocks=body,Notes=notes,Fields=fields,FieldDetailNodes=details,Sections=Sections(body),Issues=issues};
        }
        sealed class Field(InventoryNode begin,bool simple)
        {
            internal InventoryNode Begin {get;}=begin;
            internal bool Simple {get;}=simple;
            internal StringBuilder Code {get;}=new();
            internal StringBuilder Value {get;}=new();
            internal List<string> ResultNodes {get;}=[];
            internal bool Separated {get;set;}
            internal bool Unsafe {get;set;}=begin.Revision!="unchanged";
            internal bool InResult=>Simple||Separated;
        }
        void ScanFields(InventoryNode root)
        {
            var stack=new Stack<Field>();
            bool stopped=false;
            void Visit(InventoryNode n)
            {
                Check();
                if(stopped)return;
                bool simple=Is(n,"fldSimple");
                bool boundary=Is(n,"fldChar");
                string kind=boundary?Attr(n,"fldCharType"):"";
                if(simple||kind=="begin")
                {
                    if(stack.Count>=32||fields.Count>=2048)
                    {
                        Issue("READ_FIELD_LIMIT",n.Source,"Analyse contextuelle des champs limitée ; inventaire conservé.");
                        stopped=true;
                        return;
                    }
                    var frame=new Field(n,simple);
                    if(simple&&stack.Any(f=>!f.InResult))details.Add(n.Id);
                    stack.Push(frame);
                    if(simple)
                    {
                        frame.Code.Append(Attr(n,"instr"));
                        foreach(var child in index.Children(n))Visit(child);
                        while(!ReferenceEquals(stack.Peek(),frame))Finish(stack.Pop(),false);
                        Finish(stack.Pop(),true);
                    }
                    else details.Add(n.Id);
                    return;
                }
                if(boundary)
                {
                    details.Add(n.Id);
                    if(!stack.TryPeek(out var active)||active.Simple)
                        Issue("READ_FIELD_STRUCTURE_UNVERIFIED",n.Source,"Marqueur de champ sans ouverture complexe correspondante.");
                    else
                    {
                        active.Unsafe|=n.Revision!="unchanged";
                        if(kind=="end")Finish(stack.Pop(),true);
                        else if(kind=="separate"&&!active.Separated)active.Separated=true;
                        else active.Unsafe=true;
                    }
                    return;
                }
                if(Is(n,"instrText")||Is(n,"delInstrText"))
                {
                    details.Add(n.Id);
                    if(stack.TryPeek(out var active)&&!active.InResult)
                        active.Code.Append(RawText(n));
                    return;
                }
                if(stack.Count>0)
                {
                    foreach(var frame in stack)frame.Unsafe|=n.Revision!="unchanged";
                    bool text=Is(n,"t")||Is(n,"delText");
                    bool tab=Is(n,"tab"),lineBreak=Is(n,"br")||Is(n,"cr");
                    if(text||tab||lineBreak||n.Kind is "drawing" or "noteReference"||Is(n,"sym"))
                    {
                        if(stack.Any(f=>!f.InResult))details.Add(n.Id);
                        foreach(var frame in stack)
                        {
                            if(!frame.InResult)break;
                            frame.ResultNodes.Add(n.Id);
                            if(text)frame.Value.Append(RawText(n));
                            else if(tab)frame.Value.Append('\t');
                            else if(lineBreak)frame.Value.Append('\n');
                        }
                    }
                }
                if(SemanticIndex.IsAlternative(n))
                {
                    if(index.SelectedBranch(n) is {} selected)Visit(selected);
                }
                else foreach(var child in index.Children(n))Visit(child);
            }

            Visit(root);
            while(stack.TryPop(out var frame))Finish(frame,false);

            string RawText(InventoryNode n)=>string.Concat(index.Children(n).Where(c=>c.Kind=="textValue").Select(c=>c.Text));
            void Finish(Field frame,bool closed)
            {
                string code=frame.Code.ToString();
                string name=code.TrimStart().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant()??"";
                bool matched=closed&&!frame.Unsafe&&name.Length>0;
                string value=frame.Value.ToString();
                bool hasValue=matched&&name!="TC"&&!string.IsNullOrWhiteSpace(value);
                fields[frame.Begin.Id]=new(frame.Begin.Id,name,code,KnownFields.Contains(name),matched,hasValue,value,frame.Begin.Source);
                if(name=="TC"||!matched)
                    foreach(var id in frame.ResultNodes)details.Add(id);
                if(!matched)Issue("READ_FIELD_STRUCTURE_UNVERIFIED",frame.Begin.Source,"Champ incomplet ou suivi de révision : structure conservée, résultat non qualifié.");
                else if(name!="TC"&&!hasValue)
                    Issue("READ_FIELD_RESULT_MISSING",frame.Begin.Source,"Champ "+name+" sans résultat enregistré utilisable.");
            }
        }
        IReadOnlyList<ReadableSection> Sections(IReadOnlyList<ReadableBlock> body)
        {
            var ends=input.Manifest.Nodes.Where(n=>n.Source.PartUri==main&&Is(n,"sectPr")&&
                (Parent(n) is {} p&&(Is(p,"body")||Is(p,"pPr")&&Parent(p) is {} para&&Is(para,"p")&&Parent(para) is {} owner&&Is(owner,"body")))).ToArray();
            if(ends.Length>256){Issue("READ_SECTION_LIMIT",ends[0].Source,"Trop de sections pour la vue contextuelle ; inventaire conservé.");return [];}
            var result=new List<ReadableSection>();var inherited=new Dictionary<(string Kind,string Variant),(string Part,SourceLocation Source)>();var used=new HashSet<string>();
            int sectionCount=ends.Length==0?1:ends.Length+(Parent(ends[^1]) is {} lastParent&&Is(lastParent,"body")?0:1);
            for(int sectionIndex=0;sectionIndex<sectionCount;sectionIndex++)
            {
                Check();InventoryNode? end=sectionIndex<ends.Length?ends[sectionIndex]:null;var stories=new List<ReadableStory>();
                if(end is not null)
                foreach(var reference in index.Children(end).Where(n=>Is(n,"headerReference")||Is(n,"footerReference")))
                {
                    string kind=Is(reference,"headerReference")?"header":"footer",variant=Attr(reference,"type");
                    string rid=reference.Attributes.GetValueOrDefault("{"+R+"}id")??"";
                    var relations=input.Manifest.Relationships.Where(r=>r.SourcePart==main&&r.Id==rid&&r.Type==R+"/"+kind&&r.Mode=="Internal").ToArray();
                    inherited[(kind,variant)]=(relations.Length==1?relations[0].ResolvedPart??"":"",reference.Source);
                }
                foreach(var entry in inherited)
                {
                    var story=doc.Blocks.FirstOrDefault(b=>b.Source.PartUri==entry.Value.Part&&b.Source.PartUri!=main);
                    bool direct=end is not null&&index.Children(end).Any(n=>(Is(n,entry.Key.Kind=="header"?"headerReference":"footerReference"))&&Attr(n,"type")==entry.Key.Variant);
                    bool verified=story is not null;var source=story?.Source??entry.Value.Source;
                    stories.Add(new($"story-{sectionIndex+1}-{entry.Key.Kind}-{entry.Key.Variant}",sectionIndex+1,entry.Key.Kind,entry.Key.Variant,!direct,verified,source,story?.Children??[]));
                    used.Add(entry.Value.Part);
                    if(!verified)Issue("READ_STORY_UNRESOLVED",entry.Value.Source,"En-tête ou pied non rattaché ; référence conservée.");
                }
                string? closing=end is not null?body.FirstOrDefault(b=>Walk([b]).Any(c=>c.Id==end.Id))?.Id:null;
                result.Add(new(sectionIndex+1,closing,stories));
            }
            var other=doc.Blocks.Where(b=>b.Source.PartUri!=main&&!used.Contains(b.Source.PartUri)).Select(b=>new ReadableStory("unassigned-"+b.Id,0,b.Label?.StartsWith("En-tête",StringComparison.Ordinal)==true?"header":"footer","unassigned",false,false,b.Source,b.Children)).ToArray();
            if(other.Length>0)result.Add(new(0,null,other));
            return result;
        }
    }
}
