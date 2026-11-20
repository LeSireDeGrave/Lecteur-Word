using System.Buffers.Binary;
using System.Security.Cryptography;
using LecteurWord.DocxCore;
using SkiaSharp;

namespace LecteurWord.Flow;

internal static class EmbeddedFonts
{
    public static EmbeddedFont Read(InspectionResult input,string family,InventoryNode node)
    {
        const string W="{http://schemas.openxmlformats.org/wordprocessingml/2006/main}",R="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        string face=node.Name[(node.Name.IndexOf('}')+1)..].Replace("embed","",StringComparison.Ordinal);
        string id=node.Attributes.GetValueOrDefault("{"+R+"}id")??"";
        var matches=input.Manifest.Relationships.Where(r=>r.SourcePart==node.Source.PartUri&&r.Id==id).ToArray();
        var relation=matches.Length==1?matches[0]:null;string? part=relation?.ResolvedPart;
        var notices=new Dictionary<int,string>();
        EmbeddedFont Result(string status,string detail,int? fs=null,string rights="indisponibles",string? hash=null)=>new(family,face,part,node.Source,status,rights,fs,hash,detail)
            {Copyright=notices.GetValueOrDefault(0)??"",LicenseNotice=notices.GetValueOrDefault(13)??"",LicenseUrl=notices.GetValueOrDefault(14)??""};
        if(relation?.Mode!="Internal"||relation.Type!=R+"/font"||part is null)return Result("refused","Relation de police absente, externe ou incompatible.");
        var info=input.Manifest.Parts.FirstOrDefault(p=>p.PartUri==part);
        if(info is null||!input.FontParts.TryGetValue(part,out var payload)||payload.Length>8*1024*1024||Convert.ToHexStringLower(SHA256.HashData(payload))!=info.Sha256)return Result("refused","Données absentes, hors budget ou empreinte incohérente.");
        byte[] bytes=payload.ToArray();
        if(info.ContentType=="application/vnd.openxmlformats-officedocument.obfuscatedFont")
        {
            string key=node.Attributes.GetValueOrDefault(W+"fontKey")??"";
            if(!Guid.TryParse(key,out var guid)||bytes.Length<32)return Result("refused","Clé d’obfuscation invalide.");
            byte[] mask=Convert.FromHexString(guid.ToString("N"));Array.Reverse(mask);for(int i=0;i<32;i++)bytes[i]^=mask[i%16];
        }
        else if(info.ContentType!="application/x-font-ttf")return Result("refused","Type de police incorporée hors profil.");
        if(bytes.Length<12||BinaryPrimitives.ReadUInt32BigEndian(bytes)!=0x00010000)return Result("refused","Seules les fontes TrueType statiques SFNT sont qualifiées (pas TTC, CFF, WOFF ou EOT).");
        int count=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));if(count is <1 or >128||12+16*count>bytes.Length)return Result("refused","Répertoire SFNT invalide.");
        var tables=new Dictionary<string,(int Offset,int Length)>();
        for(int i=0;i<count;i++)
        {
            int p=12+16*i;string tag=System.Text.Encoding.ASCII.GetString(bytes,p,4);uint offset=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+8)),length=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(p+12));
            if(offset<12+16*count||offset>(uint)bytes.Length||length>(uint)bytes.Length-offset||!tables.TryAdd(tag,((int)offset,(int)length)))return Result("refused","Bornes ou doublons de tables SFNT invalides.");
        }
        var spans=tables.Values.Where(t=>t.Length>0).OrderBy(t=>t.Offset).ToArray();for(int i=1;i<spans.Length;i++)if(spans[i-1].Offset+spans[i-1].Length>spans[i].Offset)return Result("refused","Tables SFNT superposées.");
        foreach(string tag in new[]{"head","hhea","maxp","hmtx","cmap","loca","glyf","name","OS/2"})if(!tables.ContainsKey(tag))return Result("refused","Table TrueType obligatoire absente : "+tag);
        if(new[]{"fvar","gvar","SVG ","COLR","CFF ","CFF2"}.Any(tables.ContainsKey))return Result("refused","Variations, fontes couleur ou contours spécialisés hors profil.");
        var os=tables["OS/2"];if(os.Length<10||tables["head"].Length<54||tables["hhea"].Length<36||tables["maxp"].Length<6)return Result("refused","Tables métriques incomplètes.");
        var names=tables["name"];if(names.Length<6)return Result("refused","Table de noms incomplète.");
        int records=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(names.Offset+2)),storage=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(names.Offset+4));
        if(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(names.Offset))>1||records>4096||6+12*records>names.Length||storage<6+12*records||storage>names.Length)return Result("refused","Bornes de la table de noms invalides.");
        var preferred=new HashSet<int>();
        for(int i=0;i<records;i++)
        {
            int p=names.Offset+6+i*12,platform=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p)),language=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p+4)),nameId=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p+6)),length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p+8)),offset=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p+10));
            if(storage+offset+length>names.Length)return Result("refused","Chaîne de nom hors limites.");
            if(nameId is not (0 or 13 or 14)||platform is not (0 or 3)||(length&1)!=0)continue;
            bool english=language is 0 or 0x409;if(notices.ContainsKey(nameId)&&(!english||preferred.Contains(nameId)))continue;
            notices[nameId]=System.Text.Encoding.BigEndianUnicode.GetString(bytes,names.Offset+storage+offset,length);if(english)preferred.Add(nameId);
        }
        int flags=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(os.Offset+8));int level=flags&15;
        if((flags&~0x010c)!=0||level is not (0 or 4 or 8))return Result("refused","Droits absents du profil autorisé : licence restreinte, bitmap seul, bits inconnus ou niveaux contradictoires.",flags);
        string rights=level==4?"aperçu et impression, lecture seule":level==8?"incorporation modifiable, usage temporaire":"incorporation installable, usage temporaire ici";
        if((flags&0x100)!=0&&node.Attributes.GetValueOrDefault(W+"subsetted") is "1" or "true" or "on")return Result("refused","Sous-ensemble interdit par fsType.",flags,rights);
        string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var data=SKData.CreateCopy(bytes);using var typeface=SKTypeface.FromData(data);
        if(typeface is null)return Result("refused","Skia refuse la face validée structurellement.",flags,rights,hash);
        string actual=typeface.FamilyName;string plain=actual.Length>7&&actual[6]=='+'?actual[7..]:actual;
        if(!string.Equals(plain,family,StringComparison.OrdinalIgnoreCase))return Result("refused","Le nom interne ne correspond pas à la déclaration DOCX.",flags,rights,hash);
        return Result("usable","Chargement mémoire limité au document ; aucune extraction, installation ou redistribution.",flags,rights,hash) with{Data=new(bytes,0,hash)};
    }
}
