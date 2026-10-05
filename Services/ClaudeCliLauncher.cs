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

        // claude.cmd rather than claude: Windows PowerShell resolves "claude" to npm's claude.ps1,
        // which the default execution policy blocks.
        return TryStart("wt.exe", $"-d \"{workingDirectory}\" powershell.exe -NoExit -Command claude.cmd", workingDirectory)
            || TryStart("powershell.exe", "-NoExit -Command claude.cmd", workingDirectory);
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
