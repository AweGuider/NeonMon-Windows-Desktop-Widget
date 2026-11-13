using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonMon.Services;

internal enum ClaudeStatusLineState
{
    Unknown,
    NotSet,
    NeonMon,
    Other,
    Unreadable
}

internal sealed record ClaudeStatusLine(ClaudeStatusLineState State, string? Command);

// Reads and, only after the user confirms, writes the statusLine entry in Claude Code's user settings. The file
// is backed up first, every other setting is kept, and a file NeonMon cannot parse as plain JSON is left alone.
internal static class ClaudeStatusLineConfig
{
    public static string SettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string BackupSuffix => ".neonmon-backup";

    public static string? ScriptPath()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "neonmon-statusline.js");
        return File.Exists(script) ? script : null;
    }

    public static string Command(string script) => $"node \"{script.Replace('\\', '/')}\"";

    public static ClaudeStatusLine Read(string? neonMonCommand, string? settingsPath = null)
    {
        settingsPath ??= SettingsPath;
        if (!File.Exists(settingsPath))
        {
            return new ClaudeStatusLine(ClaudeStatusLineState.NotSet, null);
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root)
            {
                return new ClaudeStatusLine(ClaudeStatusLineState.Unreadable, null);
            }

            var command = root["statusLine"]?["command"]?.GetValue<string>();
            return command switch
            {
                null => new ClaudeStatusLine(ClaudeStatusLineState.NotSet, null),
                _ when string.Equals(command.Trim(), neonMonCommand, StringComparison.OrdinalIgnoreCase) || RunsExistingScript(command)
                    => new ClaudeStatusLine(ClaudeStatusLineState.NeonMon, command),
                _ => new ClaudeStatusLine(ClaudeStatusLineState.Other, command)
            };
        }
        catch
        {
            return new ClaudeStatusLine(ClaudeStatusLineState.Unreadable, null);
        }
    }

    // Another NeonMon copy's script, or one pasted by hand, works too as long as the file it names still exists.
    private static bool RunsExistingScript(string command)
    {
        var match = System.Text.RegularExpressions.Regex.Match(command, "\"([^\"]*neonmon-statusline\\.js)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && File.Exists(match.Groups[1].Value);
    }

    // Returns null on success, otherwise a sentence for the user.
    public static string? Connect(string command, string? settingsPath = null)
    {
        settingsPath ??= SettingsPath;
        try
        {
            JsonObject root;
            if (File.Exists(settingsPath))
            {
                if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject existing)
                {
                    return "Claude's settings.json isn't plain JSON NeonMon can edit. Use Copy setting instead.";
                }

                root = existing;
                File.Copy(settingsPath, settingsPath + BackupSuffix, overwrite: true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                root = [];
            }

            root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var temporary = settingsPath + ".neonmon-tmp";
            File.WriteAllText(temporary, json + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, settingsPath, overwrite: true);
            return null;
        }
        catch (JsonException)
        {
            return "Claude's settings.json isn't plain JSON NeonMon can edit. Use Copy setting instead.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Couldn't write Claude's settings.json. Use Copy setting instead.";
        }
    }
}
