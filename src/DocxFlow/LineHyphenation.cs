namespace LecteurWord.Flow;

internal sealed partial class LineComposer
{
    IEnumerable<int> HyphenPoints(TextParagraph paragraph,ParagraphAtoms data,LineHyphenation options,
        int first,int overflow,Action<FlowIssue> issue)
    {
        var atoms=data.Items;
        int start=atoms[first].Start;
        int end=atoms[overflow-1].End;
        string text=paragraph.Context;
        while(start<end&&(char.IsWhiteSpace(text[start])||char.GetUnicodeCategory(text[start]) is
            System.Globalization.UnicodeCategory.OpenPunctuation or System.Globalization.UnicodeCategory.InitialQuotePunctuation))
        {Check();start++;}
        int wordStart=start;
        while(wordStart>0&&char.IsLetter(text[wordStart-1]))
        {Check();wordStart--;}
        int wordEnd=start;
        bool caps=true;
        while(wordEnd<text.Length&&char.IsLetter(text[wordEnd]))
        {
            Check();
            if(wordEnd-wordStart==HyphenationPatterns.MaxWordLength)
            {
                Report("LINE_HYPHENATION_WORD_UNQUALIFIED","Le mot dépasse le profil de césure borné.");
                yield break;
            }
            wordEnd++;
        }
        for(int i=wordStart;i<wordEnd;i++)
        {Check();caps&=char.IsUpper(text[i]);}
        // Controls and Unicode cuts retain priority. Compounds and non-Latin scripts are
        // reserved for linguistically qualified profiles.
        if(wordEnd-wordStart<4||options.SuppressCaps&&caps)yield break;
        if(wordEnd<text.Length&&(text[wordEnd] is '\u00ad' or '\u2011' or '\u2019' or '\'' or '-' or '\u2010' or '\ufffc'||
            char.GetUnicodeCategory(text[wordEnd])==System.Globalization.UnicodeCategory.NonSpacingMark))
        {
            Report("LINE_HYPHENATION_WORD_UNQUALIFIED","Le traitement automatique de ce mot avec une coupure ou un contrôle explicite reste hors profil.");
            yield break;
        }
        if(start>0&&(text[start-1] is '\u00ad' or '\u2011' or '\u2019' or '\'' or '-' or '\u2010')&&
            start>1&&char.IsLetter(text[start-2]))yield break;

        var safeCuts=new Dictionary<int,int>();
        string? language=null;
        string? reason=null;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        int wordAtom=first;
        while(wordAtom>0&&atoms[wordAtom-1].End>wordStart)
        {Check();wordAtom--;}
        for(int i=wordAtom;i<atoms.Length;i++)
        {
            Check();
            var atom=atoms[i];
            if(atom.End<=wordStart)continue;
            if(atom.Start>=wordEnd)break;
            if(atom.Fragment<0||atom.Width is null)
            {reason="Contexte de mot incomplet.";break;}
            var fragment=paragraph.Fragments[atom.Fragment];
            if(fragment.Language is "" or "und")
            {reason="Aucune langue déclarée pour ce mot.";break;}
            if(language is not null&&!string.Equals(language,fragment.Language,StringComparison.OrdinalIgnoreCase))
            {reason="Plusieurs langues déclarées dans le même mot.";break;}
            if(fragment.Script!="Latn"||fragment.BidiLevel%2!=0)
            {reason="Écriture ou direction du mot hors profil de césure.";break;}
            language=fragment.Language;
            if(i>=first&&i<overflow&&atom.Start>=start&&atom.End<wordEnd&&i+1<atoms.Length&&SafeBefore(paragraph,atoms[i+1]))
                safeCuts.Add(atom.End-wordStart,i+1);
        }
        languageTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
        if(options.CompatibilityLimitation is {} compatibility)
        {
            issue(new("LINE_HYPHENATION_COMPATIBILITY",model.Source(compatibility).Location,
                "Mode de compatibilité Word hors profil pour cette décision de césure.",false));
            yield break;
        }
        if(reason is not null||language is null)
        {
            Report("LINE_HYPHENATION_LANGUAGE_UNRESOLVED",reason??"Aucune langue déterminée pour ce mot.");
            yield break;
        }
        if(!hyphenation.TryGet(language,text[wordStart..wordEnd],Check,out int[] cuts))
        {
            Report("LINE_HYPHENATION_RESOURCE_UNAVAILABLE","Ressource locale de césure compatible indisponible pour "+language+".");
            yield break;
        }
        for(int i=cuts.Length-1;i>=0;i--)
            if(safeCuts.TryGetValue(cuts[i],out int boundary))yield return boundary;

        void Report(string code,string message)
        {
            int i=first;
            while(i+1<atoms.Length&&atoms[i].End<=start){Check();i++;}
            var atom=atoms[i];
            var origin=atom.Fragment>=0?SourceOf(paragraph.Fragments[atom.Fragment],
                start-paragraph.Fragments[atom.Fragment].ContextOffset):model.Source(paragraph.Id);
            issue(new(code,origin.Location,message,false));
        }
    }

    static bool SafeBefore(TextParagraph paragraph,Atom atom)
    {
        if(atom.Fragment<0)return false;
        var fragment=paragraph.Fragments[atom.Fragment];
        int start=atom.Start-fragment.ContextOffset;
        if(fragment.Measure is not {} measure)return false;
        var clusters=measure.Clusters;
        int left=0,right=clusters.Length-1;
        // Cluster order in the source fragment is logical.
        while(left<=right)
        {
            int mid=left+(right-left)/2;
            var cluster=clusters[mid];
            if(cluster.Range.Start==start)return !cluster.UnsafeBefore;
            if(cluster.Range.Start<start)left=mid+1;
            else right=mid-1;
        }
        return false;
    }
}
