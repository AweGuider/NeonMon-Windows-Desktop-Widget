using System.Diagnostics;

namespace NeonMon.Services;

// Opens a terminal and starts an interactive CLI. For Claude this renews the CLI sign-in and refreshes the
// statusline quota data. No prompt is sent; the session is left to the user.
internal static class CliLauncher
{
    // Never bare "claude": Windows PowerShell resolves it to npm's claude.ps1, which the default
    // execution policy blocks. The native installer ships claude.exe; npm ships claude.cmd.
    public static string? ClaudeCommand()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim().Trim('"'), name)))
                        return name;
                }
                catch (ArgumentException)
                {
                }
            }
        }
        return null;
    }

    public static string? CodexCommand() =>
        CodexAppServerReader.FindCodexExecutable() is { } executable ? $"& '{executable.Replace("'", "''")}'" : null;

    public static bool Launch(string command, string? directory)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return TryStart("wt.exe", $"-d \"{workingDirectory}\" powershell.exe -NoExit -Command \"{command}\"", workingDirectory)
            || TryStart("powershell.exe", $"-NoExit -Command \"{command}\"", workingDirectory);
    }

    private static bool TryStart(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDirectory
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
