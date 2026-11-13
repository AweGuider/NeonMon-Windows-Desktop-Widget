using NeonMon.Models;

namespace NeonMon.Services;

// What the Setup page shows. Every check is a file-exists or registry read, and none touches another app's
// sign-in files: a provider counts as signed in once NeonMon has quota data for it.
internal sealed record CliSetupStatus(
    CliInstall? Claude,
    CliInstall? Codex,
    string? Node,
    string? Npm,
    string? Winget,
    bool ClaudeSignedIn,
    bool CodexSignedIn,
    ClaudeStatusLine StatusLine)
{
    public static CliSetupStatus Unknown { get; } =
        new(null, null, null, null, null, false, false, new ClaudeStatusLine(ClaudeStatusLineState.Unknown, null));

    // statusLine is null when Claude's settings were not read this time; the previous reading is kept.
    public static CliSetupStatus Detect(QuotaSnapshot quota, ClaudeStatusLine statusLine) => new(
        CliLocator.FindClaude(),
        CliLocator.FindCodex(),
        CliLocator.FindNode(),
        CliLocator.FindNpm(),
        CliLocator.FindWinget(),
        quota.Claude.HasData,
        quota.Codex.HasData,
        statusLine);

    public static CliSetupStatus Sample { get; } = new(
        new CliInstall(@"C:\Users\you\AppData\Local\Microsoft\WinGet\Links\claude.exe", CliSource.Winget),
        null,
        null,
        null,
        @"C:\Users\you\AppData\Local\Microsoft\WindowsApps\winget.exe",
        ClaudeSignedIn: true,
        CodexSignedIn: false,
        new ClaudeStatusLine(ClaudeStatusLineState.NotSet, null));
}

// Every command runs its tool by full path, so PowerShell never resolves a blocked .ps1 shim, and is only offered
// when that tool exists.
internal static class CliSetup
{
    public const string NodeWingetId = "OpenJS.NodeJS.LTS";

    public static string WingetId(QuotaProvider provider) => provider == QuotaProvider.Claude ? "Anthropic.ClaudeCode" : "OpenAI.Codex";

    public static string? InstallCommand(CliSetupStatus status, string wingetId) =>
        status.Winget is { } winget ? CliLauncher.Invoke(winget, $"install --exact --id {wingetId}") : null;

    // The official alternatives, shown for copying only; NeonMon never runs them. npm.cmd, not npm, so they also
    // work when pasted into PowerShell.
    public static IReadOnlyList<(string Label, string Command, bool NeedsNpm)> OtherInstallCommands { get; } =
    [
        ("Claude · Anthropic installer", "irm https://claude.ai/install.ps1 | iex", false),
        ("Claude · npm", "npm.cmd install -g @anthropic-ai/claude-code", true),
        ("Codex · OpenAI installer", "powershell -ExecutionPolicy ByPass -c \"irm https://chatgpt.com/codex/install.ps1 | iex\"", false),
        ("Codex · npm", "npm.cmd install -g @openai/codex", true)
    ];

    // Null when NeonMon has no safe one-click update for this install on this PC.
    public static string? UpdateCommand(QuotaProvider provider, CliInstall install, CliSetupStatus status)
    {
        var package = provider == QuotaProvider.Claude ? "@anthropic-ai/claude-code" : "@openai/codex";
        return (provider, install.Source) switch
        {
            (_, CliSource.Winget) when status.Winget is { } winget =>
                CliLauncher.Invoke(winget, $"upgrade --exact --id {WingetId(provider)}"),
            (_, CliSource.Npm) when status.Npm is { } npm => CliLauncher.Invoke(npm, $"install -g {package}@latest"),
            (QuotaProvider.Claude, _) => CliLauncher.Invoke(install.Path, "update"),
            _ => null
        };
    }

    // Wraps an install or update so the terminal ends with a plain result line instead of a bare prompt, then closes
    // on Enter. winget's "no newer version" exit code counts as up to date, not as a failure.
    public static string FinishedScript(string command, string done, string? versionExecutable)
    {
        static string Quote(string text) => $"'{text.Replace("'", "''")}'";
        var version = versionExecutable is null
            ? ""
            : $"$v = & {Quote(versionExecutable)} --version 2>$null | Select-Object -First 1; if ($v) {{ Write-Host \"Version now: $v\" }}; ";
        return string.Join("\n",
            "$host.UI.RawUI.WindowTitle = 'NeonMon setup'",
            command,
            "$code = $LASTEXITCODE",
            "Write-Host ''",
            $"if ($code -eq 0) {{ Write-Host {Quote("NeonMon: " + done)} -ForegroundColor Cyan; {version}}}",
            "elseif ($code -eq -1978335189) { Write-Host 'NeonMon: already up to date. Nothing changed.' -ForegroundColor Cyan }",
            "else { Write-Host \"NeonMon: this didn't finish (exit code $code). The messages above say why.\" -ForegroundColor Yellow }",
            "Write-Host 'Back in NeonMon Settings, the step updates on its own.'",
            "Read-Host 'Press Enter to close' | Out-Null",
            "exit");
    }

    public static string SourceShort(QuotaProvider provider, CliInstall install) => install.Source switch
    {
        CliSource.Winget => "winget",
        CliSource.Npm => "npm",
        CliSource.VendorInstaller => provider == QuotaProvider.Claude ? "Anthropic's installer" : "OpenAI's installer",
        CliSource.CodexApp => "the Codex app",
        _ => Path.GetFileName(install.Path)
    };

    public static string SourceText(QuotaProvider provider, CliInstall install, bool canUpdate) => install.Source switch
    {
        CliSource.Winget when !canUpdate => "Installed with winget, which isn't available to update it.",
        CliSource.Winget when provider == QuotaProvider.Claude => "Installed with winget, which doesn't update it. Use Update.",
        CliSource.Winget => "Installed with winget.",
        CliSource.Npm when !canUpdate => "Installed with npm, which isn't found to update it.",
        CliSource.Npm => "Installed with npm.",
        CliSource.VendorInstaller when provider == QuotaProvider.Claude => "Installed with Anthropic's installer.",
        CliSource.VendorInstaller => "Installed with OpenAI's installer.",
        CliSource.CodexApp => "Comes with the Codex app, which keeps it updated.",
        _ => $"Found {Path.GetFileName(install.Path)}."
    };
}
