using System.Runtime.InteropServices;

namespace LecteurWord.Flow;

internal sealed unsafe class TextUnicode
{
    // ICU belongs to the host, as in .NET's Linux globalization. Only this profile requires it.
    static readonly Lazy<TextUnicode?> instance = new(Load);
    internal static TextUnicode Instance => instance.Value ?? throw new InvalidDataException("TEXT_ICU_UNAVAILABLE");

    readonly delegate* unmanaged[Cdecl]<nint> bidiOpen;
    readonly delegate* unmanaged[Cdecl]<nint, void> bidiClose;
    readonly delegate* unmanaged[Cdecl]<nint, char*, int, byte, byte*, int*, void> bidiSet;
    readonly delegate* unmanaged[Cdecl]<nint, int, int, nint, int*, void> bidiSetLine;
    readonly delegate* unmanaged[Cdecl]<nint, int*, byte*> bidiLevels;
    readonly delegate* unmanaged[Cdecl]<int, byte*, char*, int, int*, nint> breakOpen;
    readonly delegate* unmanaged[Cdecl]<nint, void> breakClose;
    readonly delegate* unmanaged[Cdecl]<nint, int> breakNext;
    readonly delegate* unmanaged[Cdecl]<nint, int> breakStatus;
    readonly delegate* unmanaged[Cdecl]<int, int*, int> getScript;
    readonly delegate* unmanaged[Cdecl]<int, nint> shortScript;
    readonly delegate* unmanaged[Cdecl]<int, int, byte> hasProperty;
    internal string Version { get; }
    internal string UnicodeVersion { get; }

    TextUnicode(nint library, int major)
    {
        nint Symbol(string name) => NativeLibrary.GetExport(library, name + "_" + major);
        bidiOpen = (delegate* unmanaged[Cdecl]<nint>)Symbol("ubidi_open");
        bidiClose = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("ubidi_close");
        bidiSet = (delegate* unmanaged[Cdecl]<nint, char*, int, byte, byte*, int*, void>)Symbol("ubidi_setPara");
        bidiSetLine = (delegate* unmanaged[Cdecl]<nint, int, int, nint, int*, void>)Symbol("ubidi_setLine");
        bidiLevels = (delegate* unmanaged[Cdecl]<nint, int*, byte*>)Symbol("ubidi_getLevels");
        breakOpen = (delegate* unmanaged[Cdecl]<int, byte*, char*, int, int*, nint>)Symbol("ubrk_open");
        breakClose = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("ubrk_close");
        breakNext = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("ubrk_next");
        breakStatus = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("ubrk_getRuleStatus");
        getScript = (delegate* unmanaged[Cdecl]<int, int*, int>)Symbol("uscript_getScript");
        shortScript = (delegate* unmanaged[Cdecl]<int, nint>)Symbol("uscript_getShortName");
        hasProperty = (delegate* unmanaged[Cdecl]<int, int, byte>)Symbol("u_hasBinaryProperty");
        Version = ReadVersion("u_getVersion");
        UnicodeVersion = ReadVersion("u_getUnicodeVersion");

        string ReadVersion(string name)
        {
            byte* data = stackalloc byte[4];
            ((delegate* unmanaged[Cdecl]<byte*, void>)Symbol(name))(data);
            return $"{data[0]}.{data[1]}.{data[2]}.{data[3]}";
        }
    }

    static TextUnicode? Load()
    {
        if (!OperatingSystem.IsLinux()) return null;
        for (int version = 80; version >= 72; version--)
        {
            if (!NativeLibrary.TryLoad("libicuuc.so." + version, out nint library)) continue;
            try { return new(library, version); }
            catch (EntryPointNotFoundException) { NativeLibrary.Free(library); }
        }
        return null;
    }

    internal bool IsIgnorable(int scalar) => hasProperty(scalar, 5 /* UCHAR_DEFAULT_IGNORABLE_CODE_POINT */) != 0;

    internal string Script(int scalar)
    {
        int status = 0;
        int script = getScript(scalar, &status);
        if (status > 0) throw new InvalidDataException("TEXT_ICU_SCRIPT");
        return Marshal.PtrToStringAnsi(shortScript(script)) ?? "Zzzz";
    }

    internal byte[] Levels(string text, bool rightToLeft)
    {
        if (text.Length == 0) return [];
        nint bidi = bidiOpen();
        if (bidi == 0) throw new InvalidDataException("TEXT_ICU_BIDI");
        fixed (char* input = text)
        {
            try
            {
                int status = 0;
                bidiSet(bidi, input, text.Length, rightToLeft ? (byte)1 : (byte)0, null, &status);
                if (status > 0) throw new InvalidDataException("TEXT_ICU_BIDI");
                byte* levels = bidiLevels(bidi, &status);
                if (status > 0 || levels is null) throw new InvalidDataException("TEXT_ICU_BIDI");
                return new ReadOnlySpan<byte>(levels, text.Length).ToArray();
            }
            finally { bidiClose(bidi); }
        }
    }

    internal BidiLines OpenLines(string text,bool rightToLeft)=>new(this,text,rightToLeft);

    internal sealed class BidiLines : IDisposable
    {
        readonly TextUnicode api;
        GCHandle pinned;
        nint paragraph,line;

        internal BidiLines(TextUnicode api,string text,bool rightToLeft)
        {
            this.api=api;
            try
            {
                paragraph=api.bidiOpen();
                line=api.bidiOpen();
                if(paragraph==0||line==0)throw new InvalidDataException("LINE_ICU_BIDI");
                pinned=GCHandle.Alloc(text,GCHandleType.Pinned);
                int status=0;
                api.bidiSet(paragraph,(char*)pinned.AddrOfPinnedObject(),text.Length,rightToLeft?(byte)1:(byte)0,null,&status);
                if(status>0)throw new InvalidDataException("LINE_ICU_BIDI");
            }
            catch { Dispose(); throw; }
        }

        internal byte[] Levels(int start,int end)
        {
            if(start==end)return [];
            int status=0;
            api.bidiSetLine(paragraph,start,end,line,&status);
            if(status>0)throw new InvalidDataException("LINE_ICU_RANGE");
            byte* pointer=api.bidiLevels(line,&status);
            if(status>0||pointer is null)throw new InvalidDataException("LINE_ICU_BIDI");
            return new ReadOnlySpan<byte>(pointer,end-start).ToArray();
        }

        public void Dispose()
        {
            if(line!=0){api.bidiClose(line);line=0;}
            if(paragraph!=0){api.bidiClose(paragraph);paragraph=0;}
            if(pinned.IsAllocated)pinned.Free();
        }
    }

    internal List<(int Offset, bool Mandatory)> Boundaries(string text, bool graph, string language, Action check)
    {
        var result = new List<(int, bool)>();
        if (text.Length == 0) return result;
        fixed (char* input = text)
        fixed (byte* locale = System.Text.Encoding.ASCII.GetBytes((language is "" or "und" ? "root" : language) + "\0"))
        {
            int status = 0;
            nint iterator = breakOpen(graph ? 0 : 2, locale, input, text.Length, &status);
            try
            {
                if (status > 0 || iterator == 0) throw new InvalidDataException("TEXT_ICU_BREAK");
                int previous = 0;
                for (int end = breakNext(iterator); end != -1; end = breakNext(iterator))
                {
                    check();
                    if (end <= previous || end > text.Length ||
                        end < text.Length && char.IsLowSurrogate(text[end]))
                        throw new InvalidDataException("TEXT_ICU_OFFSET");
                    result.Add((end, !graph && breakStatus(iterator) >= 100));
                    previous = end;
                }
                if (previous != text.Length) throw new InvalidDataException("TEXT_ICU_BREAK");
            }
            finally { if (iterator != 0) breakClose(iterator); }
        }
        return result;
    }
}
