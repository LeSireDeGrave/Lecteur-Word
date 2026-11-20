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
        if(block.Kind=="field")return Fields.ContainsKey(block.Id)?string.Concat(block.Children.Select(TextFor)):"";
        string own=block.Children.Count==0?block.Text:block.Kind is "paragraph" or "heading" or "run" or "link"?"":block.Text;
        return own+string.Concat(block.Children.Select(TextFor));
    }
    static readonly HashSet<string> KnownFields=new(StringComparer.OrdinalIgnoreCase){"PAGE","NUMPAGES","SECTION","SECTIONPAGES","DATE","TIME","CREATEDATE","SAVEDATE","PRINTDATE","AUTHOR","TITLE","SUBJECT","KEYWORDS","FILENAME","DOCPROPERTY","REF","PAGEREF","HYPERLINK","STYLEREF","SEQ","TOC","NUMWORDS","NUMCHARS"};
    public static IEnumerable<ReadableBlock> Walk(IEnumerable<ReadableBlock> blocks)
    {foreach(var b in blocks){yield return b;foreach(var c in Walk(b.Children))yield return c;}}

    internal static ReadablePresentation Create(InspectionResult input,ReadableDocument document,CancellationToken token)
        =>new Builder(input,document,token).Build();
    sealed class Builder
    {
        const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        readonly InspectionResult input;readonly ReadableDocument doc;readonly CancellationToken token;
        readonly Dictionary<string,InventoryNode> nodes;readonly ILookup<string,InventoryNode> children;
        readonly Dictionary<string,ReadableBlock> blocks;
        readonly List<FlowIssue> issues=[];
        readonly Dictionary<string,ReadableFieldValue> fields=[];readonly HashSet<string> details=[];
        readonly Stopwatch timer=Stopwatch.StartNew();
        string main="";
        public Builder(InspectionResult input,ReadableDocument doc,CancellationToken token)
        {this.input=input;this.doc=doc;this.token=token;nodes=input.Manifest.Nodes.ToDictionary(n=>n.Id);children=input.Manifest.Nodes.Where(n=>n.ParentId is not null).ToLookup(n=>n.ParentId!);blocks=Walk(doc.Blocks).GroupBy(b=>b.Id).ToDictionary(g=>g.Key,g=>g.First());}
        void Check(){token.ThrowIfCancellationRequested();if(timer.ElapsedMilliseconds>10_000)throw new InvalidOperationException("Reading aids time budget exceeded");}
        static bool Is(InventoryNode n,string local)=>n.Name=="{"+W+"}"+local;
        static string Attr(InventoryNode n,string local)=>n.Attributes.GetValueOrDefault("{"+W+"}"+local)??"";
        InventoryNode? Parent(InventoryNode n)=>n.ParentId is {} id?nodes.GetValueOrDefault(id):null;
        IEnumerable<InventoryNode> Tree(InventoryNode n){Check();yield return n;foreach(var child in children[n.Id])foreach(var d in Tree(child))yield return d;}
        void Issue(string code,SourceLocation source,string message)=>issues.Add(new(code,source,message,false));
        public ReadablePresentation Build()
        {
            main=input.Manifest.Relationships.FirstOrDefault(r=>r.SourcePart=="/"&&r.Type==R+"/officeDocument")?.ResolvedPart??"";
            var body=doc.Blocks.Where(b=>b.Source.PartUri==main).ToArray();
            var notes=new Dictionary<string,ReadableNoteLink>();var numbers=new Dictionary<string,string>();int foot=0,end=0;
            foreach(var top in body)
            foreach(var b in Walk([top]).Where(b=>b.Kind=="note"))
            {
                Check();var n=nodes.GetValueOrDefault(b.Id);string kind=b.NoteKind??"";
                var relationships=input.Manifest.Relationships.Where(r=>r.SourcePart==main&&r.Type==R+"/"+(kind=="footnote"?"footnotes":"endnotes")&&r.Mode=="Internal").ToArray();
                string? part=relationships.Length==1?relationships[0].ResolvedPart:null;
                var definitions=input.Manifest.Nodes.Where(d=>d.Source.PartUri==part&&Is(d,kind)&&Attr(d,"id")==b.NoteId).ToArray();
                bool verified=n is not null&&n.Source.PartUri==main&&Parent(n) is {} parent&&Is(parent,"r")&&definitions.Length==1&&b.Children.Count>0&&
                    Attr(definitions[0],"type") is "" or "normal"&&Attr(n,"customMarkFollows") is not ("1" or "true" or "on")&&
                    int.TryParse(b.NoteId,NumberStyles.Integer,CultureInfo.InvariantCulture,out int id)&&id>0;
                // Nested note calls cannot be assigned a safe main-story return anchor.
                if(top.Source.PartUri!=b.Source.PartUri)verified=false;
                string number="";string key=part+":"+kind+":"+b.NoteId;
                if(verified&&!numbers.TryGetValue(key,out number!)){number=(kind=="footnote"?++foot:++end).ToString(CultureInfo.InvariantCulture);numbers[key]=number;}
                notes[b.Id]=new(b.Id,top.Id,kind,number,verified,b.Source,b.Children);
                if(!verified)Issue("READ_NOTE_LINK_UNVERIFIED",b.Source,"Correspondance de note incertaine ; aucun appel numéroté n’est inventé.");
            }
            // Parse fields per story, never across distinct note bodies.
            foreach(var root in input.Manifest.Nodes.Where(n=>Is(n,"body")||Is(n,"hdr")||Is(n,"ftr")||Is(n,"footnote")||Is(n,"endnote")))ScanFields(root);
            return new(){BodyBlocks=body,Notes=notes,Fields=fields,FieldDetailNodes=details,Sections=Sections(body),Issues=issues};
        }
        sealed class Field(InventoryNode begin)
        {
            public InventoryNode Begin=begin;public StringBuilder Code=new(),Value=new();public List<string> DetailIds=[];
            public bool Separated,HasValue,Unsafe;
        }
        void ScanFields(InventoryNode root)
        {
            var stack=new Stack<Field>();
            foreach(var n in Tree(root))
            {
                Check();
                if(Is(n,"fldSimple"))
                {
                    var text=Tree(n).Where(t=>Is(t,"t")).ToArray();
                    string value=string.Concat(text.SelectMany(t=>children[t.Id].Where(c=>c.Kind=="textValue")).Select(c=>c.Text));
                    AddField(n,Attr(n,"instr"),value,text.Length>0,n.Revision=="unchanged",[]);
                }
                if(Is(n,"fldChar"))
                {
                    string type=Attr(n,"fldCharType");
                    if(type=="begin")
                    {
                        if(stack.Count>=32||fields.Count>=2048){Issue("READ_FIELD_LIMIT",n.Source,"Analyse contextuelle des champs limitée ; codes et contenu conservés avec repères.");return;}
                        var f=new Field(n){Unsafe=n.Revision!="unchanged"};f.DetailIds.Add(n.Id);stack.Push(f);
                    }
                    else if(stack.TryPeek(out var f))
                    {
                        f.DetailIds.Add(n.Id);
                        if(n.Revision!="unchanged")f.Unsafe=true;
                        if(type=="separate"&&!f.Separated)f.Separated=true;
                        else if(type=="end"){stack.Pop();AddField(f.Begin,f.Code.ToString(),f.Value.ToString(),f.HasValue,f.Separated&&!f.Unsafe,f.DetailIds);}
                        else f.Unsafe=true;
                    }
                    continue;
                }
                if(stack.Count==0)continue;
                foreach(var f in stack)
                {
                    if(n.Revision!="unchanged")f.Unsafe=true;
                    if(Is(n,"instrText")&&!f.Separated){f.DetailIds.Add(n.Id);f.Code.Append(string.Concat(children[n.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)));}
                    if(Is(n,"t")&&f.Separated){f.HasValue=true;f.Value.Append(string.Concat(children[n.Id].Where(c=>c.Kind=="textValue").Select(c=>c.Text)));}
                    if(Is(n,"tab")&&f.Separated)f.Value.Append('\t');
                    if(Is(n,"br")&&f.Separated)f.Value.Append('\n');
                }
            }
            foreach(var f in stack)AddField(f.Begin,f.Code.ToString(),f.Value.ToString(),f.HasValue,false,[]);
        }
        void AddField(InventoryNode n,string code,string value,bool hasValue,bool matched,IReadOnlyList<string> ids)
        {
            string name=code.TrimStart().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant()??"";
            fields[n.Id]=new(n.Id,name,code,KnownFields.Contains(name),matched,hasValue,value,n.Source);
            if(matched)foreach(var id in ids)details.Add(id);
            if(!matched)Issue("READ_FIELD_STRUCTURE_UNVERIFIED",n.Source,"Champ incomplet ou suivi de révision : structure conservée, résultat non qualifié.");
        }
        IReadOnlyList<ReadableSection> Sections(IReadOnlyList<ReadableBlock> body)
        {
            var ends=input.Manifest.Nodes.Where(n=>n.Source.PartUri==main&&Is(n,"sectPr")&&
                (Parent(n) is {} p&&(Is(p,"body")||Is(p,"pPr")&&Parent(p) is {} para&&Is(para,"p")&&Parent(para) is {} owner&&Is(owner,"body")))).ToArray();
            if(ends.Length>256){Issue("READ_SECTION_LIMIT",ends[0].Source,"Trop de sections pour la vue contextuelle ; inventaire conservé.");return [];}
            var result=new List<ReadableSection>();var inherited=new Dictionary<(string Kind,string Variant),(string Part,SourceLocation Source)>();var used=new HashSet<string>();
            int sectionCount=ends.Length==0?1:ends.Length+(Parent(ends[^1]) is {} lastParent&&Is(lastParent,"body")?0:1);
            for(int index=0;index<sectionCount;index++)
            {
                Check();InventoryNode? end=index<ends.Length?ends[index]:null;var stories=new List<ReadableStory>();
                if(end is not null)
                foreach(var reference in children[end.Id].Where(n=>Is(n,"headerReference")||Is(n,"footerReference")))
                {
                    string kind=Is(reference,"headerReference")?"header":"footer",variant=Attr(reference,"type");
                    string rid=reference.Attributes.GetValueOrDefault("{"+R+"}id")??"";
                    var relations=input.Manifest.Relationships.Where(r=>r.SourcePart==main&&r.Id==rid&&r.Type==R+"/"+kind&&r.Mode=="Internal").ToArray();
                    inherited[(kind,variant)]=(relations.Length==1?relations[0].ResolvedPart??"":"",reference.Source);
                }
                foreach(var entry in inherited)
                {
                    var story=doc.Blocks.FirstOrDefault(b=>b.Source.PartUri==entry.Value.Part&&b.Source.PartUri!=main);
                    bool direct=end is not null&&children[end.Id].Any(n=>(Is(n,entry.Key.Kind=="header"?"headerReference":"footerReference"))&&Attr(n,"type")==entry.Key.Variant);
                    bool verified=story is not null;var source=story?.Source??entry.Value.Source;
                    stories.Add(new($"story-{index+1}-{entry.Key.Kind}-{entry.Key.Variant}",index+1,entry.Key.Kind,entry.Key.Variant,!direct,verified,source,story?.Children??[]));
                    used.Add(entry.Value.Part);
                    if(!verified)Issue("READ_STORY_UNRESOLVED",entry.Value.Source,"En-tête ou pied non rattaché ; référence conservée.");
                }
                string? closing=end is not null?body.FirstOrDefault(b=>Walk([b]).Any(c=>c.Id==end.Id))?.Id:null;
                result.Add(new(index+1,closing,stories));
            }
            var other=doc.Blocks.Where(b=>b.Source.PartUri!=main&&!used.Contains(b.Source.PartUri)).Select(b=>new ReadableStory("unassigned-"+b.Id,0,b.Label?.StartsWith("En-tête",StringComparison.Ordinal)==true?"header":"footer","unassigned",false,false,b.Source,b.Children)).ToArray();
            if(other.Length>0)result.Add(new(0,null,other));
            return result;
        }
    }
}
