using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SkiaSharp;
using LecteurWord.DocxCore;
using LecteurWord.Flow;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LecteurWord;

static class CompatibilityCases
{
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Sha(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    static string Section=>"<w:sectPr w:rsidSect=\"00112233\"><w:pgSz w:w=\"6000\" w:h=\"6000\"/><w:pgMar w:left=\"500\" w:right=\"500\" w:top=\"500\" w:bottom=\"500\"/><w:docGrid w:linePitch=\"360\"/></w:sectPr>";
    static InspectionResult Inspect(string root,string body,string name,Dictionary<string,string>? extra=null,string rel="",Dictionary<string,byte[]>? binary=null)
    {
        string path=Fixtures.Write(root,name+".docx",Fixtures.Parts(body,extra,rel,binary));string hash=Sha(path);
        var input=DocumentInspector.InspectDocument(path);Check(input.Manifest.InspectionComplete&&Sha(path)==hash,"Fixture inspection/integrity failed");return input;
    }
    public static void Paragraphs(string root)
    {
        string text=string.Concat(Enumerable.Repeat("Un paragraphe justifié conserve ses caractères et ses espaces. ",12));
        string body="<w:p><w:pPr><w:jc w:val=\"both\"/><w:ind w:right=\"-57\"/><w:contextualSpacing/><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:pPr><w:bookmarkStart w:id=\"1\" w:name=\"repere\"/><w:r><w:t xml:space=\"preserve\">"+text+"</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>";
        body+="<w:p><w:hyperlink r:id=\"link\" w:history=\"1\"><w:r><w:rPr><w:noProof/></w:rPr><w:t>Libellé du lien</w:t></w:r></w:hyperlink></w:p>"+Section;
        var input=Inspect(root,body,"paragraphs",rel:Fixtures.Relationship("link","hyperlink","https://example.invalid/never-load","External"));
        var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        Check(pages.Available,"Common paragraph properties refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);
        var lines=pages.Pages.SelectMany(p=>p.Lines).Where(l=>l.ParagraphId==model.Paragraphs[0].Id).ToArray();
        Check(lines.Length>3,"Justification witness did not wrap");
        foreach(var line in lines[..^1])
        {
            var last=line.Fragments.Last(f=>f.Text.Trim().Length>0);using var face=SkiaSharp.SKTypeface.FromFamilyName(last.Style.ResolvedFont);using var font=new SkiaSharp.SKFont(face,last.Style.Size);
            Check(Math.Abs(last.X+font.MeasureText(last.Text.TrimEnd())-277.85f)<.2f,"Justified right edge not reached");
        }
        Check(model.Issues.Any(i=>i.Code=="LAYOUT_HYPERLINK_TEXT"&&!i.BlocksRendering),"Link provenance not diagnosed");
        Check(pages.Pages.SelectMany(p=>p.Lines).Any(l=>l.Text=="Libellé du lien"),"Link content lost");
        File.WriteAllBytes(Path.Combine(root,"paragraphs.png"),Composer.PagePng(pages.Pages[0]));
        File.WriteAllText(Path.Combine(root,"paragraphs.json"),JsonSerializer.Serialize(new{model,pages},Json));
    }
    public static void Spacing(string root)
    {
        string styles=Fixtures.Xml("styles","<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:pPr><w:spacing w:before=\"200\" w:after=\"300\"/><w:contextualSpacing/></w:pPr><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:style>");
        string P(string text,string props="")=>"<w:p><w:pPr>"+props+"</w:pPr><w:r><w:t>"+text+"</w:t></w:r></w:p>";
        var input=Inspect(root,P("Premier")+P("Deuxième")+P("Troisième","<w:rPr><w:sz w:val=\"48\"/></w:rPr>")+Section,"contextual",new(){["word/styles.xml"]=styles},Fixtures.Relationship("s","styles","styles.xml"));
        var model=LayoutReader.Read(input);var pages=Composer.Compose(model);Check(pages.Available,"Contextual/mark formatting unavailable");Integrity(model,pages);
        var lines=pages.Pages[0].Lines;Check(lines.Count==3,"Unexpected line count");
        Check(Math.Abs(lines[1].Baseline-lines[0].Baseline-lines[0].Height)<.02f,"Contextual paragraph spacing not suppressed");
        Check(lines[2].Height>lines[1].Height*1.8&&lines[2].Fragments.All(f=>f.Style.Size==12),"Paragraph mark leaked into text style or did not affect metrics");
        var ordinary=Composer.Compose(Inspect(root,P("Un","<w:spacing w:after=\"300\"/>")+P("Deux","<w:spacing w:before=\"200\"/>")+Section,"collapsed"));
        Check(ordinary.Available&&Math.Abs(ordinary.Pages[0].Lines[1].Baseline-ordinary.Pages[0].Lines[0].Baseline-ordinary.Pages[0].Lines[0].Height-15)<.02f,"Adjacent spacing not collapsed by maximum");
        string inheritance=Fixtures.Xml("styles","<w:style w:type=\"paragraph\" w:styleId=\"Base\"><w:pPr><w:rPr><w:sz w:val=\"48\"/></w:rPr></w:pPr></w:style><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Derived\"><w:basedOn w:val=\"Base\"/><w:rPr><w:sz w:val=\"24\"/></w:rPr></w:style>");
        var cascade=LayoutReader.Read(Inspect(root,P("Cascade")+Section,"mark-cascade",new(){["word/styles.xml"]=inheritance},Fixtures.Relationship("s","styles","styles.xml")));
        Check(cascade.Available&&cascade.Paragraphs[0].Runs.Single(r=>r.Kind=="paragraphMark").Style.Size==12,"Derived style did not override inherited paragraph mark");
        var trailing=Composer.Compose(Inspect(root,"<w:p><w:pPr><w:rPr><w:sz w:val=\"30\"/></w:rPr></w:pPr><w:r><w:t>Avant</w:t><w:br/></w:r></w:p>"+Section,"mark-break"));
        Check(trailing.Available&&trailing.Pages[0].Lines.Count==2,"Paragraph mark hid trailing line break");
        File.WriteAllText(Path.Combine(root,"spacing.json"),JsonSerializer.Serialize(new{model,pages},Json));
    }
    public static void TabWrap(string root)
    {
        string body="<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:t>En-tête centré</w:t>"+string.Concat(Enumerable.Repeat("<w:tab/>",8))+"</w:r></w:p><w:p><w:r>"+string.Concat(Enumerable.Repeat("<w:tab/>",8))+"<w:t>Une signature puis un texte qui revient naturellement à la ligne.</w:t></w:r></w:p>"+Section;
        var input=Inspect(root,body,"tabs-wrap");var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        Check(pages.Available,"Left tab wrapping refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);
        Check(pages.Pages.SelectMany(p=>p.Lines).Sum(l=>l.Tabs.Count)==16,"Wrapped tabs lost");
        Check(pages.Pages.SelectMany(p=>p.Lines).All(l=>l.Tabs.All(t=>t.From>=0&&t.To<=300.01f)),"Wrapped tabs outside page");
        Check(JsonSerializer.Serialize(pages)==JsonSerializer.Serialize(Composer.Compose(model)),"Tab wrapping nondeterministic");
        File.WriteAllBytes(Path.Combine(root,"tabs-wrap.png"),Composer.PagePng(pages.Pages[0]));
    }
    public static void AutoFit(string root)
    {
        string Cell(string text,string width="")=>"<w:tc><w:tcPr>"+width+"</w:tcPr>"+Fixtures.Paragraph(text)+"</w:tc>";
        string Table(string first,string second,string props="",string firstWidth="")=>"<w:tbl><w:tblPr><w:tblLayout w:type=\"autofit\"/>"+props+"</w:tblPr><w:tblGrid><w:gridCol w:w=\"2400\"/><w:gridCol w:w=\"2400\"/></w:tblGrid><w:tr>"+Cell(first,firstWidth)+Cell(second)+"</w:tr></w:tbl>";
        var input=Inspect(root,Table("I","Mesure réelle du contenu")+Section,"autofit");var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        Check(pages.Available,"AutoFit refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);
        var cells=pages.Pages[0].Cells;Check(cells[1].Width>cells[0].Width*2,"AutoFit did not measure actual contents");Check(cells.Sum(c=>c.Width)<=250.01f,"AutoFit outside page body");
        var preferred=Composer.Compose(Inspect(root,Table("A","B","<w:tblW w:type=\"pct\" w:w=\"4000\"/>","<w:tcW w:type=\"pct\" w:w=\"3500\"/>")+Section,"autofit-preferred"));
        Check(preferred.Available&&Math.Abs(preferred.Pages[0].Cells.Sum(c=>c.Width)-200)<.1f&&preferred.Pages[0].Cells[0].Width>100,"Preferred table/cell widths not respected");
        var overflow=Composer.Compose(Inspect(root,Table(new string('W',100),"B")+Section,"autofit-overflow"));Check(!overflow.Available&&overflow.Issues.Any(i=>i.Code=="LAYOUT_AUTOFIT_MINIMUM_TOO_WIDE"),"Impossible AutoFit silently clipped");
        File.WriteAllBytes(Path.Combine(root,"autofit.png"),Composer.PagePng(pages.Pages[0]));
        File.WriteAllText(Path.Combine(root,"autofit.json"),JsonSerializer.Serialize(new{model,pages,preferred,overflow},Json));
    }
    static byte[] Jpeg()
    {
        using var bitmap=new SKBitmap(48,24);using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.Blue);
        using var paint=new SKPaint{Color=SKColors.Red};canvas.DrawRect(0,0,24,24,paint);using var image=SKImage.FromBitmap(bitmap);using var bytes=image.Encode(SKEncodedImageFormat.Jpeg,95);return bytes.ToArray();
    }
    static string Drawing=>"<w:drawing><wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"1\" name=\"JPEG synthétique\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"witness.jpg\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"pic\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"457200\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";
    public static void Images(string root)
    {
        var parts=Fixtures.Parts("<w:p><w:r><w:t>Avant</w:t>"+Drawing+"<w:t>Après</w:t></w:r></w:p>"+Section,relationships:Fixtures.Relationship("pic","image","media/witness.jpg"),binary:new(){["word/media/witness.jpg"]=Jpeg()});
        parts["[Content_Types].xml"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(parts["[Content_Types].xml"]).Replace("</Types>","<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/></Types>",StringComparison.Ordinal));
        string path=Fixtures.Write(root,"jpeg.docx",parts),hash=Sha(path);var input=DocumentInspector.InspectDocument(path);var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        Check(pages.Available,"JPEG refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);
        var line=pages.Pages[0].Lines.Single();var image=line.Images.Single();Check(image.Image.Width==72&&image.Image.Height==36&&image.Image.PixelWidth==48&&image.Image.PixelHeight==24,"JPEG dimensions changed");
        Check(line.Fragments[1].X>=image.X+72-.01f,"JPEG did not occupy its inline width");
        byte[] png=Composer.PagePng(pages.Pages[0]);using var bitmap=SKBitmap.Decode(png);var pixel=bitmap.GetPixel((int)image.X+12,(int)image.Y+18);Check(pixel.Red>180&&pixel.Blue<70,"JPEG not drawn with expected colors");
        Check(Sha(path)==hash,"JPEG source modified");
        Check(!DocumentExports.Preflight(input,ExportFormat.Pdf).Allowed&&!DocumentExports.Preflight(input,ExportFormat.Odt).Allowed&&!DocumentExports.Preflight(input,ExportFormat.Epub3).Allowed,"JPEG extended export qualification");
        File.WriteAllBytes(Path.Combine(root,"jpeg-page.png"),png);File.WriteAllText(Path.Combine(root,"jpeg.json"),JsonSerializer.Serialize(new{model,pages},Json));
    }
    static byte[] Exif(byte[] jpeg,int orientation,bool bigEndian=false)
    {
        byte[] tiff=new byte[26];tiff[0]=tiff[1]=(byte)(bigEndian?'M':'I');
        void U16(int p,ushort v){if(bigEndian)System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(p,2),v);else System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(p,2),v);}
        void U32(int p,uint v){if(bigEndian)System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(p,4),v);else System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(p,4),v);}
        U16(2,42);U32(4,8);U16(8,1);U16(10,274);U16(12,3);U32(14,1);U16(18,(ushort)orientation);
        return jpeg[..2].Concat(new byte[]{255,225,0,34}).Concat("Exif\0\0"u8.ToArray()).Concat(tiff).Concat(jpeg[2..]).ToArray();
    }
    public static void JpegSafety(string root)
    {
        byte[] raw=Jpeg();var results=new List<object>();
        using(var gray=new SKBitmap(new SKImageInfo(24,12,SKColorType.Gray8,SKAlphaType.Opaque)))
        {
            gray.Erase(SKColors.Gray);using var image=SKImage.FromBitmap(gray);using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,95);byte[] bytes=encoded.ToArray();
            int grayFrame=Enumerable.Range(0,bytes.Length-1).First(i=>bytes[i]==255&&bytes[i+1]==192);Check(bytes[grayFrame+9]==1,"Gray witness is not a one-component JPEG");
            Check(SafeJpeg.TryGetRenderableBytes(bytes,out var png,out int width,out int height,out _,out _)&&width==24&&height==12,"Grayscale baseline JPEG refused");
            using var raster=SKBitmap.Decode(png);var pixel=raster.GetPixel(10,5);Check(Math.Abs(pixel.Red-pixel.Blue)<=1&&Math.Abs(pixel.Red-pixel.Green)<=1,"Grayscale JPEG changed channels");
        }
        foreach(bool big in new[]{false,true})foreach(int orientation in Enumerable.Range(1,8))
        {
            byte[] input=Exif(raw,orientation,big);string hash=Convert.ToHexStringLower(SHA256.HashData(input));
            Check(SafeJpeg.TryGetRenderableBytes(input,out var png,out int width,out int height,out int origin,out _,default),"EXIF orientation refused: "+orientation);
            Check(origin==orientation&&width==(orientation>=5?24:48)&&height==(orientation>=5?48:24),"EXIF dimensions incorrect");
            using var bitmap=SKBitmap.Decode(png);var first=bitmap.GetPixel(width/4,height/4);var second=bitmap.GetPixel(width*3/4,height*3/4);
            bool redFirst=orientation is 1 or 4 or 5 or 6;
            Check(redFirst?first.Red>180&&second.Blue>180:first.Blue>180&&second.Red>180,"EXIF transform pixels incorrect: "+orientation);
            Check(Convert.ToHexStringLower(SHA256.HashData(input))==hash&&SafePng.Validate(png),"JPEG input changed or output PNG unsafe");results.Add(new{bigEndian=big,orientation,width,height});
        }
        byte[] huge=raw.ToArray(),progressive=raw.ToArray(),cmyk=raw.ToArray();int frame=-1;
        for(int i=0;i<raw.Length-1;i++)if(raw[i]==255&&raw[i+1]==192){frame=i;break;}Check(frame>=0,"JPEG frame missing");
        huge[frame+7]=huge[frame+8]=255;progressive[frame+1]=194;cmyk[frame+9]=4;
        byte[] badExif=Exif(raw,1);Array.Fill(badExif,(byte)255,16,4);
        foreach(var input in new[]{raw[..^2],raw.Concat(new byte[]{0}).ToArray(),huge,progressive,cmyk,Exif(raw,9),badExif})
            Check(!SafeJpeg.TryGetRenderableBytes(input,out _,out _,out _,out _,out _),"Malformed/oversize JPEG accepted");
        using var cancel=new CancellationTokenSource();cancel.Cancel();try{SafeJpeg.TryGetRenderableBytes(raw,out _,out _,out _,out _,out _,cancel.Token);throw new InvalidOperationException("JPEG cancellation ignored");}catch(OperationCanceledException){}
        File.WriteAllText(Path.Combine(root,"jpeg-safety.json"),JsonSerializer.Serialize(new{grayscale=true,orientations=results,refusals=7,cancellation=true},Json));
    }
    public static void TableStyles(string root)
    {
        string sides=string.Concat(new[]{"top","bottom","left","right","insideH","insideV"}.Select(s=>"<w:"+s+" w:val=\"double\" w:sz=\"8\" w:color=\"345678\"/>"));
        string styles=Fixtures.Xml("styles","<w:style w:type=\"table\" w:styleId=\"Base\"><w:tblPr><w:tblBorders>"+sides+"</w:tblBorders></w:tblPr><w:rPr><w:b/></w:rPr></w:style><w:style w:type=\"table\" w:styleId=\"Derived\"><w:basedOn w:val=\"Base\"/><w:tcPr><w:shd w:val=\"clear\" w:fill=\"E0E8F0\"/></w:tcPr></w:style>");
        string Cell(string text,string properties="")=>"<w:tc><w:tcPr>"+properties+"</w:tcPr>"+Fixtures.Paragraph(text)+"</w:tc>";
        string table="<w:tbl><w:tblPr><w:tblStyle w:val=\"Derived\"/><w:tblLayout w:type=\"autofit\"/><w:tblLook w:val=\"0000\"/></w:tblPr><w:tr><w:trPr><w:tblHeader/></w:trPr>"+Cell("En-tête","<w:gridSpan w:val=\"2\"/>")+"</w:tr><w:tr>"+Cell("Fusion","<w:vMerge w:val=\"restart\"/>")+Cell("Un")+"</w:tr><w:tr>"+Cell("","<w:vMerge/>")+Cell("Deux")+"</w:tr>"+string.Concat(Enumerable.Range(0,24).Select(i=>"<w:tr>"+Cell("Ligne "+i)+Cell("Valeur")+"</w:tr>"))+"</w:tbl>";
        var input=Inspect(root,table+Section,"table-styles",new(){["word/styles.xml"]=styles},Fixtures.Relationship("s","styles","styles.xml"));var model=LayoutReader.Read(input);var pages=Composer.Compose(model);
        Check(pages.Available,"Table style/AutoFit merge refused: "+string.Join(',',pages.Issues.Where(i=>i.BlocksRendering).Select(i=>i.Code)));Integrity(model,pages);
        Check(pages.Pages.Count>1&&pages.Pages.Skip(1).All(p=>p.Cells.Any(c=>c.IsRepeated)),"AutoFit repeated header lost");
        Check(pages.Pages.SelectMany(p=>p.Cells).Any(c=>c.MergedSourceIds.Count==1),"AutoFit vertical merge lost");
        Check(pages.Pages.SelectMany(p=>p.Cells).All(c=>c.Fill=="E0E8F0"&&c.Borders.Top is {Style:"double",Width:1}),"Inherited table appearance lost");
        Check(pages.Pages.SelectMany(p=>p.Lines).SelectMany(l=>l.Fragments).All(f=>f.Style.Bold),"Inherited table run style lost");
        string conditional=styles.Replace("<w:basedOn w:val=\"Base\"/>","<w:basedOn w:val=\"Base\"/><w:tblStylePr w:type=\"firstRow\"><w:rPr><w:i/></w:rPr></w:tblStylePr>",StringComparison.Ordinal);
        var refused=Composer.Compose(Inspect(root,table+Section,"table-conditional",new(){["word/styles.xml"]=conditional},Fixtures.Relationship("s","styles","styles.xml")));
        Check(!refused.Available&&refused.Issues.Any(i=>i.Code=="LAYOUT_CONDITIONAL_TABLE_STYLE_UNQUALIFIED"),"Conditional table style silently flattened");
        string rowStyle=styles.Replace("<w:basedOn w:val=\"Base\"/>","<w:basedOn w:val=\"Base\"/><w:trPr><w:trHeight w:val=\"1000\" w:hRule=\"exact\"/></w:trPr>",StringComparison.Ordinal);
        var rowRefused=Composer.Compose(Inspect(root,table+Section,"table-row-style",new(){["word/styles.xml"]=rowStyle},Fixtures.Relationship("s","styles","styles.xml")));
        Check(!rowRefused.Available&&rowRefused.Issues.Any(i=>i.Code=="LAYOUT_TABLE_ROW_STYLE_UNQUALIFIED"),"Unqualified row style silently ignored");
        foreach(var page in pages.Pages)File.WriteAllBytes(Path.Combine(root,"table-style-"+page.Number+".png"),Composer.PagePng(page));
        File.WriteAllText(Path.Combine(root,"table-style.json"),JsonSerializer.Serialize(new{model,pages},Json));
    }
    public static void Boundaries(string root)
    {
        string separators=Fixtures.Xml("endnotes","<w:endnote w:type=\"separator\" w:id=\"-1\"><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr><w:r><w:separator/></w:r></w:p></w:endnote>");
        var empty=Inspect(root,Fixtures.Paragraph("Corps complet")+Section,"empty-notes",new(){["word/endnotes.xml"]=separators},Fixtures.Relationship("n","endnotes","endnotes.xml"));
        Check(Composer.Compose(empty).Available,"Separator-only note part blocked complete body");
        string actual=separators.Replace("</w:endnotes>","<w:endnote w:id=\"1\">"+Fixtures.Paragraph("Note utilisateur conservée")+"</w:endnote></w:endnotes>",StringComparison.Ordinal);
        var note=Inspect(root,Fixtures.Paragraph("Corps")+Section,"actual-notes",new(){["word/endnotes.xml"]=actual},Fixtures.Relationship("n","endnotes","endnotes.xml"));
        Check(!Composer.Compose(note).Available&&note.Manifest.Nodes.Any(n=>n.Text=="Note utilisateur conservée"),"Actual note dropped to qualify body");
        foreach(string body in new[]{"<w:p><w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>","<w:p><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>","<w:p><w:ins w:id=\"1\"><w:r><w:t>Révision</w:t></w:r></w:ins></w:p>"})
        {
            var input=Inspect(root,body+Section,"boundary-"+Guid.NewGuid().ToString("N"));byte[] manifest=ManifestJson.Serialize(input.Manifest);
            Check(!Composer.Compose(input).Available&&ReadableReader.Read(input).Available&&ManifestJson.Serialize(input.Manifest).SequenceEqual(manifest),"Structure/provenance changed or unqualified story accepted");
        }
        var grid=Composer.Compose(Inspect(root,Fixtures.Paragraph("Grille active")+Section.Replace("<w:docGrid ","<w:docGrid w:type=\"lines\" ",StringComparison.Ordinal),"active-grid"));
        Check(!grid.Available&&grid.Issues.Any(i=>i.Code=="LAYOUT_DOCUMENT_GRID_UNQUALIFIED"),"Active grid silently ignored");
        var hardJustify=Composer.Compose(Inspect(root,"<w:p><w:pPr><w:jc w:val=\"both\"/></w:pPr><w:r><w:t>Avant</w:t><w:br/><w:t>Après</w:t></w:r></w:p>"+Section,"justify-manual"));
        Check(!hardJustify.Available&&hardJustify.Issues.Any(i=>i.Code=="LAYOUT_JUSTIFY_MANUAL_BREAK_UNQUALIFIED"&&i.Source is not null),"Manual-break justification silently approximated");
        var hyphenation=Composer.Compose(Inspect(root,Fixtures.Paragraph("Césure automatique")+Section,"hyphenation",new(){["word/settings.xml"]=Fixtures.Xml("settings","<w:autoHyphenation/>")},Fixtures.Relationship("s","settings","settings.xml")));
        Check(!hyphenation.Available&&hyphenation.Issues.Any(i=>i.Code=="LAYOUT_SETTING_UNQUALIFIED"),"Active automatic hyphenation silently ignored");
        var footer=Inspect(root,Fixtures.Paragraph("Corps")+Section.Replace("</w:sectPr>","<w:footerReference r:id=\"f\" w:type=\"default\"/></w:sectPr>",StringComparison.Ordinal),"footer",new(){["word/footer1.xml"]=Fixtures.Xml("ftr",Fixtures.Paragraph("Pied conservé"))},Fixtures.Relationship("f","footer","footer1.xml"));
        Check(!Composer.Compose(footer).Available&&footer.Manifest.Nodes.Any(n=>n.Text=="Pied conservé"),"Footer removed to qualify pages");
        Console.WriteLine("Notes, fields, revisions, active grids and footers remain explicit boundaries");
    }
    public static async Task Native(ReaderWindow window,string root)
    {
        string original=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit compatible real copy required");string hash=Sha(original),copy=Path.Combine(root,"real-compatible.docx");File.Copy(original,copy);
        var watch=Stopwatch.StartNew();await window.OpenDocumentAsync(copy);long loadMs=watch.ElapsedMilliseconds;await Task.Delay(250);
        Check(window.CurrentComposition?.Available==true&&!window.IsSimplifiedReading&&window.CurrentPage==1,"Compatible real file not paginated in native GUI");
        double expected=double.Parse(Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_SCALE")??"1",System.Globalization.CultureInfo.InvariantCulture);Check(Math.Abs(window.RenderScaling-expected)<.01,"Native render scale mismatch");
        var handle=window.TryGetPlatformHandle();Check(handle?.HandleDescriptor=="XID","Native X11 required");
        foreach(int theme in new[]{1,2})
        {
            window.SetTheme(theme);window.FitToWidth();await Task.Delay(200);await Capture("real-theme-"+theme);
            var mode=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="ReadingMode");mode.SelectedIndex=1;await Task.Delay(150);Check(window.IsSimplifiedReading,"Simplified real reading lost");mode.SelectedIndex=0;await Task.Delay(150);Check(window.CurrentPage==1,"Return to real calculated page failed");
            window.Width=860;window.Height=620;window.FitToWidth();await Task.Delay(200);await Capture("real-small-"+theme);
            var image=window.GetVisualDescendants().OfType<Image>().Single(i=>i.Name=="CalculatedPage");Check(image.Bounds.Width>0&&image.Bounds.Height>0,"Native calculated bitmap missing");
            Check(!window.ExportAvailable(ExportFormat.Pdf)&&!window.ExportAvailable(ExportFormat.Odt)&&!window.ExportAvailable(ExportFormat.Epub3),"Native GUI extended real export qualification");
        }
        Check(Sha(original)==hash&&Sha(copy)==hash,"Native GUI changed input");File.WriteAllBytes(Path.Combine(root,"native-diagnostics.json"),window.DetailedDiagnosticReport());
        File.WriteAllText(Path.Combine(root,"native-measures.json"),JsonSerializer.Serialize(new{loadMs,window.RenderScaling,window.DesktopScaling,pages=window.CurrentComposition!.Pages.Count,workingSet=Environment.WorkingSet,sourceSha256=hash},Json));
        Console.WriteLine("Compatible real native GUI OK; scale="+window.RenderScaling+"; pages="+window.CurrentComposition.Pages.Count);
        async Task Capture(string name)
        {
            var info=new ProcessStartInfo("import"){UseShellExecute=false};foreach(string value in new[]{"-window",handle!.Handle.ToInt64().ToString(),Path.Combine(root,name+".png")})info.ArgumentList.Add(value);
            using var process=Process.Start(info)!;await process.WaitForExitAsync();Check(process.ExitCode==0,"Native capture failed");
        }
    }
    public static void Corpus(string root)
    {
        var sources=Directory.GetFiles("exemples","*.docx",SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        string guide=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT")??throw new InvalidOperationException("Explicit eleventh corpus copy required");
        sources.Add(guide);Check(sources.Count==11,"Expected exactly 11 corpus documents");var results=new List<object>();
        foreach(string original in sources)
        {
            string hash=Sha(original),copy=Path.Combine(root,"copy-"+results.Count+".docx");File.Copy(original,copy);
            var watch=Stopwatch.StartNew();var input=DocumentInspector.InspectDocument(copy);long inspectionMs=watch.ElapsedMilliseconds;
            watch.Restart();var model=LayoutReader.Read(input);long readerMs=watch.ElapsedMilliseconds;
            watch.Restart();var pages=Composer.Compose(model);long compositionMs=watch.ElapsedMilliseconds;
            if(model.Available&&!pages.Available)File.WriteAllText(Path.Combine(root,"corpus-"+results.Count+"-model.json"),JsonSerializer.Serialize(model,Json));
            if(pages.Available)
            {
                Integrity(model,pages);var runs=model.Paragraphs.SelectMany(p=>p.Runs).Where(r=>r.Kind=="text").ToLookup(r=>r.Id);
                var children=input.Manifest.Nodes.ToLookup(n=>n.ParentId);
                foreach(var node in input.Manifest.Nodes.Where(n=>n.Name=="{"+Fixtures.W+"}t"&&model.Paragraphs.Any(p=>p.Source.PartUri==n.Source.PartUri)))
                    Check(string.Concat(runs[node.Id].Select(r=>r.Text))==string.Concat(children[node.Id].Where(n=>n.Kind=="textValue").Select(n=>n.Text)),"Inventory text omitted from complete real layout");
            }
            else Check(pages.Pages.Count==0,"Partial pages falsely marked complete");
            Check(Sha(original)==hash&&Sha(copy)==hash,"Corpus input changed");
            var bySource=input.Manifest.Nodes.ToLookup(n=>n.Source);
            var details=pages.Issues.Select(i=>new{i.Code,i.Source,i.Message,i.BlocksRendering,nodes=i.Source is null?[]:bySource[i.Source].Select(n=>new{n.Name,n.Attributes}).ToArray()}).ToArray();
            results.Add(new{name=Path.GetFileName(original),sha256=hash,inspection=input.Manifest.InspectionComplete,paginated=pages.Available,pages=pages.Pages.Count,inspectionMs,readerMs,compositionMs,workingSet=Environment.WorkingSet,peakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64,tableCount=model.Tables.Count,details});
            if(pages.Available)
            {
                File.WriteAllBytes(Path.Combine(root,"corpus-"+(results.Count-1)+"-first.png"),Composer.PagePng(pages.Pages[0]));
                File.WriteAllText(Path.Combine(root,"corpus-"+(results.Count-1)+"-pages.json"),JsonSerializer.Serialize(pages,Json));
            }
        }
        File.WriteAllText(Path.Combine(root,"corpus-details.json"),JsonSerializer.Serialize(results,Json));
        Console.WriteLine("11 corpus copies inspected; original hashes unchanged");
    }
    static void Integrity(PaginatedDocument model,Composition pages)
    {
        foreach(var p in model.Paragraphs.Where(p=>p.TableId is null))
            Check(string.Concat(pages.Pages.SelectMany(pg=>pg.Lines).Where(l=>l.ParagraphId==p.Id&&!l.IsRepeated).Select(l=>l.Text+l.SourceBreak))==p.Text,"Lost source text: "+p.Id);
        Check(pages.Pages.All(pg=>pg.Lines.All(l=>l.Source.PartUri.Length>0)),"Missing provenance");
    }
}
