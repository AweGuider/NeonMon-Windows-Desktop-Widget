namespace NeonMon.Services;

internal enum CliSource
{
    Unknown,
    Winget,
    Npm,
    VendorInstaller,
    CodexApp
}

internal sealed record CliInstall(string Path, CliSource Source);

// Finds the Claude and Codex CLIs and Node.js. NeonMon's own PATH is a copy taken when it started, so a CLI installed
// since then is only found through the PATH saved in the registry or the folders each installer uses.
internal static class CliLocator
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string WingetLinks => Path.Combine(Local, "Microsoft", "WinGet", "Links");
    private static string NpmBin => Path.Combine(Roaming, "npm");

    // Machine then User, the order a new shell gets, then anything only this process has.
    public static string SearchPath() => string.Join(Path.PathSeparator, SearchDirectories());

    public static IReadOnlyList<string> SearchDirectories()
    {
        var directories = new List<string>();
        foreach (var value in new[]
                 {
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                     Environment.GetEnvironmentVariable("PATH")
                 })
        {
            foreach (var entry in (value ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
                if (directory.IndexOfAny(Path.GetInvalidPathChars()) < 0
                    && !directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    // The first match on PATH is what a new terminal runs; the installer folders only count when PATH has none.
    public static CliInstall? FindClaude()
    {
        var directories = SearchDirectories().Concat([Path.Combine(Profile, ".local", "bin"), WingetLinks, NpmBin]);
        foreach (var directory in directories)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                if (Exists(directory, name) is { } path)
                {
                    return new CliInstall(path, SourceOf(path));
                }
            }
        }

        return null;
    }

    // The top-level Codex app bin copy can be stale, so the newest codex.exe wins.
    public static CliInstall? FindCodex()
    {
        var appBin = Path.Combine(Local, "OpenAI", "Codex", "bin");
        var candidates = new List<string>
        {
            Path.Combine(appBin, "codex.exe"),
            Path.Combine(Local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(WingetLinks, "codex.exe"),
            Path.Combine(NpmBin, "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64",
                "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe")
        };

        try
        {
            if (Directory.Exists(appBin))
            {
                candidates.AddRange(Directory.EnumerateDirectories(appBin).Select(directory => Path.Combine(directory, "codex.exe")));
            }
        }
        catch
        {
        }

        candidates.AddRange(SearchDirectories().Select(directory => Path.Combine(directory, "codex.exe")));
        var newest = candidates
            .Where(File.Exists)
            .OrderByDescending(LastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? null : new CliInstall(newest, SourceOf(newest));
    }

    public static string? FindNode() => FindOnPath("node.exe", NodeFolder);

    // npm.cmd, never bare "npm": PowerShell would pick npm.ps1, which the default execution policy blocks.
    public static string? FindNpm() => FindOnPath("npm.cmd", NodeFolder);

    public static string? FindWinget() => FindOnPath("winget.exe", Path.Combine(Local, "Microsoft", "WindowsApps"));

    private static string NodeFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs");

    private static string? FindOnPath(string name, string fallbackDirectory) =>
        SearchDirectories()
            .Append(fallbackDirectory)
            .Select(directory => Exists(directory, name))
            .FirstOrDefault(path => path is not null);

    public static CliSource SourceOf(string path)
    {
        bool Under(params string[] parts) =>
            path.StartsWith(System.IO.Path.Combine(parts) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (Under(Local, "Microsoft", "WinGet"))
        {
            return CliSource.Winget;
        }

        if (Under(NpmBin))
        {
            return CliSource.Npm;
        }

        if (Under(Profile, ".local", "bin") || Under(Local, "Programs", "OpenAI", "Codex"))
        {
            return CliSource.VendorInstaller;
        }

        return Under(Local, "OpenAI", "Codex") ? CliSource.CodexApp : CliSource.Unknown;
    }

    private static string? Exists(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // winget's Links entries are symbolic links, whose own date is when the link was made.
    private static DateTime LastWriteTimeUtc(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return (file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? file).LastWriteTimeUtc;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
