using System.Diagnostics;

namespace NeonMon.Services;

// Opens a terminal in the configured folder and starts the interactive claude CLI, which renews the
// CLI sign-in and refreshes the statusline quota data. No prompt is sent; the session is left to the user.
internal static class ClaudeCliLauncher
{
    public static bool Launch(string? directory)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Never bare "claude": Windows PowerShell resolves it to npm's claude.ps1, which the default
        // execution policy blocks. The native installer ships claude.exe; npm ships claude.cmd.
        var command = CommandName();
        return TryStart("wt.exe", $"-d \"{workingDirectory}\" powershell.exe -NoExit -Command {command}", workingDirectory)
            || TryStart("powershell.exe", $"-NoExit -Command {command}", workingDirectory);
    }

    private static string CommandName()
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
        return "claude.cmd";
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
