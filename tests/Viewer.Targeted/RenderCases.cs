using System.Security.Cryptography;
using System.Text.Json;
using LecteurWord.DocxCore;
using LecteurWord.Flow;

static class RenderCases
{
    const string WP="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing",WP14="http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";
    static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static void Geometry(string root)
    {
        string drawing="<w:p><w:r><w:drawing xmlns:wp=\""+WP+"\" xmlns:wp14=\""+WP14+"\"><wp:positionH><wp:posOffset>0</wp:posOffset></wp:positionH><wp14:sizeRelH><wp14:pctWidth>0</wp14:pctWidth></wp14:sizeRelH><wp14:sizeRelV><wp14:pctHeight>0</wp14:pctHeight></wp14:sizeRelV><w:txbxContent>"+Fixtures.Paragraph("Texte du dessin : 0")+"</w:txbxContent><x:pctWidth xmlns:x=\"urn:render-test\">Texte inconnu conservé</x:pctWidth></w:drawing></w:r></w:p>";
        string path=Fixtures.Write(root,"drawing-geometry.docx",Fixtures.Parts(drawing));var input=DocumentInspector.InspectDocument(path);Check(input.Manifest.InspectionComplete,"Synthetic geometry inspection failed");
        Verify(input);
        string? real=Environment.GetEnvironmentVariable("LECTEUR_WORD_QA_INPUT");
        if(real is not null)
        {
            string hash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(real)));var actual=DocumentInspector.InspectDocument(real);Verify(actual);
            Check(!Composer.Compose(actual).Available,"Reading correction falsely qualified complete Stage pagination");
            Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(real)))==hash,"Real input modified");
            File.WriteAllText(Path.Combine(root,"real-geometry.json"),JsonSerializer.Serialize(ReadableReader.Read(actual).Issues.Where(i=>i.Code=="READ_DRAWING_GEOMETRY_METADATA"),new JsonSerializerOptions{WriteIndented=true}));
        }
        void Verify(InspectionResult source)
        {
            byte[] before=ManifestJson.Serialize(source.Manifest);var reading=ReadableReader.Read(source);Check(reading.Available,"Reading unavailable");
            var blocks=ReadablePresentation.Walk(reading.Blocks).ToArray();var parents=source.Manifest.Nodes.ToDictionary(n=>n.Id);
            var geometry=source.Manifest.Nodes.Where(n=>n.Kind=="textValue"&&n.ParentId is {} p&&parents[p].Name is "{"+WP+"}posOffset" or "{"+WP14+"}pctWidth" or "{"+WP14+"}pctHeight").ToArray();
            Check(geometry.Length>=3,"Geometry fixture not representative");
            Check(geometry.All(n=>blocks.All(b=>b.Id!=n.Id)),"DrawingML geometry leaked into reading text");
            Check(before.SequenceEqual(ManifestJson.Serialize(source.Manifest)),"Geometry or provenance removed from inventory");
            if(ReferenceEquals(source,input))
            {
                Check(reading.PlainText.Contains("Texte du dessin : 0",StringComparison.Ordinal),"Actual drawing text lost");
                Check(reading.PlainText.Contains("Texte inconnu conservé",StringComparison.Ordinal),"Unknown namespace silently filtered");
            }
        }
    }
}
