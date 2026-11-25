using System.Globalization;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static partial class LayoutReader
{
    sealed partial class Reader
    {
        const string VmlName="{"+Vml+"}";
        const string Office="{urn:schemas-microsoft-com:office:office}";
        const string RelationId="{"+R+"}id";
        const string OleRelation=R+"/oleObject";
        const string OleContentType="application/vnd.openxmlformats-officedocument.oleObject";
        readonly HashSet<(string Part,string Id)> renderedOlePreviews=[];
        ILookup<(string Part,string Id),InventoryNode>? vmlShapes;
        static readonly string[] PictureFrameFormulas=[
            "if lineDrawn pixelLineWidth 0","sum @0 1 0","sum 0 0 @1","prod @2 1 2",
            "prod @3 21600 pixelWidth","prod @3 21600 pixelHeight","sum @0 0 1","prod @6 1 2",
            "prod @7 21600 pixelWidth","sum @8 21600 0","prod @7 21600 pixelHeight","sum @10 21600 0"
        ];

        InlineImage? ReadLegacyPicture(InventoryNode picture)
        {
            if(Is(picture,"pict")&&(ReadInlineVmlGroup(picture)??ReadInlineVmlShape(picture)) is {} vector)return vector;
            int before=issues.Count;
            bool embedded=Is(picture,"object");
            var members=Elements(picture).Take(4).ToArray();
            InventoryNode? declaration=members.FirstOrDefault()?.Name==VmlName+"shapetype"?members[0]:null;
            var graphics=declaration is null?members:members[1..];
            InventoryNode? ole=embedded&&graphics.Length==2&&graphics[1].Name==Office+"OLEObject"?graphics[1]:null;
            if(embedded?ole is null:graphics.Length!=1)
                return Refused(picture,"La représentation VML ne déclare pas un rectangle image intégré pris en charge.");
            var shape=graphics[0];
            InventoryNode? frame=ResolvePictureFrame(shape);
            if(frame is null&&(!IsImageRectangle(shape)||shape.Attributes.ContainsKey("{}type"))||
                declaration is not null&&declaration.Id!=frame?.Id)
                return Refused(shape,"Modèle VML non résolu ou géométrie d’image hors profil.");
            if(Elements(shape).Take(2).ToArray() is not [var reference]||reference.Name!=VmlName+"imagedata"||
                Elements(reference).Any()||Descendants(picture).Any(n=>n.Kind=="textValue"&&!string.IsNullOrWhiteSpace(n.Text)))
                return Refused(shape,"Le rectangle doit contenir un seul aperçu image.");
            if(embedded)
            {
                Attributes(picture,"{"+W+"}dxaOrig","{"+W+"}dyaOrig","{http://schemas.microsoft.com/office/word/2010/wordml}anchorId");
                string x=A(picture,"dxaOrig"),y=A(picture,"dyaOrig");
                if((x.Length>0||y.Length>0)&&(!Positive(x)||!Positive(y)))
                    return Refused(picture,"Les dimensions d’origine de l’objet incorporé sont invalides.");
            }
            else WAttributes(picture);
            if(shape.Name==VmlName+"shape")
                Attributes(shape,"{}id","{}alt","{}style","{}filled","{}stroked","{}path","{}coordsize","{}type",Office+"ole");
            else Attributes(shape,"{}id","{}alt","{}style","{}filled","{}stroked",Office+"ole");
            Attributes(reference,RelationId,Office+"title");
            if(!Disabled(shape,"filled",frame)||!Disabled(shape,"stroked",frame)||
                !TryReadInlineBox(shape.Attributes.GetValueOrDefault("{}style"),out float width,out float height,out bool flipX,out bool flipY))
                return Refused(shape,"Boîte VML, remplissage, trait ou transformation hors profil.");

            string? oleId=null;
            if(ole is not null)
            {
                Attributes(ole,"{}Type","{}ProgID","{}ShapeID","{}DrawAspect","{}ObjectID",RelationId);
                oleId=ole.Attributes.GetValueOrDefault(RelationId);
                string? shapeId=shape.Attributes.GetValueOrDefault("{}id");
                var link=input.Manifest.Relationships.SingleOrDefault(r=>r.SourcePart==picture.Source.PartUri&&r.Id==oleId);
                if(Elements(ole).Any()||string.IsNullOrEmpty(shapeId)||shapeId!=ole.Attributes.GetValueOrDefault("{}ShapeID")||
                    !UniqueShapeId(shape,shapeId)||
                    ole.Attributes.GetValueOrDefault("{}Type")!="Embed"||ole.Attributes.GetValueOrDefault("{}DrawAspect")!="Content"||
                    shape.Attributes.GetValueOrDefault(Office+"ole") is not (null or "" or "t" or "true")||
                    link is not {Type:OleRelation,Mode:"Internal",ResolvedPart:{} target}||
                    !input.Manifest.Parts.Any(p=>p.PartUri==target&&p.ContentType==OleContentType))
                    return Refused(ole,"La relation interne entre l’objet incorporé et son rectangle d’aperçu est indéterminée.");
            }
            else if(shape.Attributes.ContainsKey(Office+"ole"))
                return Refused(shape,"Marque d’objet incorporé sans déclaration correspondante.");

            var image=ReadImageResource(picture,reference,reference.Attributes.GetValueOrDefault(RelationId),width,height,before);
            if(image is not null&&oleId is not null)
            {
                renderedOlePreviews.Add((picture.Source.PartUri,oleId));
                Issue("PAGE_OLE_PASSIVE_PREVIEW",picture,"Aperçu passif de l’objet incorporé. La relation d’origine reste inventoriée.",false);
            }
            return image is null?null:image with{FlipX=flipX,FlipY=flipY};

            InlineImage? Refused(InventoryNode source,string reason)
            {
                Issue("PAGE_VML_PICTURE_UNQUALIFIED",source,reason);
                return null;
            }
            static bool Disabled(InventoryNode node,string attribute,InventoryNode? template)=>
                (node.Attributes.GetValueOrDefault("{}"+attribute)??template?.Attributes.GetValueOrDefault("{}"+attribute)) is "f" or "false" or "0";
            static bool Positive(string raw)=>int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out int n)&&n>0;
            static bool IsImageRectangle(InventoryNode node)=>node.Name==VmlName+"rect"||node.Name==VmlName+"shape"&&
                node.Attributes.GetValueOrDefault("{}coordsize")=="21600,21600"&&
                node.Attributes.GetValueOrDefault("{}path")=="m0,0l0,21600,21600,21600,21600,0xe";
        }

        InventoryNode? ResolvePictureFrame(InventoryNode shape)
        {
            if(shape.Name!=VmlName+"shape"||shape.Attributes.GetValueOrDefault("{}type") is not {Length:>1 and <=129} type||
                type[0]!='#'||shape.Attributes.ContainsKey("{}path")||shape.Attributes.ContainsKey("{}coordsize")||
                VmlDefinition(shape.Source.PartUri,type[1..]) is not {} template||template.Name!=VmlName+"shapetype"||
                semantic!.Element(template.Id).Order>=semantic.Element(shape.Id).Order)return null;
            // ST_ShapeType 75 is a picture frame. Verify the actual path and formulas before using the raster rectangle.
            if(!AttributesMatch(template,("{}id",type[1..]),("{}coordsize","21600,21600"),(Office+"spt","75"),
                (Office+"preferrelative","t"),("{}path","m@4@5l@4@11@9@11@9@5xe"),("{}filled","f"),("{}stroked","f"))||
                Elements(template).Take(5).ToArray() is not [var stroke,var formulas,var path,var drawingLock]||
                stroke.Name!=VmlName+"stroke"||!Leaf(stroke,("{}joinstyle","miter"))||
                formulas.Name!=VmlName+"formulas"||!AttributesMatch(formulas)||
                path.Name!=VmlName+"path"||!Leaf(path,(Office+"extrusionok","f"),("{}gradientshapeok","t"),(Office+"connecttype","rect"))||
                drawingLock.Name!=Office+"lock"||!Leaf(drawingLock,(VmlName+"ext","edit"),("{}aspectratio","t")))return null;
            var equations=Elements(formulas).Take(PictureFrameFormulas.Length+1).ToArray();
            if(equations.Length!=PictureFrameFormulas.Length)return null;
            for(int i=0;i<equations.Length;i++)
                if(equations[i].Name!=VmlName+"f"||!Leaf(equations[i],("{}eqn",PictureFrameFormulas[i])))return null;
            return template;

            bool Leaf(InventoryNode node,params (string Name,string Value)[] expected)=>
                !Elements(node).Any()&&AttributesMatch(node,expected);
            static bool AttributesMatch(InventoryNode node,params (string Name,string Value)[] expected)=>
                node.Attributes.Keys.Count(name=>!name.StartsWith("{http://www.w3.org/2000/xmlns/}",StringComparison.Ordinal))==expected.Length&&
                expected.All(a=>node.Attributes.GetValueOrDefault(a.Name)==a.Value);
        }

        bool UniqueShapeId(InventoryNode shape,string id)=>VmlDefinition(shape.Source.PartUri,id)?.Id==shape.Id;

        InventoryNode? VmlDefinition(string part,string id)
        {
            vmlShapes??=input.Manifest.Nodes.Where(n=>n.Name.StartsWith(VmlName,StringComparison.Ordinal)&&
                    n.Attributes.ContainsKey("{}id")&&semantic!.Element(n.Id).Coverage!=SemanticCoverage.InactiveAlternative)
                .ToLookup(n=>(n.Source.PartUri,n.Attributes["{}id"]));
            return SemanticIndex.Unique(vmlShapes[(part,id)]);
        }

        static bool TryReadInlineBox(string? style,out float width,out float height,out bool flipX,out bool flipY)
        {
            width=height=0;
            flipX=flipY=false;
            if(style is null||style.Length>160)return false;
            var seen=new HashSet<string>(StringComparer.Ordinal);
            bool halfTurn=false;
            foreach(string entry in style.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
            {
                var property=entry.Split(':',2,StringSplitOptions.TrimEntries);
                if(property.Length!=2||!seen.Add(property[0]))return false;
                string value=property[1];
                switch(property[0])
                {
                    case "width": if(!Length(value,out width))return false;break;
                    case "height": if(!Length(value,out height))return false;break;
                    case "rotation" when value is "0" or "180" or "-180": halfTurn=value!="0";break;
                    case "flip" when value is "x" or "y" or "x y" or "y x":
                        flipX=value.Contains('x');
                        flipY=value.Contains('y');
                        break;
                    default: return false;
                }
            }
            flipX^=halfTurn;
            flipY^=halfTurn;
            return width>0&&height>0;

            static bool Length(string raw,out float points)
            {
                points=0;
                if(raw.Length<3)return false;
                float scale=raw[^2..] switch{"pt"=>1,"in"=>72,"cm"=>72/2.54f,"mm"=>72/25.4f,_=>0};
                if(scale==0||!float.TryParse(raw.AsSpan(0,raw.Length-2),NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,out float number))return false;
                points=number*scale;
                return points is >=.01f and <=1440;
            }
        }
    }
}
