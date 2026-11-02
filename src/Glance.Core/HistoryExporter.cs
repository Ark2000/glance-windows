using System.Text.Json;

namespace Glance.Core;

public static class HistoryExporter
{
    public static string Json(IEnumerable<HistoryEntry> entries) => JsonSerializer.Serialize(entries, LocalStore.JsonOptions);
    public static string Markdown(IEnumerable<HistoryEntry> entries) => string.Join("\n\n---\n\n", entries.Select(entry =>
        $"## {entry.Question.Replace("\r", " ").Replace("\n", " ")}\n\n{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm} · {entry.Model}\n\n{entry.Answer}\n\n状态：{entry.Status} · {(entry.Estimated ? "预留" : "用量估算")} ${entry.Cost:0.######}"));
}
