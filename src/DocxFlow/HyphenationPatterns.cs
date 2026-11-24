using System.Security.Cryptography;
using System.Text;

namespace LecteurWord.Flow;

/// <summary>Document-owned Liang pattern matching over the locally qualified dictionaries.</summary>
internal sealed class HyphenationPatterns
{
    internal const int MaxWordLength=128;
    const int MaxCachedWords=4096;
    readonly Dictionary<string,PatternSet?> languages=new(StringComparer.Ordinal);
    readonly Dictionary<(string Language,string Word),int[]> cache=[];

    internal bool TryGet(string language,string word,Action check,out int[] points)
    {
        points=[];
        string? profile=language.ToLowerInvariant() switch
        {
            "fr" or "fr-fr"=>"fr",
            "en-us"=>"en-US",
            _=>null
        };
        if(profile is null||word.Length>MaxWordLength||!word.IsNormalized())return false;
        if(!languages.TryGetValue(profile,out var patterns))
            languages.Add(profile,patterns=Load(profile,check));
        if(patterns is null)return false;
        string lower=word.ToLowerInvariant();
        var key=(profile,lower);
        if(cache.TryGetValue(key,out points!))return true;
        points=patterns.Match(lower,check);
        if(cache.Count<MaxCachedWords)cache.Add(key,points);
        return true;
    }

    static PatternSet? Load(string profile,Action check)
    {
        var (path,size,digest,left,right)=profile=="fr"
            ?("/usr/share/hyphen/hyph_fr.dic",23631,"476CA60B958400C8B3FBE55764B4900DF87ACF60394EF62362705D9C701DB191",2,2)
            :("/usr/share/texlive/texmf-dist/tex/generic/hyphen/hyphen.tex",27860,"2C18ACDC04C1A066AEB1759905E7CA449F0616C314B5ED6AEBE55B9D4A89B8D4",2,3);
        byte[] data=new byte[size];
        try
        {
            using var stream=File.OpenRead(path);
            if(stream.Length!=size)return null;
            stream.ReadExactly(data);
            if(stream.ReadByte()!=-1||Convert.ToHexString(SHA256.HashData(data))!=digest)return null;
        }
        catch(IOException){return null;}
        catch(UnauthorizedAccessException){return null;}
        // Pin the inputs so the parsed subset and language remain independently reproducible.
        using var reader=new StringReader(Encoding.UTF8.GetString(data));
        var result=new PatternSet(left,right);
        bool patterns=false,exceptions=false;
        while(reader.ReadLine() is {} row)
        {
            check();
            int comment=row.IndexOf('%');
            if(comment>=0)row=row[..comment];
            row=row.Trim();
            switch(row)
            {
                case "NEXTLEVEL":
                case @"\patterns{": patterns=true;continue;
                case @"\hyphenation{": exceptions=true;continue;
                case "}": patterns=exceptions=false;continue;
                case "":continue;
            }
            if(patterns)result.Add(row);
            else if(exceptions)result.AddException(row);
        }
        return result;
    }

    sealed class PatternSet(int left,int right)
    {
        sealed class Node
        {
            internal Dictionary<char,Node> Children {get;}=[];
            internal byte[]? Weights {get;set;}
        }
        readonly Node root=new();
        readonly Dictionary<string,int[]> exceptions=new(StringComparer.Ordinal);

        internal void AddException(string row)
        {
            var word=new StringBuilder();
            var points=new List<int>();
            foreach(char letter in row)
            {
                if(letter=='-')points.Add(word.Length);
                else word.Append(letter);
            }
            exceptions.Add(word.ToString(),points.Where(p=>p>=left&&p<=word.Length-right).ToArray());
        }

        internal void Add(string row)
        {
            var current=root;
            var weights=new List<byte>{0};
            foreach(char c in row)
            {
                if(char.IsAsciiDigit(c)){weights[^1]=(byte)(c-'0');continue;}
                if(!current.Children.TryGetValue(c,out var next))
                    current.Children.Add(c,next=new());
                current=next;
                weights.Add(0);
            }
            current.Weights=weights.ToArray();
        }

        internal int[] Match(string word,Action check)
        {
            if(exceptions.TryGetValue(word,out int[]? special))return special;
            string marked="."+word+".";
            var weights=new byte[marked.Length+1];
            for(int start=0;start<marked.Length;start++)
            {
                check();
                var node=root;
                for(int i=start;i<marked.Length;i++)
                {
                    if(!node.Children.TryGetValue(marked[i],out var next))break;
                    node=next;
                    if(node.Weights is not {} values)continue;
                    for(int j=0;j<values.Length;j++)weights[start+j]=Math.Max(weights[start+j],values[j]);
                }
            }
            var points=new List<int>();
            for(int i=left;i<=word.Length-right;i++)
                if((weights[i+1]&1)!=0)points.Add(i);
            return points.ToArray();
        }
    }
}
