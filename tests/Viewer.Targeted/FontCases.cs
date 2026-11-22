using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static partial class FontCases
{
    const string ThemeNs="http://schemas.openxmlformats.org/drawingml/2006/main";
    const string Missing="PoliceAbsente-FONTS001-2026";
    static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
    static string Sha(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static string Section=>"<w:sectPr><w:pgSz w:w=\"5000\" w:h=\"5000\"/><w:pgMar w:left=\"400\" w:right=\"400\" w:top=\"400\" w:bottom=\"400\"/></w:sectPr>";
    static string Run(string family,string text,string extra="")=>"<w:r><w:rPr><w:rFonts w:ascii=\""+family+"\" w:hAnsi=\""+family+"\"/>"+extra+"</w:rPr><w:t xml:space=\"preserve\">"+text+"</w:t></w:r>";
    static InspectionResult Inspect(string root,string body,string name="font",Dictionary<string,string>? extra=null,string rel="",Dictionary<string,byte[]>? binary=null)
    {
        string path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body+Section,extra,rel,binary));string before=Sha(path);var input=DocumentInspector.InspectDocument(path);
        Check(input.Manifest.InspectionComplete,"Inspection failed: "+string.Join(',',input.Manifest.Diagnostics.Where(i=>i.Severity=="error").Select(i=>i.Code)));Check(Sha(path)==before,"Original changed");return input;
    }
    public static string Fixture(string root)
    {
        string body="<w:p>"+Run("DejaVu Serif","Texte français en sérif. ")+Run("DejaVu Sans Mono","Code mono 012345. ","<w:b/>")+Run(Missing,"Police absente : substitution visible. ")+"</w:p>";
        body+=string.Concat(Enumerable.Range(0,12).Select(i=>"<w:p>"+Run(i%2==0?"Liberation Serif":"DejaVu Sans Mono","Paragraphe "+i+" : fidélité typographique expérimentale, accents conservés. ")+"</w:p>"));
        Inspect(root,body,"Polices-comparaison");return Path.Combine(root,"Polices-comparaison.docx");
    }
    public static void Model(string root)
    {
        string styles=Fixtures.Xml("styles","<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:asciiTheme=\"minorHAnsi\" w:hAnsiTheme=\"minorHAnsi\" w:eastAsia=\"Noto Sans CJK SC\" w:cs=\"Noto Naskh Arabic\"/><w:sz w:val=\"24\"/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:rPr><w:b/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"Inherited\"><w:basedOn w:val=\"Normal\"/><w:rPr><w:i/><w:rFonts w:ascii=\"DejaVu Serif\"/></w:rPr></w:style><w:style w:type=\"character\" w:styleId=\"Mono\"><w:rPr><w:rFonts w:ascii=\"DejaVu Sans Mono\"/><w:sz w:val=\"28\"/></w:rPr></w:style>");
        string theme="<a:theme xmlns:a=\""+ThemeNs+"\" name=\"Synthetic\"><a:themeElements><a:fontScheme name=\"Test\"><a:majorFont><a:latin typeface=\"DejaVu Serif\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Liberation Sans\"/><a:ea typeface=\"Noto Sans CJK SC\"/><a:cs typeface=\"Noto Naskh Arabic\"/><a:font script=\"Jpan\" typeface=\"Noto Sans CJK JP\"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>";
        string fonts=Fixtures.Xml("fonts","<w:font w:name=\"DejaVu Serif\"><w:altName w:val=\"Alias conservé\"/><w:family w:val=\"roman\"/><w:charset w:val=\"00\"/><w:pitch w:val=\"variable\"/></w:font>");
        string body="<w:p><w:r><w:t>Défaut é</w:t></w:r></w:p><w:p><w:pPr><w:pStyle w:val=\"Inherited\"/></w:pPr><w:r><w:t>Ascii é</w:t></w:r><w:r><w:rPr><w:rStyle w:val=\"Mono\"/><w:rFonts w:hAnsi=\"Liberation Serif\"/><w:bCs/><w:iCs/><w:szCs w:val=\"26\"/></w:rPr><w:t>Mono é</w:t></w:r></w:p>";
        var parts=Fixtures.Parts(body+Section,new(){["word/styles.xml"]=styles,["word/fontTable.xml"]=fonts},Fixtures.Relationship("s","styles","styles.xml")+Fixtures.Relationship("f","fontTable","fontTable.xml")+Fixtures.Relationship("t","theme","theme/theme1.xml"));
        parts["word/theme/theme1.xml"]=Encoding.UTF8.GetBytes(theme);
        string ct=Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>","<Override PartName=\"/word/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/></Types>");parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(ct);
        string path=Fixtures.Write(root,"styles-theme.docx",parts),before=Sha(path);var input=DocumentInspector.InspectDocument(path);var model=LayoutReader.Read(input);
        Check(model.Available,"Theme/style model refused: "+string.Join(',',model.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));
        Check(model.Fonts.ThemeFamily("minorAscii")=="Liberation Sans"&&model.Fonts.Theme.Any(t=>t.Script=="Jpan"),"Theme font scheme lost");
        Check(model.Paragraphs[0].Runs.All(r=>r.Style.Font!.UsedFamily=="Liberation Sans"&&r.Style.Bold),"Defaults not resolved");
        var inherited=model.Paragraphs[1].Runs.First();Check(inherited.Style.FontSlots.Ascii?.Source.PartUri=="/word/styles.xml"&&inherited.Style.RequestedFont=="DejaVu Serif"&&inherited.Style.Bold&&inherited.Style.Italic,"Inheritance or direct ASCII override failed");
        var mono=model.Paragraphs[1].Runs.Where(r=>r.Style.Size==14).ToArray();Check(mono.Any(r=>r.Style.RequestedFont=="DejaVu Sans Mono")&&mono.Any(r=>r.Style.RequestedFont=="Liberation Serif")&&mono.All(r=>r.Style.ComplexSize==13&&r.Style.ComplexBold==true&&r.Style.ComplexItalic==true),"Character style or hAnsi/Cs properties lost");
        Check(model.Fonts.Declarations.Single().Alternative=="Alias conservé"&&model.Fonts.Requests.Any(r=>r.Attribute=="cs")&&model.Fonts.Requests.Any(r=>r.Attribute=="eastAsia"),"Declarations or script slots lost");
        Check(Composer.Compose(model).Available&&Sha(path)==before,"Composition or conservation failed");File.WriteAllText(Path.Combine(root,"fonts-model.json"),JsonSerializer.Serialize(model,new JsonSerializerOptions{WriteIndented=true}));
        Check(Encoding.UTF8.GetString(ManifestJson.Serialize(input.Manifest)).Contains("Noto Naskh Arabic",StringComparison.Ordinal),"Font declaration lost from v1 contract");
        var priority=LayoutReader.Read(Inspect(root,"<w:p><w:r><w:rPr><w:rFonts w:ascii=\"DejaVu Serif\" w:asciiTheme=\"undefinedTheme\"/></w:rPr><w:t>ABC</w:t></w:r></w:p>","theme-missing"));
        Check(priority.Fonts.Uses.Single().Resolution?.Status=="substituted"&&priority.Fonts.Uses.Single().Request?.Theme==true,"Unresolved theme silently replaced by direct font");
        var independent=LayoutReader.Read(Inspect(root,"<w:p><w:r><w:rPr><w:rFonts w:hAnsi=\"DejaVu Sans Mono\"/></w:rPr><w:t>Aé</w:t></w:r></w:p>","independent-font-slots"));
        Check(independent.Paragraphs[0].Runs[0].Style.ResolvedFont=="DejaVu Serif"&&independent.Paragraphs[0].Runs[1].Style.ResolvedFont=="DejaVu Sans Mono"&&independent.Issues.Any(i=>i.Code=="LAYOUT_DEFAULT_FONT_SLOT"),"One font slot silently leaked into another");
    }
    public static void Resolution(string root)
    {
        var input=DocumentInspector.InspectDocument(Fixture(root));var model=LayoutReader.Read(input);var pages=Composer.Compose(model);Check(pages.Available,"Mixed font page refused");
        var fonts=model.Fonts.Uses.Select(u=>u.Resolution!).DistinctBy(r=>(r.RequestedFamily,r.Weight,r.Slant)).ToArray();
        Check(fonts.Any(r=>r.RequestedFamily=="DejaVu Sans Mono"&&r.UsedFamily=="DejaVu Sans Mono"&&r.Weight>=600&&r.Status=="installed"),"Actual installed bold face not used");
        Check(fonts.Any(r=>r.RequestedFamily==Missing&&!r.Installed&&r.Status=="substituted"),"Missing face not diagnosed");
        foreach(var style in model.Paragraphs.SelectMany(p=>p.Runs).Select(r=>r.Style))
        {var loaded=FontResolver.Probe(style);Check(loaded.UsedFamily==style.Font!.UsedFamily&&loaded.Weight==style.Font.Weight&&loaded.Sha256.Length==64,"Loaded face identity mismatch");}
        foreach(var p in model.Paragraphs)Check(string.Concat(pages.Pages.SelectMany(p=>p.Lines).Where(l=>l.ParagraphId==p.Id).Select(l=>l.Text+l.SourceBreak))==p.Text,"Mixed font text lost");
        foreach(var line in pages.Pages.SelectMany(p=>p.Lines))for(int i=1;i<line.Fragments.Count;i++)
        {
            var previous=line.Fragments[i-1];using var face=SKTypeface.FromFamilyName(previous.Style.ResolvedFont,new SKFontStyle(previous.Style.Font!.Weight,5,Enum.Parse<SKFontStyleSlant>(previous.Style.Font.Slant)));using var font=new SKFont(face,previous.Style.Size);
            Check(Math.Abs(line.Fragments[i].X-previous.X-font.MeasureText(previous.Text))<.02,"Composition does not use loaded native font metrics");
        }
        using var fallbackFace=SKTypeface.FromFamilyName("DejaVu Serif");using var fallback=new SKFont(fallbackFace,12);
        string? glyph=null;string coverageFamily="";
        foreach(string family in new[]{"Standard Symbols PS","MathJax_Main","Latin Modern Math","Liberation Serif"})
        {
            if(!FontResolver.IsInstalled(family))continue;using var primaryFace=SKTypeface.FromFamilyName(family);using var primary=new SKFont(primaryFace,12);
            glyph=Enumerable.Range(0x100,0x150).Select(char.ConvertFromUtf32).FirstOrDefault(c=>!primary.ContainsGlyphs(c)&&fallback.ContainsGlyphs(c));if(glyph is not null){coverageFamily=family;break;}
        }
        Check(glyph is not null,"Coverage witness not found");
        var coverage=LayoutReader.Read(Inspect(root,"<w:p>"+Run(coverageFamily,glyph!)+"</w:p>","glyph-fallback"));Check(coverage.Available&&coverage.Fonts.Uses.Any(u=>u.Resolution?.Status=="substituted"),"Unicode coverage fallback missing");
        var refused=Composer.Compose(Inspect(root,"<w:p>"+Run("DejaVu Sans","العربية 😀")+"</w:p>","complex-script"));Check(!refused.Available&&ReadableReader.Read(Inspect(root,Fixtures.Paragraph("العربية 😀"),"unicode-reading")).Available,"Complex-script refusal bypassed");
        var budget=Inspect(root,"<w:p>"+string.Concat(Enumerable.Range(0,70).Select(i=>Run("Missing-budget-"+i,"A")))+"</w:p>","font-budget");
        var limited=Composer.Compose(budget);Check(!limited.Available&&limited.Issues.Any(i=>i.Code=="LAYOUT_FONT_FACE_LIMIT")&&ReadableReader.Read(budget).Available,"Font face budget bypass or simplified reading lost");
        File.WriteAllText(Path.Combine(root,"font-resolution.json"),JsonSerializer.Serialize(model.Fonts,new JsonSerializerOptions{WriteIndented=true}));File.WriteAllBytes(Path.Combine(root,"fonts-page.png"),Composer.PagePng(pages.Pages[0]));
        Console.WriteLine("Observed: "+string.Join("; ",fonts.Select(f=>$"{f.RequestedFamily} -> {f.UsedFamily} [{f.Weight}/{f.Slant}, {f.Status}]"))+"; Unicode fallback U+"+char.ConvertToUtf32(glyph!,0).ToString("X4"));
    }
    public static void Pagination(string root)
    {
        var stats=new List<object>();var counts=new List<int>();
        foreach(string family in new[]{"DejaVu Serif","DejaVu Sans Mono"})
        {
            string text=new('i',2600);var input=Inspect(root,"<w:p>"+Run(family,text)+"</w:p>",family.Replace(' ','-'));
            long allocated=GC.GetTotalAllocatedBytes();var watch=System.Diagnostics.Stopwatch.StartNew();var model=LayoutReader.Read(input);long read=watch.ElapsedMilliseconds;watch.Restart();var result=Composer.Compose(model);long compose=watch.ElapsedMilliseconds;
            Check(result.Available,"Pagination font witness refused");counts.Add(result.Pages.Count);Check(string.Concat(result.Pages.SelectMany(p=>p.Lines).Select(l=>l.Text))==text,"Text lost on font-dependent pagination");
            stats.Add(new{family,pages=result.Pages.Count,lines=result.Pages.Sum(p=>p.Lines.Count),readMs=read,composeMs=compose,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,workingSet=Environment.WorkingSet,faces=model.Fonts.Uses.Select(u=>u.Resolution).Distinct()});
        }
        foreach(string body in new[]{"<w:p>"+Run("DejaVu Sans","العربية 中文 😀")+"</w:p>","<w:p>"+Run("DejaVu Serif","ABC","<w:cs/>")+"</w:p>","<w:p><w:r><w:rPr><w:rFonts w:ascii=\"DejaVu Serif\" w:hint=\"eastAsia\"/></w:rPr><w:t>ABC</w:t></w:r></w:p>"})
        {
            var input=Inspect(root,body,"unqualified-"+Guid.NewGuid().ToString("N"));Check(!Composer.Compose(input).Available&&ReadableReader.Read(input).Available,"Unqualified script/property lost explicit refusal");
        }
        Check(counts[0]!=counts[1],"Font metrics do not affect page count");File.WriteAllText(Path.Combine(root,"font-measures.json"),JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Font-dependent pages: "+string.Join(" / ",counts));
    }
    static byte[] TestFont(int flags,byte[]? cmap=null)
    {
        byte[] bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"../../../../../src/DocxFlow/Fonts/DejaVuSerif.ttf"));
        int count=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));int head=0;
        for(int i=0;i<count;i++)
        {
            int p=12+16*i;string tag=Encoding.ASCII.GetString(bytes,p,4);int off=(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+8));
            if(tag=="OS/2")BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(off+8),(ushort)flags);
            if(tag=="cmap"&&cmap is not null)
            {
                int capacity=(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+12));
                Check(cmap.Length<=capacity,"La table synthétique dépasse son emplacement.");
                bytes.AsSpan(off,capacity).Clear();
                cmap.CopyTo(bytes,off);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(p+12),(uint)cmap.Length);
            }
            if(tag=="head")head=off;
            if(tag=="name")
            {
                int names=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(off+2)),storage=off+BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(off+4));
                for(int j=0;j<names;j++)
                {
                    int entry=off+6+12*j,id=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry+6));if(id is not (1 or 4 or 6 or 16))continue;
                    int length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry+8)),start=storage+BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry+10)),platform=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(entry));
                    byte[] name=platform is 0 or 3?Encoding.BigEndianUnicode.GetBytes("LW TestFace"):Encoding.ASCII.GetBytes("LW TestFace");if(name.Length>length)continue;
                    Array.Clear(bytes,start,length);name.CopyTo(bytes,start);BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entry+8),(ushort)name.Length);
                }
            }
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(head+8),0);
        uint Sum(int off,int length){uint sum=0;for(int i=0;i<length;i+=4){uint value=0;for(int j=0;j<4;j++)value=(value<<8)|((i+j<length)?bytes[off+i+j]:0u);sum=unchecked(sum+value);}return sum;}
        for(int i=0;i<count;i++){int p=12+16*i;int off=(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+8)),length=(int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+12));BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(p+4),Sum(off,length));}
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(head+8),unchecked(0xB1B0AFBA-Sum(0,bytes.Length)));return bytes;
    }
    public static void Embedded(string root)
    {
        var stats=new List<object>();
        foreach(int flags in new[]{0,4,8,2,0x200,0xc,0x100})
        {
            byte[] font=TestFont(flags);string key="001B70DC-AA60-4AD5-90EC-18A0948E1EAE";byte[] mask=Convert.FromHexString(key.Replace("-",""));Array.Reverse(mask);for(int i=0;i<32;i++)font[i]^=mask[i%16];
            string table=Fixtures.Xml("fonts","<w:font w:name=\"LW TestFace\"><w:embedRegular r:id=\"font\" w:fontKey=\"{"+key+"}\" w:subsetted=\"1\"/></w:font>");
            var parts=Fixtures.Parts("<w:p>"+Run("LW TestFace","Police incorporée vérifiée.")+"</w:p>"+Section,new(){["word/fontTable.xml"]=table},Fixtures.Relationship("ft","fontTable","fontTable.xml"),new(){["word/fonts/test.odttf"]=font});
            parts["word/_rels/fontTable.xml.rels"]=Encoding.UTF8.GetBytes(Fixtures.Relations(Fixtures.Relationship("font","font","fonts/test.odttf")));
            parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>","<Default Extension=\"odttf\" ContentType=\"application/vnd.openxmlformats-officedocument.obfuscatedFont\"/></Types>"));
            string path=Fixtures.Write(root,"embedded-"+flags+".docx",parts),before=Sha(path);var input=DocumentInspector.InspectDocument(path);Check(input.Manifest.InspectionComplete,"Embedded package rejected by inspector");
            var model=LayoutReader.Read(input);var e=model.Fonts.Embedded.Single();bool allowed=flags is 0 or 4 or 8;
            Check(e.Copyright.Length>0&&e.LicenseNotice.Length>0,"Font licence notices lost");
            Check((e.Status=="usable")==allowed,"Incorrect embedding rights decision "+flags+": "+e.Detail);
            Check(model.Fonts.Uses.Any(u=>u.Resolution?.Status==(allowed?"embedded":"substituted")),"Embedded font not actually used or refused");
            var pages=Composer.Compose(model);Check(pages.Available&&Sha(path)==before,"Embedded composition or original preservation failed");
            if(allowed)File.WriteAllBytes(Path.Combine(root,"embedded-"+flags+".png"),Composer.PagePng(pages.Pages[0]));
            if(allowed)foreach(var style in model.Paragraphs.SelectMany(p=>p.Runs).Select(r=>r.Style))Check(FontResolver.Probe(style).UsedFamily=="LW TestFace","Native embedded face not loaded");
            string report=JsonSerializer.Serialize(model.Fonts);Check(!report.Contains(Convert.ToBase64String(font),StringComparison.Ordinal),"Font bytes leaked into model export");
            Check(input.SafeMedia.Count==0&&input.Manifest.Parts.Any(p=>p.ContentType.Contains("obfuscatedFont",StringComparison.Ordinal)),"Font misclassified as safe image");
            stats.Add(new{flags,e.Status,e.Rights,e.Detail});
            if(flags==0)
            {
                string output=Path.Combine(root,"contract-no-font-assets");AtomicOutput.Write(input,output);
                Check(Directory.GetFiles(output,"*",SearchOption.AllDirectories).Select(Path.GetFileName).SequenceEqual(new[]{"manifest.json"}),"Font redistributed by v1 transport");
                var malformed=TestFont(0);BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(20),uint.MaxValue);
                for(int j=0;j<32;j++)malformed[j]^=mask[j%16];
                var malformedManifest=input.Manifest with{Parts=input.Manifest.Parts.Select(p=>p.PartUri=="/word/fonts/test.odttf"?p with{Sha256=Convert.ToHexStringLower(SHA256.HashData(malformed))}:p).ToList()};
                Check(DocumentFonts.Read(new(malformedManifest,input.SafeMedia){FontParts=new Dictionary<string,byte[]>{{"/word/fonts/test.odttf",malformed}}}).Embedded.Single().Status=="refused","Out-of-bounds SFNT table accepted");
                var plainParts=parts.ToDictionary(p=>p.Key,p=>p.Value);plainParts["word/fonts/test.odttf"]=TestFont(0);
                plainParts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("application/vnd.openxmlformats-officedocument.obfuscatedFont","application/x-font-ttf",StringComparison.Ordinal));
                string plainPath=Fixtures.Write(root,"embedded-plain.docx",plainParts);var plainInput=DocumentInspector.InspectDocument(plainPath);var plainModel=LayoutReader.Read(plainInput);
                Check(plainInput.Manifest.InspectionComplete&&plainModel.Fonts.Embedded.Single().Status=="usable"&&plainModel.Fonts.Uses.All(u=>u.Resolution?.Status=="embedded")&&Composer.Compose(plainModel).Available,"Plain TTF path not usable");
                var priorityParts=plainParts.ToDictionary(p=>p.Key,p=>p.Value);priorityParts["word/fonts/test.odttf"]=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"../../../../../src/DocxFlow/Fonts/DejaVuSerif.ttf"));
                foreach(string xml in new[]{"word/document.xml","word/fontTable.xml"})priorityParts[xml]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(priorityParts[xml]).Replace("LW TestFace","DejaVu Serif",StringComparison.Ordinal));
                var priority=LayoutReader.Read(DocumentInspector.InspectDocument(Fixtures.Write(root,"embedded-installed-priority.docx",priorityParts)));
                Check(priority.Fonts.Embedded.Single().Status=="usable"&&priority.Fonts.Uses.All(u=>u.Resolution?.Status=="installed"),"Installed requested face did not take priority");
                var altered=input with{FontParts=new Dictionary<string,byte[]>{{"/word/fonts/test.odttf",new byte[40]}}};Check(DocumentFonts.Read(altered).Embedded.Single().Status=="refused","Corrupt payload accepted");
                var bad=font.ToArray();bad[0]^=255;var badManifest=input.Manifest with{Parts=input.Manifest.Parts.Select(p=>p.PartUri=="/word/fonts/test.odttf"?p with{Sha256=Convert.ToHexStringLower(SHA256.HashData(bad))}:p).ToList()};
                Check(DocumentFonts.Read(new(badManifest,input.SafeMedia){FontParts=new Dictionary<string,byte[]>{{"/word/fonts/test.odttf",bad}}}).Embedded.Single().Status=="refused","Invalid SFNT signature accepted");
            }
        }
        File.WriteAllText(Path.Combine(root,"embedded-fonts.json"),JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();var window=new ReaderWindow();window.Show();AboutCases.Complete(window.OpenDocumentAsync(Fixture(root)));
        VerifyProperties(window);window.Close();
    }
    public static void Real(string root)
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit controlled copy required");
        string before=Sha(path);var timer=System.Diagnostics.Stopwatch.StartNew();var input=DocumentInspector.InspectDocument(path);var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        int declared=input.Manifest.Nodes.Count(n=>n.Source.PartUri=="/word/fontTable.xml"&&n.Name=="{"+Fixtures.W+"}font");
        Check(input.Manifest.InspectionComplete&&model.Fonts.Declarations.Count==declared&&model.Fonts.Requests.Count>0,"Real font declarations not analysed");
        Check(!pages.Available&&ReadableReader.Read(input).Available&&Sha(path)==before,"Real document incorrectly paginated, unreadable or changed");
        File.WriteAllText(Path.Combine(root,"real-fonts.json"),JsonSerializer.Serialize(model.Fonts,new JsonSerializerOptions{WriteIndented=true}));
        var stats=new{elapsedMs=timer.ElapsedMilliseconds,workingSet=Environment.WorkingSet,declared=model.Fonts.Declarations.Count,theme=model.Fonts.Theme.Count,requests=model.Fonts.Requests.Count,uses=model.Fonts.Uses.Count,sourceSha256=before,pages=pages.Pages.Count};
        File.WriteAllText(Path.Combine(root,"real-font-measures.json"),JsonSerializer.Serialize(stats,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(stats));
    }
    static void VerifyProperties(ReaderWindow window)
    {
        Check(window.CurrentComposition?.Available==true,"No paginated font fixture");
        Check(!window.ExportAvailable(ExportFormat.Pdf)&&!window.ExportAvailable(ExportFormat.Odt)&&!window.ExportAvailable(ExportFormat.Epub3),"Font composition incorrectly extended export qualification");
        foreach(bool en in new[]{false,true})
        {
            window.SetTheme(en?2:1);window.SetLanguage(en);window.Width=860;window.Height=620;Dispatcher.UIThread.RunJobs();
            var properties=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content as string==(en?"Properties":"Propriétés"));properties.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
            var section=window.GetVisualDescendants().OfType<StackPanel>().Single(p=>p.Name=="DocumentFonts");Check(ReadingUxCases.VisibleText(section).Contains(en?"Document fonts":"Polices du document",StringComparison.Ordinal),"Font property section missing");
            foreach(var expander in section.Children.OfType<Expander>())expander.IsExpanded=true;Dispatcher.UIThread.RunJobs();Check(ReadingUxCases.VisibleText(section).Contains(Missing,StringComparison.Ordinal),"Missing font not visible");
            using var report=JsonDocument.Parse(window.DetailedDiagnosticReport());Check(report.RootElement.GetProperty("fonts").GetProperty("uses").GetArrayLength()>0,"Fonts absent from technical report");
            Check(!Encoding.UTF8.GetString(window.DetailedDiagnosticReport()).Contains("\"bytes\":\"",StringComparison.Ordinal),"Font payload exposed in report");
            var mode=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ReadingMode");mode.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Check(window.IsSimplifiedReading,"Simplified mode lost");mode.SelectedIndex=0;Dispatcher.UIThread.RunJobs();Check(window.CurrentPage>0,"Paginated mode lost");
        }
    }
    public static async Task Native(ReaderWindow window,string root)
    {
        string path=Fixture(root),before=Sha(path);await window.OpenDocumentAsync(path);await Task.Delay(200);VerifyProperties(window);
        var handle=window.TryGetPlatformHandle();Check(handle?.HandleDescriptor=="XID","Native font test needs X11");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.SetLanguage(false);window.FitToWidth();await Task.Delay(250);
            var section=window.GetVisualDescendants().OfType<StackPanel>().Single(p=>p.Name=="DocumentFonts");
            foreach(var expander in section.Children.OfType<Expander>())expander.IsExpanded=false;
            section.Children.OfType<TextBlock>().First().BringIntoView();await Task.Delay(150);
            var capture=new System.Diagnostics.ProcessStartInfo("import"){UseShellExecute=false};foreach(string arg in new[]{"-window",handle!.Handle.ToInt64().ToString(),Path.Combine(root,"fonts-native-"+theme+".png")})capture.ArgumentList.Add(arg);using var p=System.Diagnostics.Process.Start(capture)!;await p.WaitForExitAsync();Check(p.ExitCode==0,"Native capture failed");
        }
        Check(Sha(path)==before,"Native changed source");File.WriteAllBytes(Path.Combine(root,"fonts-native-report.json"),window.DetailedDiagnosticReport());File.WriteAllText(Path.Combine(root,"fonts-native-measures.json"),JsonSerializer.Serialize(new{window.RenderScaling,pages=window.CurrentComposition!.Pages.Count,workingSet=Environment.WorkingSet,sourceSha256=before},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Native fonts OK | scale="+window.RenderScaling+" | pages="+window.CurrentComposition!.Pages.Count);
    }
}
