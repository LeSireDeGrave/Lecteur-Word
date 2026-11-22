using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using LecteurWord;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class StructureCases
{
    static void Check(bool yes,string message){if(!yes)throw new InvalidOperationException(message);}
    static string Sha(string p)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));
    static string Section(int width=8000,int height=6000)=>$"<w:sectPr><w:pgSz w:w=\"{width}\" w:h=\"{height}\"/><w:pgMar w:left=\"500\" w:right=\"500\" w:top=\"500\" w:bottom=\"500\"/></w:sectPr>";
    static InspectionResult Inspect(string root,string body,string name="structure",Dictionary<string,string>? extra=null,string rel="",Dictionary<string,byte[]>? binary=null)
    {
        string path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body,extra,rel,binary)),before=Sha(path);var input=DocumentInspector.InspectDocument(path);Check(Sha(path)==before,"Fixture modified");Check(input.Manifest.InspectionComplete,"Inventory refused: "+string.Join(',',input.Manifest.Diagnostics.Where(d=>d.Severity=="error").Select(d=>d.Code)));return input;
    }
    static Composition Compose(InspectionResult input,out PaginatedDocument model)
    {
        model=LayoutReader.Read(input);var composition=Composer.Compose(model);Check(composition.Available,"Composition refused: "+string.Join(" | ",composition.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code+":"+i.Message)));Integrity(model,composition);return composition;
    }
    static void Integrity(PaginatedDocument model,Composition composition)
    {
        foreach(var p in model.Paragraphs.Where(p=>p.TableId is null))
        {
            string text=string.Concat(composition.Pages.SelectMany(pg=>pg.Lines).Where(l=>l.ParagraphId==p.Id&&!l.IsRepeated).Select(l=>l.Text+l.SourceBreak));Check(text==p.Text,"Source text lost or reordered: "+p.Id);
        }
        foreach(var page in composition.Pages)
        {
            Check(page.Cells.All(c=>c.X>=page.Geometry.Left-.1&&c.X+c.Width<=page.Geometry.Width-page.Geometry.Right+.1&&c.Y>=page.Geometry.Top-.1&&c.Y+c.Height<=page.Geometry.Height-page.Geometry.Bottom+.1),"Table outside page body");
            Check(page.Lines.SelectMany(l=>l.Images).All(i=>i.X>=0&&i.Y>=0&&i.X+i.Image.Width<=page.Geometry.Width+.01&&i.Y+i.Image.Height<=page.Geometry.Height+.01),"Image outside page");
        }
    }
    static string Numbered(int level,string text,string id="7")=>"<w:p><w:pPr><w:numPr><w:ilvl w:val=\""+level+"\"/><w:numId w:val=\""+id+"\"/></w:numPr></w:pPr><w:r><w:t>"+text+"</w:t></w:r></w:p>";
    static string Numbering=>Fixtures.Xml("numbering","<w:abstractNum w:abstractNumId=\"3\"><w:multiLevelType w:val=\"multilevel\"/>"+Level(0,"decimal","%1.",3)+Level(1,"upperLetter","%1.%2)")+"</w:abstractNum><w:num w:numId=\"7\"><w:abstractNumId w:val=\"3\"/></w:num><w:num w:numId=\"8\"><w:abstractNumId w:val=\"3\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"10\"/></w:lvlOverride></w:num>");
    static string Level(int level,string format,string label,int start=1)=>$"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"{start}\"/><w:numFmt w:val=\"{format}\"/><w:lvlText w:val=\"{label}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{960+level*360}\" w:hanging=\"720\"/></w:pPr></w:lvl>";
    public static void Lists(string root)
    {
        string body=Numbered(0,"Premier")+Numbered(1,"Sous A")+Numbered(1,"Sous B")+Numbered(0,"Deuxième")+Numbered(1,"Reprise A")+Numbered(0,"Dix","8")+Section();
        var input=Inspect(root,body,"numbered",new(){["word/numbering.xml"]=Numbering},Fixtures.Relationship("n","numbering","numbering.xml"));var pages=Compose(input,out var model);
        Check(model.Paragraphs.Select(p=>p.List!.Text).SequenceEqual(new[]{"3.","3.A)","3.B)","4.","4.A)","10."}),"Multilevel counters/restart/instance override incorrect");
        Check(pages.Pages.SelectMany(p=>p.Lines).SelectMany(l=>l.Fragments).Count(f=>f.Generated)==6,"List labels missing or counted as source text");
        foreach(var format in new[]{"bullet","lowerRoman","lowerLetter"})
        {
            string definition=Fixtures.Xml("numbering","<w:abstractNum w:abstractNumId=\"3\">"+Level(0,format,format=="bullet"?"•":"%1)")+"</w:abstractNum><w:num w:numId=\"7\"><w:abstractNumId w:val=\"3\"/></w:num>");
            var result=Compose(Inspect(root,Numbered(0,"Item")+Section(),format,new(){["word/numbering.xml"]=definition},Fixtures.Relationship("n","numbering","numbering.xml")),out var numbered);Check(numbered.Paragraphs.Single().List!.Text==(format=="bullet"?"•":format=="lowerRoman"?"i)":"a)"),"List format incorrect");
        }
        string inheritedStyle=Fixtures.Xml("styles","<w:style w:type=\"paragraph\" w:styleId=\"ListStyle\"><w:pPr><w:numPr><w:numId w:val=\"7\"/><w:ilvl w:val=\"1\"/></w:numPr></w:pPr><w:rPr><w:rFonts w:ascii=\"DejaVu Sans\" w:hAnsi=\"DejaVu Sans\"/></w:rPr></w:style>");
        Compose(Inspect(root,"<w:p><w:pPr><w:pStyle w:val=\"ListStyle\"/></w:pPr><w:r><w:t>Style hérité</w:t></w:r></w:p>"+Section(),"list-inherited",new(){["word/numbering.xml"]=Numbering,["word/styles.xml"]=inheritedStyle},Fixtures.Relationship("n","numbering","numbering.xml")+Fixtures.Relationship("s","styles","styles.xml")),out var inherited);
        Check(inherited.Paragraphs.Single().List?.Text=="3.A)"&&inherited.Paragraphs.Single().Runs.All(r=>r.Style.ResolvedFont=="DejaVu Sans"),"List inheritance changed character styles or numbering");
        File.WriteAllBytes(Path.Combine(root,"lists.png"),Composer.PagePng(pages.Pages[0]));File.WriteAllText(Path.Combine(root,"lists.json"),JsonSerializer.Serialize(new{model,pages},new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Tabs(string root)
    {
        string P(string align,string text)=>"<w:p><w:pPr><w:tabs><w:tab w:val=\""+align+"\" w:pos=\"3600\"/></w:tabs></w:pPr><w:r><w:t>Nom</w:t><w:tab/><w:t>"+text+"</w:t></w:r></w:p>";
        var pages=Compose(Inspect(root,P("left","Suite")+P("right","1234")+P("center","Centre")+P("decimal","12,50")+"<w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t></w:r></w:p>"+Section(),"tabs"),out var model);
        var lines=pages.Pages.SelectMany(p=>p.Lines).ToArray();Check(lines.All(l=>l.Tabs.Count==1)&&lines.All(l=>l.Text.Contains('\t')),"Tabs lost or changed into spaces");
        Check(Math.Abs(lines[0].Tabs[0].To-205)<.02,"Left tab position wrong");
        var right=lines[1].Fragments.Last();using var face=SKTypeface.FromFamilyName(right.Style.ResolvedFont);using var font=new SKFont(face,right.Style.Size);Check(Math.Abs(right.X+font.MeasureText(right.Text)-205)<.02,"Right tab not aligned by real metrics");
        var center=lines[2].Fragments.Last();Check(Math.Abs(center.X+font.MeasureText(center.Text)/2-205)<.02,"Center tab incorrect");
        var dec=lines[3].Fragments.Last();Check(Math.Abs(dec.X+font.MeasureText("12")-205)<.02,"Decimal tab incorrect");
        Check(Math.Abs(lines[4].Tabs[0].To-61)<.02,"Default 36-point tab wrong");
        var only=Compose(Inspect(root,"<w:p><w:r><w:tab/></w:r></w:p>"+Fixtures.Paragraph("Après la ligne tabulée")+Section(),"tab-only"),out _);Check(only.Pages[0].Lines[0].Height>10&&only.Pages[0].Lines[1].Baseline>only.Pages[0].Lines[0].Baseline+10,"Tab-only paragraph silently collapsed");File.WriteAllBytes(Path.Combine(root,"tabs.png"),Composer.PagePng(pages.Pages[0]));
    }
    static string TableStart(string properties="",string grid="<w:gridCol w:w=\"2400\"/><w:gridCol w:w=\"4600\"/>")=>"<w:tbl><w:tblPr><w:tblLayout w:type=\"fixed\"/>"+(properties.Contains("<w:tblW",StringComparison.Ordinal)?"":"<w:tblW w:type=\"dxa\" w:w=\"7000\"/>")+"<w:tblCellMar><w:top w:type=\"dxa\" w:w=\"60\"/><w:bottom w:type=\"dxa\" w:w=\"60\"/><w:left w:type=\"dxa\" w:w=\"80\"/><w:right w:type=\"dxa\" w:w=\"80\"/></w:tblCellMar><w:tblBorders>"+string.Concat(new[]{"top","left","bottom","right","insideH","insideV"}.Select(e=>"<w:"+e+" w:val=\"single\" w:sz=\"4\" w:color=\"607080\"/>"))+"</w:tblBorders>"+properties+"</w:tblPr><w:tblGrid>"+grid+"</w:tblGrid>";
    static string Cell(string text,string properties="")=>"<w:tc><w:tcPr>"+properties+"</w:tcPr>"+Fixtures.Paragraph(text)+"</w:tc>";
    static string Row(string first,string second,bool header=false,string rowProperties="")=>"<w:tr><w:trPr>"+(header?"<w:tblHeader/>":"")+rowProperties+"</w:trPr>"+Cell(first)+Cell(second)+"</w:tr>";
    public static void Tables(string root)
    {
        string body=Fixtures.Paragraph("Avant le tableau")+TableStart()+Row("Titre A","Titre B",true)+Row("Retour automatique dans une cellule étroite, sans perte du texte.","Autre cellule")+"<w:tr><w:trPr><w:trHeight w:val=\"1000\" w:hRule=\"exact\"/></w:trPr>"+Cell("Bas","<w:vAlign w:val=\"bottom\"/>")+Cell("Centre","<w:vAlign w:val=\"center\"/><w:shd w:fill=\"DCE6F1\" w:val=\"clear\"/>")+"</w:tr></w:tbl>"+Fixtures.Paragraph("Après le tableau")+Section();
        var pages=Compose(Inspect(root,body,"table-basic"),out var model);var boxes=pages.Pages.SelectMany(p=>p.Cells).Where(c=>!c.IsRepeated).ToArray();Check(boxes.Length==6,"Cell boxes missing");Check(boxes.Any(c=>Math.Abs(c.Width-120)<.01)&&boxes.Any(c=>Math.Abs(c.Width-230)<.01),"Grid widths wrong");
        var table=model.Tables.Single();var longCell=table.Cells.First(c=>model.Paragraphs.Any(p=>c.ParagraphIds.Contains(p.Id)&&p.Text.StartsWith("Retour",StringComparison.Ordinal)));Check(pages.Pages.SelectMany(p=>p.Lines).Count(l=>l.CellId==longCell.Id)>1,"Cell text did not wrap");
        var lines=pages.Pages.SelectMany(p=>p.Lines).Where(l=>!l.IsRepeated).ToArray();Check(lines[0].Text=="Avant le tableau"&&lines[^1].Text=="Après le tableau","Block order changed");
        Check(boxes.Any(c=>c.Fill=="DCE6F1")&&boxes.All(c=>c.Borders.Top is {Width:>0}),"Shading or borders lost");var bottom=lines.Single(l=>l.Text=="Bas");var center=lines.Single(l=>l.Text=="Centre");Check(bottom.Baseline>center.Baseline+10,"Vertical cell alignment ignored");
        var percent=Compose(Inspect(root,TableStart("<w:tblW w:type=\"pct\" w:w=\"2500\"/><w:jc w:val=\"center\"/>")+Row("A","B")+"</w:tbl>"+Section(),"table-percent"),out _);var pct=percent.Pages[0].Cells[0];Check(Math.Abs(pct.X-112.5f)<.02&&Math.Abs(pct.Width-60)<.02,"Percentage table width or alignment incorrect");
        File.WriteAllBytes(Path.Combine(root,"table-basic.png"),Composer.PagePng(pages.Pages[0]));File.WriteAllText(Path.Combine(root,"table-basic.json"),JsonSerializer.Serialize(new{model,pages},new JsonSerializerOptions{WriteIndented=true}));
    }
    public static void Merges(string root)
    {
        string body=TableStart()+"<w:tr>"+Cell("Fusion horizontale","<w:gridSpan w:val=\"2\"/>")+"</w:tr><w:tr>"+Cell("Fusion verticale","<w:vMerge w:val=\"restart\"/>")+Cell("Droite 1")+"</w:tr><w:tr>"+Cell("","<w:vMerge/>")+Cell("Droite 2")+"</w:tr></w:tbl>"+Section();
        var pages=Compose(Inspect(root,body,"merges"),out var model);var boxes=pages.Pages.SelectMany(p=>p.Cells).ToArray();Check(boxes.Length==4&&boxes.Any(c=>Math.Abs(c.Width-350)<.01)&&boxes.Any(c=>c.MergedSourceIds.Count==1),"Merged cell geometry/provenance missing");
        var vertical=model.Tables[0].Cells.Single(c=>c.RowSpan==2);var box=boxes.Single(b=>b.CellId==vertical.Id);Check(box.Height>30,"Vertical merge did not span rows");File.WriteAllBytes(Path.Combine(root,"merges.png"),Composer.PagePng(pages.Pages[0]));
    }
    public static void TablePages(string root)
    {
        string body=TableStart()+Row("EN-TÊTE","Répété",true)+string.Concat(Enumerable.Range(0,22).Select(i=>Row("Ligne "+i,"Contenu "+i)))+"</w:tbl>"+Section();
        var pages=Compose(Inspect(root,body,"table-pages"),out var model);Check(pages.Pages.Count>=3,"Table did not span calculated pages");var header=model.Tables[0].Cells.First();Check(pages.Pages.All(p=>p.Cells.Any(c=>c.CellId==header.Id)),"Header missing on a table page");Check(pages.Pages.Skip(1).All(p=>p.Cells.Where(c=>c.CellId==header.Id).All(c=>c.IsRepeated)),"Repeated header not traced");
        string before="<w:p><w:pPr><w:spacing w:line=\"400\" w:lineRule=\"exact\"/></w:pPr><w:r>"+string.Join("<w:br/>",Enumerable.Repeat("<w:t>Occupation</w:t>",10))+"</w:r></w:p>";
        string heading="<w:p><w:pPr><w:keepNext/></w:pPr><w:r><w:t>Titre solidaire</w:t></w:r></w:p>";
        var together=Compose(Inspect(root,before+heading+TableStart()+Row("Header","Column",true)+Row("Data","Value")+"</w:tbl>"+Section(),"heading-table"),out _);
        int headingPage=together.Pages.ToList().FindIndex(p=>p.Lines.Any(l=>l.Text=="Titre solidaire"));Check(headingPage==together.Pages.ToList().FindIndex(p=>p.Cells.Count>0),"Heading separated from following table");
        foreach(var page in pages.Pages)File.WriteAllBytes(Path.Combine(root,"table-page-"+page.Number+".png"),Composer.PagePng(page));Console.WriteLine("Table calculated pages="+pages.Pages.Count);
    }
    static byte[] Png(int seed=0)
    {
        using var bitmap=new SKBitmap(40,20);using var canvas=new SKCanvas(bitmap);canvas.Clear(new SKColor((byte)(20+seed),100,200));using var paint=new SKPaint{Color=SKColors.Gold};canvas.DrawRect(0,0,12,20,paint);using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();
    }
    static string Drawing(string id="pic",int width=762000,int height=381000,bool floating=false)=>"<w:drawing><wp:"+(floating?"anchor":"inline")+" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><wp:extent cx=\""+width+"\" cy=\""+height+"\"/><wp:docPr id=\"1\" name=\"Image témoin\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"image.png\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\""+id+"\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\""+width+"\" cy=\""+height+"\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:"+(floating?"anchor":"inline")+"></w:drawing>";
    static string ImageParagraph(string drawing)=>"<w:p><w:r><w:t xml:space=\"preserve\">Avant </w:t>"+drawing+"<w:t xml:space=\"preserve\"> après.</w:t></w:r></w:p>";
    public static void Images(string root)
    {
        var input=Inspect(root,ImageParagraph(Drawing())+Section(),"inline",rel:Fixtures.Relationship("pic","image","media/picture.png"),binary:new(){["word/media/picture.png"]=Png()});var pages=Compose(input,out var model);var line=pages.Pages[0].Lines.Single();var picture=line.Images.Single();Check(picture.Image.Width==60&&picture.Image.Height==30,"EMU dimensions changed");Check(picture.X>line.Fragments[0].X&&line.Fragments[^1].X>=picture.X+60-.01,"Image not placed in source run order");
        byte[] png=Composer.PagePng(pages.Pages[0]);File.WriteAllBytes(Path.Combine(root,"inline.png"),png);using var raster=SKBitmap.Decode(png);var pixel=raster.GetPixel((int)picture.X+30,(int)picture.Y+15);Check(pixel.Blue>150&&pixel.Red<80,"Image not actually drawn in raster");
        string dpiDrawing=Drawing().Replace("<a:blip r:embed=\"pic\"/>","<a:blip r:embed=\"pic\"><a:extLst><a:ext uri=\"{28A0092B-C50C-407E-A947-70E740481C1C}\"><a14:useLocalDpi xmlns:a14=\"http://schemas.microsoft.com/office/drawing/2010/main\" val=\"0\"/></a:ext></a:extLst></a:blip>",StringComparison.Ordinal);
        var dpi=Compose(Inspect(root,ImageParagraph(dpiDrawing)+Section(),"inline-dpi",rel:Fixtures.Relationship("pic","image","media/picture.png"),binary:new(){["word/media/picture.png"]=Png()}),out _);
        Check(dpi.Pages.SelectMany(p=>p.Lines).SelectMany(l=>l.Images).Count()==1&&!dpi.Issues.Any(i=>i.Code=="LAYOUT_IMAGE_DIMENSION_INVALID"),"DPI metadata confused with a drawing extent");
        var replacement=input with{SafeMedia=input.SafeMedia.ToDictionary(kv=>kv.Key,kv=>Png(100))};Check(!Composer.Compose(replacement).Available,"Substituted image bytes accepted without matching source hash");
        string chain=string.Concat(Enumerable.Range(0,65).Select(i=>ImageParagraph(Drawing("i"+i))));var assets=Enumerable.Range(0,65).ToDictionary(i=>"word/media/p"+i+".png",i=>Png(i));string rel=string.Concat(Enumerable.Range(0,65).Select(i=>Fixtures.Relationship("i"+i,"image","media/p"+i+".png")));
        var limited=Composer.Compose(Inspect(root,chain+Section(),"image-limit",rel:rel,binary:assets));Check(!limited.Available&&limited.Issues.Any(i=>i.Code=="LAYOUT_IMAGE_BUDGET"),"Image resource budget ignored");
    }
    public static string MixedFixture(string root)
    {
        string body=Fixtures.Paragraph("Composition de structures Word")+Numbered(0,"Première entrée")+Numbered(1,"Niveau secondaire")+"<w:p><w:pPr><w:tabs><w:tab w:val=\"right\" w:pos=\"6000\"/></w:tabs></w:pPr><w:r><w:t>Total</w:t><w:tab/><w:t>123,45</w:t></w:r></w:p>"+ImageParagraph(Drawing())+TableStart()+Row("Article","Description",true)+string.Concat(Enumerable.Range(0,16).Select(i=>Row("Article "+i,"Texte conservé dans la cellule "+i)))+"</w:tbl>"+Fixtures.Paragraph("Fin du document témoin.")+Section();
        Inspect(root,body,"Structures-Word-compatibles",new(){["word/numbering.xml"]=Numbering},Fixtures.Relationship("n","numbering","numbering.xml")+Fixtures.Relationship("pic","image","media/picture.png"),new(){["word/media/picture.png"]=Png()});return Path.Combine(root,"Structures-Word-compatibles.docx");
    }
    public static void Mixed(string root)
    {
        string path=MixedFixture(root),before=Sha(path);var input=DocumentInspector.InspectDocument(path);long allocated=GC.GetTotalAllocatedBytes();var timer=System.Diagnostics.Stopwatch.StartNew();var model=LayoutReader.Read(input);long read=timer.ElapsedMilliseconds;timer.Restart();var pages=Composer.Compose(model);long elapsed=timer.ElapsedMilliseconds;Check(pages.Available,"Mixed unavailable: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);Check(Sha(path)==before,"Mixed original changed");Check(!DocumentExports.Preflight(input,ExportFormat.Pdf).Allowed&&!DocumentExports.Preflight(input,ExportFormat.Odt).Allowed&&!DocumentExports.Preflight(input,ExportFormat.Epub3).Allowed,"New structures incorrectly qualified exports");
        var again=Composer.Compose(model);Check(JsonSerializer.Serialize(pages)==JsonSerializer.Serialize(again),"Rich layout nondeterministic");
        foreach(var page in pages.Pages)File.WriteAllBytes(Path.Combine(root,"mixed-page-"+page.Number+".png"),Composer.PagePng(page));File.WriteAllText(Path.Combine(root,"layout.json"),JsonSerializer.Serialize(new{model,pages},new JsonSerializerOptions{WriteIndented=true}));File.WriteAllText(Path.Combine(root,"measures.json"),JsonSerializer.Serialize(new{readMs=read,composeMs=elapsed,pages=pages.Pages.Count,lines=pages.Pages.Sum(p=>p.Lines.Count),cells=pages.Pages.Sum(p=>p.Cells.Count),images=pages.Pages.Sum(p=>p.Lines.Sum(l=>l.Images.Count)),allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,workingSet=Environment.WorkingSet,sourceSha256=before},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Mixed pages="+pages.Pages.Count);
    }
    public static void Gates(string root)
    {
        var bodies=new[]{TableStart()+"<w:tr>"+Cell("X","<w:gridSpan w:val=\"3\"/>")+"</w:tr></w:tbl>",TableStart()+"<w:tr>"+Cell("X","<w:vMerge/>")+Cell("Y")+"</w:tr></w:tbl>",TableStart()+"<w:tr>"+Cell("A","<w:vMerge w:val=\"restart\"/>")+Cell("B")+"</w:tr><w:tr>"+Cell("Hidden content","<w:vMerge/>")+Cell("C")+"</w:tr></w:tbl>",TableStart()+"<w:tr><w:tc><w:tcPr/>"+TableStart()+Row("Inner","Table")+"</w:tbl>"+Fixtures.Paragraph("")+"</w:tc>"+Cell("Other")+"</w:tr></w:tbl>",TableStart()+Row("Clip","Row",rowProperties:"<w:trHeight w:val=\"10\" w:hRule=\"exact\"/>")+"</w:tbl>",TableStart()+Row("Too tall","Row",rowProperties:"<w:trHeight w:val=\"10000\" w:hRule=\"atLeast\"/>")+"</w:tbl>"};
        foreach(var body in bodies){var input=Inspect(root,body+Section(),"gate-"+Guid.NewGuid().ToString("N"));Check(!Composer.Compose(input).Available&&ReadableReader.Read(input).Available,"Table refusal lost or simplified fallback missing");}
        foreach(var (name,drawing,bytes) in new[]{("floating",Drawing(floating:true),Png()),("missing",Drawing("absent"),Png()),("invalid",Drawing(),Png().Select((b,i)=>(byte)(i==50?b^1:b)).ToArray()),("oversize",Drawing(width:12000000),Png())})
        {var input=Inspect(root,ImageParagraph(drawing)+Section(),name,rel:Fixtures.Relationship("pic","image","media/picture.png"),binary:new(){["word/media/picture.png"]=bytes});Check(!Composer.Compose(input).Available&&ReadableReader.Read(input).Available,"Image unsafe/unqualified structure silently displayed");}
    }
    public static void Corpus(string root)
    {
        string folder=Path.Combine(Directory.GetCurrentDirectory(),"exemples");var sources=Directory.GetFiles(folder,"*",SearchOption.AllDirectories).Where(p=>Path.GetExtension(p).Equals(".docx",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.Ordinal).ToList();string? guide=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT");if(guide is not null)sources.Add(guide);Check(sources.Count>0,"Corpus unavailable");var results=new List<object>();
        foreach(string original in sources)
        {
            string before=Sha(original),copy=Path.Combine(root,"copy-"+results.Count+".docx");File.Copy(original,copy);var timer=System.Diagnostics.Stopwatch.StartNew();var input=DocumentInspector.InspectDocument(copy);var model=LayoutReader.Read(input);var pages=Composer.Compose(model);var reading=ReadableReader.Read(input);
            if(pages.Available)Integrity(model,pages);else Check(pages.Pages.Count==0,"Unqualified real file has invented pages");Check(Sha(original)==before&&Sha(copy)==before,"Corpus original or copy changed");results.Add(new{name=Path.GetFileName(original),sourceSha256=before,inspection=input.Manifest.InspectionComplete,paginated=pages.Available,pages=pages.Pages.Count,simplified=reading.Available,tableCount=model.Tables.Count,elapsedMs=timer.ElapsedMilliseconds,workingSet=Environment.WorkingSet,blockers=pages.Issues.Where(i=>i.BlocksRendering).GroupBy(i=>i.Code).Select(g=>new{code=g.Key,count=g.Count()})});
        }
        File.WriteAllText(Path.Combine(root,"corpus.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Corpus copies qualified="+results.Count+"; originals unchanged");
    }
    public static void RealTable(string root)
    {
        string path=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit Guide copy required");string before=Sha(path);
        using var zip=System.IO.Compression.ZipFile.OpenRead(path);using var input=zip.GetEntry("word/document.xml")!.Open();
        using var reader=System.Xml.XmlReader.Create(input,new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024});
        var xml=System.Xml.Linq.XDocument.Load(reader);System.Xml.Linq.XNamespace w=Fixtures.W;var table=xml.Descendants(w+"tbl").First();
        // Explicit isolation fixture, not a claim that the entire Guide is paginated.
        var witness=Inspect(root,table.ToString()+Section(12240,15840),"Guide-table-isolee");var pages=Compose(witness,out var model);
        Check(model.Tables.Count==1&&pages.Pages.SelectMany(p=>p.Cells).Any(),"Real table not represented");
        Check(Sha(path)==before,"Guide original changed");
        foreach(var page in pages.Pages)File.WriteAllBytes(Path.Combine(root,"guide-table-page-"+page.Number+".png"),Composer.PagePng(page));
        File.WriteAllText(Path.Combine(root,"real-table.json"),JsonSerializer.Serialize(new{sourceSha256=before,isolation=true,changedPageGeometry="612 x 792 pt, margins 25 pt",pages=pages.Pages.Count,rows=model.Tables[0].Rows.Count,cells=model.Tables[0].Cells.Count,sourceDescription="First table copied intact; other source blocks are outside this explicit isolation fixture"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("Isolated Guide table: "+pages.Pages.Count+" pages, "+model.Tables[0].Rows.Count+" rows; complete Guide remains unqualified");
    }
    public static void Gui(string root)
    {
        AppBuilder.Configure<ReaderApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).UseSkia().WithInterFont().SetupWithoutStarting();var window=new ReaderWindow();window.Show();AboutCases.Complete(window.OpenDocumentAsync(MixedFixture(root)));VerifyGui(window);window.Close();
    }
    static void VerifyGui(ReaderWindow window)
    {
        Check(window.CurrentComposition is {Available:true,Profile:PageComposer.Profile}&&window.CurrentComposition.Pages.Count>=2,
            "No native multipage table in viewer");
        Check(window.CurrentComposition!.Pages.Skip(1).SelectMany(p=>p.Cells).Any(c=>c.IsRepeated),"Repeated table headers are missing");
        window.GoToPage(1);Check(window.CurrentPage==2,"Rich page navigation failed");window.FitToWidth();
        var composition=window.CurrentComposition??throw new InvalidOperationException("Composition unavailable");
        var sourceLine=composition.Pages.Last().Lines.Last(l=>l.CellId is not null&&!l.IsRepeated);
        var paragraph=ReadablePresentation.Walk(window.CurrentReading!.Blocks).First(b=>b.Source==sourceLine.Source&&(b.Kind=="paragraph"||b.Kind=="heading"));
        Check(window.NavigateToBlock(paragraph.Id)&&!window.IsSimplifiedReading&&window.CurrentPage==composition.Pages.Count,"Cell paragraph navigation did not reach the calculated page");
        var mode=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ReadingMode");mode.SelectedIndex=1;Dispatcher.UIThread.RunJobs();Check(window.IsSimplifiedReading,"Simplified mode unavailable");mode.SelectedIndex=0;Dispatcher.UIThread.RunJobs();Check(window.CurrentPage>0,"Return to pages failed");Check(!window.ExportAvailable(ExportFormat.Pdf),"Rich viewer loosened export guard");
    }
    public static async Task Native(ReaderWindow window,string root)
    {
        string path=MixedFixture(root),before=Sha(path);await window.OpenDocumentAsync(path);await Task.Delay(250);VerifyGui(window);var xid=window.TryGetPlatformHandle();Check(xid?.HandleDescriptor=="XID","Native X11 window required");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.GoToPage(0);window.FitToWidth();await Task.Delay(200);Capture("native-structures-"+theme);window.GoToPage(window.CurrentComposition!.Pages.Count-1);await Task.Delay(200);Capture("native-table-last-"+theme);window.Width=860;window.Height=620;await Task.Delay(150);Capture("native-small-"+theme);
        }
        Check(Sha(path)==before,"Native modified witness");File.WriteAllBytes(Path.Combine(root,"native-report.json"),window.DetailedDiagnosticReport());File.WriteAllText(Path.Combine(root,"native-measures.json"),JsonSerializer.Serialize(new{window.RenderScaling,pages=window.CurrentComposition!.Pages.Count,workingSet=Environment.WorkingSet},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Native structure pages="+window.CurrentComposition!.Pages.Count);
        void Capture(string name){var cmd=new System.Diagnostics.ProcessStartInfo("import"){UseShellExecute=false};foreach(string arg in new[]{"-window",xid!.Handle.ToInt64().ToString(),Path.Combine(root,name+".png")})cmd.ArgumentList.Add(arg);using var process=System.Diagnostics.Process.Start(cmd)!;process.WaitForExit();Check(process.ExitCode==0,"Native capture failed");}
    }
}
