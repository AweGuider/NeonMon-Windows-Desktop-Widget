using System.Diagnostics;

namespace NeonMon.Services;

// Opens a terminal you can watch and runs a command in it: an interactive CLI (for Claude this renews the sign-in and
// refreshes the statusline quota data; no prompt is sent), or an install or update command the user clicked.
internal static class CliLauncher
{
    // Never bare "claude": Windows PowerShell resolves it to npm's claude.ps1, which the default
    // execution policy blocks. The full path to claude.exe or claude.cmd avoids it.
    public static string? ClaudeCommand() => CliLocator.FindClaude() is { } claude ? Invoke(claude.Path) : null;

    public static string? CodexCommand() => CodexAppServerReader.FindCodexExecutable() is { } executable ? Invoke(executable) : null;

    public static string Invoke(string executable, string? arguments = null) =>
        $"& '{executable.Replace("'", "''")}'" + (arguments is null ? "" : $" {arguments}");

    public static bool Launch(string command, string? directory)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Encoded, so quotes, paths and semicolons reach PowerShell intact; Windows Terminal would split on ';'.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        return TryStart("wt.exe", $"-d \"{workingDirectory}\" powershell.exe -NoExit -EncodedCommand {encoded}", workingDirectory)
            || TryStart("powershell.exe", $"-NoExit -EncodedCommand {encoded}", workingDirectory);
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
            startInfo.Environment["PATH"] = CliLocator.SearchPath();
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
