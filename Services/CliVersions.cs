using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonMon.Models;

namespace NeonMon.Services;

// Installed and latest versions for the Setup page's Update step. It only runs while Settings is open. The installed
// version comes from npm's package.json or "<cli> --version", cached until the file changes; the latest comes from
// the channel the CLI was installed from (winget's catalog, or the npm registry for npm and Anthropic's installer),
// cached for six hours. Neither is a model call or uses an account.
internal sealed partial class CliVersions : IDisposable
{
    private static readonly TimeSpan LatestLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    private readonly string _userAgent;
    private readonly Dictionary<string, (DateTime Stamp, string? Version)> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(QuotaProvider, CliSource), (DateTime At, string? Version)> _latest = [];
    private readonly HashSet<string> _running = [];
    private readonly object _gate = new();
    private HttpClient? _client;

    public CliVersions(string currentVersion) => _userAgent = $"NeonMon/{currentVersion}";

    // Raised on a worker thread when a version arrives.
    public event Action? Changed;

    public string? Installed(QuotaProvider provider, CliInstall install)
    {
        lock (_gate)
        {
            return _installed.TryGetValue(install.Path, out var entry) && entry.Stamp == Stamp(StampPath(provider, install)) ? entry.Version : null;
        }
    }

    public string? Latest(QuotaProvider provider, CliInstall install)
    {
        lock (_gate)
        {
            return _latest.TryGetValue((provider, LatestChannel(provider, install)), out var entry) ? entry.Version : null;
        }
    }

    public bool Checking(QuotaProvider provider, CliInstall install)
    {
        lock (_gate)
        {
            return _running.Contains($"latest {provider} {LatestChannel(provider, install)}") || _running.Contains($"installed {install.Path}");
        }
    }

    public void Refresh(QuotaProvider provider, CliInstall? install, string? winget, bool checkLatest)
    {
        if (install is null)
        {
            return;
        }

        var stamp = Stamp(StampPath(provider, install));
        bool needInstalled, needLatest;
        var channel = LatestChannel(provider, install);
        lock (_gate)
        {
            needInstalled = !(_installed.TryGetValue(install.Path, out var entry) && entry.Stamp == stamp) && _running.Add($"installed {install.Path}");
            needLatest = checkLatest && channel != CliSource.Unknown
                && !(_latest.TryGetValue((provider, channel), out var latest) && DateTime.UtcNow - latest.At < LatestLifetime)
                && (channel != CliSource.Winget || winget is not null)
                && _running.Add($"latest {provider} {channel}");
        }

        if (needInstalled)
        {
            _ = Task.Run(() =>
            {
                var version = ReadInstalled(provider, install);
                Finish($"installed {install.Path}", () => _installed[install.Path] = (stamp, version));
            });
        }

        if (needLatest)
        {
            _ = Task.Run(async () =>
            {
                var version = channel == CliSource.Winget
                    ? ReadWingetVersion(winget!, CliSetup.WingetId(provider))
                    : await ReadNpmLatestAsync(provider).ConfigureAwait(false);
                Finish($"latest {provider} {channel}", () => _latest[(provider, channel)] = (DateTime.UtcNow, version));
            });
        }
    }

    internal void SetPreview(QuotaProvider provider, CliInstall install, string installed, string latest)
    {
        lock (_gate)
        {
            _installed[install.Path] = (Stamp(StampPath(provider, install)), installed);
            _latest[(provider, LatestChannel(provider, install))] = (DateTime.UtcNow, latest);
        }
    }

    // True when latest is newer than installed. A prerelease counts as older than its release.
    public static bool IsNewer(string latest, string installed)
    {
        static (Version Number, bool Prerelease)? Parse(string text)
        {
            var match = VersionPattern().Match(text);
            return match.Success && Version.TryParse(match.Groups[1].Value, out var number) ? (number, match.Groups[2].Success) : null;
        }

        if (Parse(latest) is not { } newest || Parse(installed) is not { } current)
        {
            return false;
        }

        var compare = newest.Number.CompareTo(current.Number);
        return compare > 0 || (compare == 0 && current.Prerelease && !newest.Prerelease);
    }

    public static string? FirstVersion(string? text) =>
        text is not null && VersionPattern().Match(text) is { Success: true } match ? match.Value : null;

    // Codex's own installer and the Codex app have no Update button, so their latest version is not looked up.
    private static CliSource LatestChannel(QuotaProvider provider, CliInstall install) => install.Source switch
    {
        CliSource.Winget => CliSource.Winget,
        CliSource.Npm => CliSource.Npm,
        _ when provider == QuotaProvider.Claude => CliSource.Npm,
        _ => CliSource.Unknown
    };

    // npm rewrites package.json on every update, while its claude.cmd shim can keep its date.
    private static string StampPath(QuotaProvider provider, CliInstall install) =>
        install.Source == CliSource.Npm ? NpmPackageJson(provider) : install.Path;

    private static string NpmPackageJson(QuotaProvider provider) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules",
            provider == QuotaProvider.Claude ? Path.Combine("@anthropic-ai", "claude-code") : Path.Combine("@openai", "codex"), "package.json");

    private void Finish(string key, Action store)
    {
        lock (_gate)
        {
            store();
            _running.Remove(key);
        }

        Changed?.Invoke();
    }

    private static string? ReadInstalled(QuotaProvider provider, CliInstall install)
    {
        if (install.Source == CliSource.Npm)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(NpmPackageJson(provider)));
                return document.RootElement.GetProperty("version").GetString();
            }
            catch
            {
            }
        }

        return install.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? FirstVersion(Run(install.Path, "--version")) : null;
    }

    // winget's table labels are translated, so the version is taken from the line that starts like "Version: 1.2.3".
    private static string? ReadWingetVersion(string winget, string id)
    {
        var output = Run(winget, $"show --exact --id {id} --disable-interactivity --accept-source-agreements");
        return output?.Split('\n').Select(line => WingetVersionLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value;
    }

    private async Task<string?> ReadNpmLatestAsync(QuotaProvider provider)
    {
        var package = provider == QuotaProvider.Claude ? "@anthropic-ai%2Fclaude-code" : "@openai%2Fcodex";
        try
        {
            _client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://registry.npmjs.org/{package}/latest");
            request.Headers.UserAgent.ParseAdd(_userAgent);
            using var response = await _client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return document.RootElement.GetProperty("version").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string? Run(string executable, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (process is null)
            {
                return null;
            }

            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ProcessTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return output.GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static DateTime Stamp(string path)
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

    public void Dispose() => _client?.Dispose();

    [GeneratedRegex(@"(\d+\.\d+\.\d+)(-[0-9A-Za-z.]+)?")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*\p{L}+\s*:\s*(\d+\.\d+\.\d+\S*)\s*$")]
    private static partial Regex WingetVersionLine();
}
