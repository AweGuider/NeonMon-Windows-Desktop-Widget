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
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = workingDirectory
            };
            RemoveSessionVariables(startInfo.Environment);
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    // NeonMon started from inside a Claude session inherits that session's markers and endpoint, which make the new
    // CLI run as a nested session. Only variables the user also set persistently in Windows are kept.
    private static void RemoveSessionVariables(IDictionary<string, string?> environment)
    {
        var persistent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            persistent.UnionWith(Environment.GetEnvironmentVariables(target).Keys.OfType<string>());
        }

        foreach (var name in environment.Keys.Where(name => IsClaudeVariable(name) && !persistent.Contains(name)).ToList())
        {
            environment.Remove(name);
        }
    }

    private static bool IsClaudeVariable(string name) =>
        name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);
}
