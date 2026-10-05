using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

// Reads Codex plan limits and reset credits through the local `codex app-server` JSON-RPC protocol.
// Only `initialize` and `account/rateLimits/read` are sent; the read does not consume quota.
internal sealed class CodexAppServerReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<ProviderQuota?> ReadAsync(CancellationToken cancellationToken)
    {
        var executable = FindCodexExecutable();
        if (executable is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, "app-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };

        try
        {
            process.Start();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();

            await SendAsync(process, new
            {
                id = 1,
                method = "initialize",
                @params = new { clientInfo = new { name = "neonmon", title = (string?)null, version = "1.0" }, capabilities = (object?)null }
            }).ConfigureAwait(false);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || message.TryGetProperty("method", out _))
                {
                    continue;
                }

                if (id.GetInt32() == 1)
                {
                    await SendAsync(process, new { method = "initialized" }).ConfigureAwait(false);
                    await SendAsync(process, new { id = 2, method = "account/rateLimits/read", @params = new { } }).ConfigureAwait(false);
                }
                else if (id.GetInt32() == 2)
                {
                    return message.TryGetProperty("result", out var result) ? Parse(result) : null;
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Shutdown(process);
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private static void Shutdown(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.StandardInput.Close();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static ProviderQuota? Parse(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var (fiveHour, weekly) = QuotaJson.SplitWindows(ReadWindow(limits, "primary"), ReadWindow(limits, "secondary"));
        var credits = new List<ResetCredit>();
        var creditCount = 0;
        if (result.TryGetProperty("rateLimitResetCredits", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            creditCount = (int)(QuotaJson.ReadNumber(summary, "availableCount") ?? 0);
            if (summary.TryGetProperty("credits", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in list.EnumerateArray())
                {
                    if (QuotaJson.ReadString(credit, "status") == "available")
                    {
                        credits.Add(new ResetCredit(QuotaJson.ReadEpoch(credit, "expiresAt"), QuotaJson.ReadString(credit, "title")));
                    }
                }
            }
        }

        return new ProviderQuota
        {
            Provider = QuotaProvider.Codex,
            Plan = QuotaJson.PlanName(QuotaJson.ReadString(limits, "planType")),
            FiveHour = fiveHour,
            Weekly = weekly,
            ResetCreditCount = creditCount,
            ResetCredits = credits,
            Source = "app-server",
            CapturedAt = DateTimeOffset.Now
        };
    }

    private static QuotaWindow? ReadWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || QuotaJson.ReadNumber(window, "usedPercent") is not { } used)
        {
            return null;
        }

        return new QuotaWindow(used, QuotaJson.ReadEpoch(window, "resetsAt"), (int)(QuotaJson.ReadNumber(window, "windowDurationMins") ?? 0));
    }

    private static string? FindCodexExecutable()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var desktopBin = Path.Combine(local, "OpenAI", "Codex", "bin");
            var candidates = new List<string>
            {
                Path.Combine(desktopBin, "codex.exe"),
                Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64",
                    "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe")
            };

            if (Directory.Exists(desktopBin))
            {
                candidates.AddRange(Directory.EnumerateDirectories(desktopBin).Select(directory => Path.Combine(directory, "codex.exe")));
            }

            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(directory => directory.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                .Select(directory => Path.Combine(directory, "codex.exe")));

            return candidates
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
