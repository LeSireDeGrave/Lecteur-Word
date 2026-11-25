using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Platform;
using Avalonia.Styling;

namespace LecteurWord;

internal enum ReaderTheme { System, Chocolate, MicrosoftBlue, Night, Light }

internal sealed class ThemePreference(string path)
{
    internal string DirectoryPath=>Path.GetDirectoryName(path)!;
    internal static ThemePreference ForCurrentUser()
    {
        string? xdg=Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string config=xdg is not null&&Path.IsPathFullyQualified(xdg)?xdg:
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".config");
        return new(Path.Combine(config,"lecteur-word","settings.json"));
    }

    internal ReaderTheme Load()
    {
        var settings=Read();
        if(settings is null)return ReaderTheme.System;
        string key=ThemeKey(settings);
        var value=settings[key];
        if(value is not JsonValue scalar)return ReaderTheme.System;

        if(scalar.TryGetValue<string>(out var text))
        {
            return text.Trim().ToLowerInvariant() switch
            {
                "light" or "chocolate" or "clair"=>ReaderTheme.Chocolate,
                "blue" or "microsoftblue" or "microsoft blue" or "bleu microsoft"=>ReaderTheme.MicrosoftBlue,
                "night"=>ReaderTheme.Night,
                "daylight"=>ReaderTheme.Light,
                "dark" or "sombre" or "espresso" or "espresso & lin" or "espresso & linen"=>MigrateBlue(),
                _=>ReaderTheme.System
            };
        }

        if(scalar.TryGetValue<int>(out int previous))
            return previous switch {1=>ReaderTheme.Chocolate,2=>MigrateBlue(),_=>ReaderTheme.System};
        return ReaderTheme.System;

        ReaderTheme MigrateBlue()
        {
            _=Save(ReaderTheme.MicrosoftBlue);
            return ReaderTheme.MicrosoftBlue;
        }
    }

    internal bool Save(ReaderTheme theme)
    {
        string? temporary=null;
        try
        {
            if(new FileInfo(path).LinkTarget is not null)return false;
            var settings=Read();
            if(settings is null)return false;
            string canonical=theme switch
            {
                ReaderTheme.Chocolate=>"light",
                ReaderTheme.MicrosoftBlue=>"blue",
                ReaderTheme.Night=>"night",
                ReaderTheme.Light=>"daylight",
                _=>"system"
            };
            string key=ThemeKey(settings);
            if(settings[key] is JsonValue current&&current.TryGetValue<string>(out var existing)&&existing==canonical)return true;

            settings[key]=canonical;
            string directory=Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary=Path.Combine(directory,$".settings-{Guid.NewGuid():N}.tmp");
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var output=new FileStream(temporary,options))
            {
                JsonSerializer.Serialize(output,settings,new JsonSerializerOptions{WriteIndented=true});
                output.Flush(flushToDisk:true);
            }
            File.Move(temporary,path,overwrite:true);
            return true;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return false;}
        finally
        {
            if(temporary is not null)
                try{File.Delete(temporary);}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException){}
        }
    }

    JsonObject? Read()
    {
        try
        {
            using var stream=File.OpenRead(path);
            if(stream.Length>1024*1024)return null;
            var settings=JsonNode.Parse(stream) as JsonObject;
            _=settings?.Count;
            return settings;
        }
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){return new();}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return null;}
    }

    static string ThemeKey(JsonObject settings)=>
        settings.Select(p=>p.Key).FirstOrDefault(k=>k.Equals("theme",StringComparison.OrdinalIgnoreCase))??"theme";

    internal static ThemeVariant Resolve(ReaderTheme choice, PlatformThemeVariant platform) => choice switch
    {
        ReaderTheme.Chocolate => ReaderPalette.ChocolateVariant,
        ReaderTheme.MicrosoftBlue => ReaderPalette.BlueVariant,
        ReaderTheme.Night => ThemeVariant.Dark,
        ReaderTheme.System when platform == PlatformThemeVariant.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Light
    };
}
