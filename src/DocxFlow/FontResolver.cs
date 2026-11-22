using System.Security.Cryptography;
using SkiaSharp;
using LecteurWord.DocxCore;

namespace LecteurWord.Flow;

public static class FontResolver
{
    static readonly Lazy<HashSet<string>> installed=new(()=>SKFontManager.Default.GetFontFamilies().ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static bool IsInstalled(string family)=>family.Length is >0 and <=128&&installed.Value.Contains(family);
    internal static SKTypeface Open(FontResolution resolution)
    {
        var source=resolution.Data??throw new InvalidDataException("LAYOUT_FONT_SNAPSHOT_MISSING");
        using var data=SKData.CreateCopy(source.Bytes);var face=SKTypeface.FromData(data,source.Index)??throw new InvalidDataException("LAYOUT_FONT_INVALID");
        if(face.FamilyName!=resolution.UsedFamily||face.FontWeight!=resolution.Weight||face.FontSlant.ToString()!=resolution.Slant){face.Dispose();throw new InvalidDataException("LAYOUT_FONT_IDENTITY_MISMATCH");}
        return face;
    }
    public static FontResolution Probe(LayoutStyle style)
    {
        var resolution=style.Font??throw new InvalidDataException("LAYOUT_FONT_UNRESOLVED");
        using var face=Open(resolution);return resolution with{UsedFamily=face.FamilyName,Weight=face.FontWeight,Slant=face.FontSlant.ToString()};
    }
    public static PaginatedDocument Resolve(PaginatedDocument document,CancellationToken token=default)
    {
        if(document.Fonts.ResolutionAttempted||document.Paragraphs.Count==0)return document;
        var issues=document.Issues.ToList();var uses=new List<FontUse>();var keyIssues=new HashSet<string>();
        var timer=System.Diagnostics.Stopwatch.StartNew();
        void CheckBudget(){token.ThrowIfCancellationRequested();if(timer.ElapsedMilliseconds>15000)throw new InvalidDataException("LAYOUT_FONT_TIME_LIMIT");}
        void Issue(string code,SourceLocation? source,string text,bool blocks=false)
        {if(keyIssues.Add(code+":"+source?.PartUri+":"+source?.Path+":"+text))issues.Add(new(code,source,text,blocks));}
        using var faces=new FaceSelection(document.Fonts,CheckBudget);
        var paragraphs=new List<LayoutParagraph>();
        try
        {
            foreach(var p in document.Paragraphs)
            {
                CheckBudget();var runs=new List<LayoutRun>();
                foreach(var run in p.Runs)
                {
                    CheckBudget();var slots=run.Style.FontSlots;
                    if(slots.Hint is not ("" or "default"))Issue("LAYOUT_FONT_HINT_UNQUALIFIED",run.Source,"Sélection linguistique de police hors profil : "+slots.Hint,true);
                    int offset=0;
                    do
                    {
                        if(uses.Count>=50000)throw new InvalidDataException("LAYOUT_FONT_USE_LIMIT");
                        int end=offset;bool ascii=offset>=run.Text.Length||run.Text[offset]<128;
                        while(end<run.Text.Length&&(run.Text[end]<128)==ascii)end++;
                        string text=run.Text[offset..end],slot=ascii?"ascii":"hAnsi";
                        var request=ascii?slots.Ascii:slots.HAnsi;
                        bool hasSlots=slots.Ascii is not null||slots.HAnsi is not null||slots.EastAsia is not null||slots.Cs is not null;
                        string family=request?.Value??(hasSlots?"DejaVu Serif":run.Style.RequestedFont);bool missing=false;
                        if(request is null&&hasSlots)Issue("LAYOUT_DEFAULT_FONT_SLOT",run.Source,"Police du slot "+slot+" absente de la cascade : valeur de lecture DejaVu Serif, sans emprunter silencieusement la police d’un autre slot.");
                        if(request?.Theme==true){string? themed=document.Fonts.ThemeFamily(request.Value);missing=themed is null;family=themed??("thème:"+request.Value);}
                        if(family.Length is 0 or >128){Issue("LAYOUT_FONT_NAME_LIMIT",run.Source,"Nom de police vide ou hors limites.",true);family="DejaVu Serif";}
                        bool qualified=Composer.SupportedText(text)&&(!text.Contains('\t')||run.Kind=="tab")&&!text.Contains('\r');
                        FontResolution? resolved=null;
                        if(qualified)resolved=faces.Select(family,run.Style.Bold,run.Style.Italic,text.Replace("\n","",StringComparison.Ordinal).Replace("\t","",StringComparison.Ordinal),missing);
                        else Issue("LAYOUT_SCRIPT_OR_CONTROL_UNQUALIFIED",run.Source,"Écriture ou contrôle hors profil typographique ; texte conservé en lecture simplifiée.",true);
                        var style=run.Style with{RequestedFont=family,Font=resolved};runs.Add(run with{Text=text,Style=style,Offset=run.Offset+offset});
                        uses.Add(new(run.Id,run.Source,slot,request,resolved,qualified?"resolved":"unqualified-script"));
                        if(resolved?.Status=="substituted")Issue("LAYOUT_FONT_SUBSTITUTION",run.Source,"Police « "+family+" » → « "+resolved.UsedFamily+" » ("+resolved.Weight+"/"+resolved.Slant+"). "+resolved.Reason+" Les métriques peuvent différer.");
                        offset=end;
                    }while(offset<run.Text.Length);
                }
                paragraphs.Add(p with{Runs=runs});
            }
        }
        catch(InvalidDataException e){Issue(e.Message,null,"Résolution de police refusée : budget, couverture ou identité de face non qualifiés.",true);}
        foreach(var embedded in document.Fonts.Embedded.Where(e=>e.Status!="usable"))Issue("LAYOUT_EMBEDDED_FONT_REFUSED",embedded.Source,embedded.Family+" : "+embedded.Detail);
        return new(paragraphs,issues){Tables=document.Tables,Fonts=document.Fonts with{Uses=uses,ResolutionAttempted=true}};
    }
    internal sealed class FaceSelection(DocumentFonts fonts,Action check):IDisposable
    {
        DocumentFonts documentFonts = fonts;
        readonly Dictionary<(string Family,bool Bold,bool Italic),FontResolution?> cache=new();
        long bytesTotal;
        FontResolution? Installed(string family,bool bold,bool italic)
        {
            var key=(family,bold,italic);if(cache.TryGetValue(key,out var value))return value;
            check();if(cache.Count>=64)throw new InvalidDataException("LAYOUT_FONT_FACE_LIMIT");
            if(!IsInstalled(family))return cache[key]=null;
            using var requested=new SKFontStyle(bold?700:400,5,italic?SKFontStyleSlant.Italic:SKFontStyleSlant.Upright);
            using var face=SKFontManager.Default.MatchFamily(family,requested);
            if(face is null||!FaceMatches(face,bold,italic))return cache[key]=null;
            using var stream=face.OpenStream(out int index);if(stream is null||stream.Length is <=0 or >8*1024*1024)return cache[key]=null;
            byte[] bytes=new byte[stream.Length];int read=stream.Read(bytes,bytes.Length);if(read!=bytes.Length)return cache[key]=null;
            using var data=SKData.CreateCopy(bytes);using var frozen=SKTypeface.FromData(data,index);
            // Variable/collection instances must survive the snapshot without silently changing face.
            if(frozen is null||frozen.FamilyName!=face.FamilyName||frozen.FontWeight!=face.FontWeight||frozen.FontSlant!=face.FontSlant)return cache[key]=null;
            bytesTotal+=bytes.Length;if(bytesTotal>32*1024*1024)throw new InvalidDataException("LAYOUT_FONT_BYTES_LIMIT");
            string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            return cache[key]=new(family,frozen.FamilyName,"installed",true,frozen.FontWeight,frozen.FontSlant.ToString(),hash,index,"checked","Face Linux chargée et figée pour ce document."){Data=new(bytes,index,hash)};
        }
        internal FontResolution Bundled(bool bold,bool italic)
        {
            string key="@bundled";if(cache.TryGetValue((key,bold,italic),out var cached))return cached!;
            string suffix=bold&&italic?"-BoldItalic":bold?"-Bold":italic?"-Italic":"";
            using var stream=typeof(Composer).Assembly.GetManifestResourceStream("LecteurWord.Fonts.DejaVuSerif"+suffix+".ttf")??throw new InvalidDataException("LAYOUT_FONT_MISSING");
            using var memory=new MemoryStream();stream.CopyTo(memory);byte[] bytes=memory.ToArray();string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var data=SKData.CreateCopy(bytes);using var face=SKTypeface.FromData(data)??throw new InvalidDataException("LAYOUT_FONT_INVALID");
            return cache[(key,bold,italic)]=new("DejaVu Serif",face.FamilyName,"bundled",IsInstalled("DejaVu Serif"),face.FontWeight,face.FontSlant.ToString(),hash,0,"checked","Face libre embarquée dans Lecteur Word."){Data=new(bytes,0,hash)};
        }
        readonly Dictionary<(string Hash,int Index),SKFont> coverageFonts=new Dictionary<(string Hash,int Index),SKFont>();
        readonly List<SKTypeface> coverageFaces=new List<SKTypeface>();
        bool Covers(FontResolution font,string text,Func<int,bool>? ignorable,bool extended)
        {
            var key=(font.Sha256,font.FaceIndex);
            if(!coverageFonts.TryGetValue(key,out var skfont)){var face=Open(font);coverageFaces.Add(face);coverageFonts[key]=skfont=new SKFont(face,12);}
            if (!extended) return skfont.ContainsGlyphs(text);
            if (skfont.Typeface.GetTableSize(0x66766172) > 0) return false; // fvar: default instance is not qualified here.
            foreach (var scalar in text.EnumerateRunes())
                if (ignorable?.Invoke(scalar.Value) != true && skfont.GetGlyph(scalar.Value) == 0) return false;
            return true;
        }
        internal FontResolution Select(string family,bool bold,bool italic,string text,bool themeMissing,
            bool extended=false,Func<int,bool>? ignorable=null,string? language=null)
        {
            check();
            bool CoversText(FontResolution resolution) => Covers(resolution,text,ignorable,extended);
            var candidate=themeMissing?null:Installed(family,bold,italic);
            if(candidate is not null&&CoversText(candidate))return candidate with{RequestedFamily=family};
            if(!themeMissing)
                foreach(var face in EmbeddedFaces(family,bold,italic))
                    if(CoversText(face))return face with{RequestedFamily=family};
            string reason=themeMissing?"Référence de thème non résolue.":candidate is not null?"La face demandée ne couvre pas les caractères du segment.":IsInstalled(family)?"La face demandée n’est pas exploitable sans transformation silencieuse.":"Famille demandée absente de Linux.";
            string kind=documentFonts.Declarations.FirstOrDefault(d=>d.Family.Equals(family,StringComparison.OrdinalIgnoreCase))?.Classification??"";
            string[] fallbacks=family.Contains("Mono",StringComparison.OrdinalIgnoreCase)||family.Contains("Courier",StringComparison.OrdinalIgnoreCase)||kind=="modern"?["Liberation Mono","DejaVu Sans Mono"]:family.Contains("Arial",StringComparison.OrdinalIgnoreCase)||family.Contains("Sans",StringComparison.OrdinalIgnoreCase)||family=="Calibri"||kind=="swiss"?["Carlito","Liberation Sans","DejaVu Sans"]:["Liberation Serif","DejaVu Serif","Noto Serif"];
            foreach(string fallback in fallbacks)
            {
                var face=Installed(fallback,bold,italic);if(face is not null&&CoversText(face))return face with{RequestedFamily=family,Status="substituted",Installed=IsInstalled(family),Reason=reason};
            }
            if(text.Length>0&&(extended||Composer.SupportedText(text)))
            {
                using var requested=new SKFontStyle(bold?700:400,5,italic?SKFontStyleSlant.Italic:SKFontStyleSlant.Upright);
                var languages=language is null or "und"?Array.Empty<string>():new[]{language};
                var seen=new HashSet<int>();
                foreach(var scalar in text.EnumerateRunes())
                {
                    if(ignorable?.Invoke(scalar.Value)==true||!seen.Add(scalar.Value))continue;
                    check();
                    using var matched=SKFontManager.Default.MatchCharacter(null,requested,languages,scalar.Value);
                    if(matched is not null)
                    {
                        var face=Installed(matched.FamilyName,bold,italic);
                        if(face is not null&&CoversText(face))return face with{RequestedFamily=family,Status="substituted",Installed=IsInstalled(family),Reason=reason};
                    }
                    if(!extended||seen.Count>=8)break;
                }
            }
            var bundled=Bundled(bold,italic);
            if(CoversText(bundled))return bundled with{RequestedFamily=family,Status="substituted",Installed=IsInstalled(family),Reason=reason};
            throw new InvalidDataException("LAYOUT_GLYPH_MISSING");
        }

        internal IEnumerable<FontResolution> ExactFaces(string family,bool bold,bool italic)
        {
            if(ExactInstalled(family,bold,italic) is {} installed)yield return installed;
            foreach(var face in EmbeddedFaces(family,bold,italic))yield return face;
        }

        IEnumerable<FontResolution> EmbeddedFaces(string family,bool bold,bool italic)
        {
            string variant=bold&&italic?"BoldItalic":bold?"Bold":italic?"Italic":"Regular";
            foreach(var e in documentFonts.Embedded.Where(e=>e.Family.Equals(family,StringComparison.OrdinalIgnoreCase)&&e.Face==variant&&e.Data is not null))
            {
                check();
                using var data=SKData.CreateCopy(e.Data!.Bytes);using var face=SKTypeface.FromData(data);
                if(face is not null&&FaceMatches(face,bold,italic))
                    yield return new(family,face.FamilyName,"embedded",IsInstalled(family),face.FontWeight,face.FontSlant.ToString(),
                        e.Data.Hash,0,"checked",e.Rights+" ; usage mémoire temporaire pour ce document."){Data=e.Data};
            }
        }

        internal FontResolution? ExactInstalled(string family,bool bold,bool italic)
        {
            var face=Installed(family,bold,italic);
            return face?.UsedFamily.Equals(family,StringComparison.OrdinalIgnoreCase)==true?face:null;
        }

        public void Dispose()
        {
            foreach(var font in coverageFonts.Values)font.Dispose();
            foreach(var face in coverageFaces)face.Dispose();
            coverageFonts.Clear();
            coverageFaces.Clear();
            cache.Clear();
            documentFonts = new();
        }
    }
    static bool FaceMatches(SKTypeface face,bool bold,bool italic)=>
        (face.FontWeight>=600)==bold&&(face.FontSlant!=SKFontStyleSlant.Upright)==italic;
}
