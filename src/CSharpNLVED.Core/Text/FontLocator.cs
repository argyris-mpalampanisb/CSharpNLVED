using System.Diagnostics;
using Microsoft.Win32;

namespace CSharpNLVED.Core.Text;

/// <summary>
/// Finds the font file for a font family name such as "Arial Black", so FFmpeg's drawtext
/// can use it (drawtext needs a file, not a name).
///
///   Windows: the installed-fonts list in the registry (all users + current user), which maps
///            "Arial Black (TrueType)" to "ariblk.ttf".
///   Linux/macOS: fontconfig's fc-match, only accepted when it returns the family asked for
///            (it otherwise quietly substitutes another font); then a search of the usual font
///            folders by file name. Untested on real Linux/macOS machines so far.
/// </summary>
public static class FontLocator
{
    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    /// <summary>Fonts already found. Misses aren't kept, so a font installed while CSharpNLVED runs is found later.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Found =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The font file for <paramref name="family"/>, or null if it isn't installed.</summary>
    public static string? Find(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
            return null;
        family = family.Trim();
        if (Found.TryGetValue(family, out var known) && File.Exists(known))
            return known;

        try
        {
            var path = OperatingSystem.IsWindows()
                ? FindOnWindows(family)
                : FindWithFontconfig(family) ?? FindInFolders(family);
            if (path is not null)
                Found[family] = path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Windows

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindOnWindows(string family)
    {
        var systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(FontsKey);
            if (key is null)
                continue;

            foreach (var valueName in key.GetValueNames())
            {
                if (!RegistryNameMatches(valueName, family) || key.GetValue(valueName) is not string file || file.Length == 0)
                    continue;

                // Per-user fonts are stored with a full path, system fonts with just the file name.
                var path = Path.IsPathRooted(file) ? file : Path.Combine(systemFonts, file);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    /// <summary>
    /// True if a registry font entry such as "Arial Black (TrueType)" or
    /// "Cambria &amp; Cambria Math (TrueType)" names <paramref name="family"/>.
    /// </summary>
    internal static bool RegistryNameMatches(string valueName, string family)
    {
        var name = valueName;
        var paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(')'))
            name = name[..paren];

        return name.Split(" & ", StringSplitOptions.TrimEntries)
            .Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------- Linux / macOS

    private static string? FindWithFontconfig(string family)
    {
        // "%{family}|%{file}" → e.g. "Arial Black,Arial|/usr/share/fonts/…/ariblk.ttf"
        var output = Run("fc-match", "--format=%{family}|%{file}", family);
        if (output is null || output.Split('|', 2) is not [var families, var file])
            return null;

        var matches = families.Split(',', StringSplitOptions.TrimEntries)
            .Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        return matches && File.Exists(file) ? file : null;
    }

    private static string? FindInFolders(string family)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            "/System/Library/Fonts", "/System/Library/Fonts/Supplemental", "/Library/Fonts",
            Path.Combine(home, "Library", "Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".local", "share", "fonts"), Path.Combine(home, ".fonts"),
        };

        // "Arial Black" matches "Arial Black.ttf" and "ArialBlack.ttf".
        var wanted = new[] { family, family.Replace(" ", "", StringComparison.Ordinal) };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var found = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => FontExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                                     && wanted.Any(w => string.Equals(Path.GetFileNameWithoutExtension(f), w, StringComparison.OrdinalIgnoreCase)));
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Runs a small helper program; null if it's missing, fails or takes too long.</summary>
    private static string? Run(string program, params string[] arguments)
    {
        var psi = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process is null)
            return null;

        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { /* already gone */ }
            return null;
        }

        return process.ExitCode == 0 ? output.Result.Trim() : null;
    }
}
