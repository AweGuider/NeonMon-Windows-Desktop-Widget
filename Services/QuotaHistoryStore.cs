using System.Text.Json;
using NeonMon.Models;

namespace NeonMon.Services;

internal static class QuotaHistoryStore
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonMon", "quota-history.json");

    public static QuotaHistory Load()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? JsonSerializer.Deserialize<QuotaHistory>(File.ReadAllText(HistoryPath)) ?? new QuotaHistory()
                : new QuotaHistory();
        }
        catch
        {
            return new QuotaHistory();
        }
    }

    public static void Save(QuotaHistory history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var temporaryPath = HistoryPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, HistoryPath, true);
        }
        catch
        {
        }
    }
}
