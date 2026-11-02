using System.Text.Json;

namespace Glance.Core;

public sealed record HistoryEntry(Guid Id, DateTimeOffset Time, string Question, string Answer, string Model, string Status, decimal Cost, bool Estimated);
public sealed record Charge(Guid Id, DateTimeOffset Time, decimal Amount, bool Estimated);

public sealed class LocalStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; }
    public LocalStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "history"));
    }

    public T Read<T>(string name, T fallback)
    {
        var path = Path.Combine(Root, name);
        if (!File.Exists(path)) return fallback;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException($"本机文件 {name} 格式损坏。请先备份并修复该文件。原文件已保留。"); }
    }

    public void Write<T>(string name, T value)
    {
        var path = Path.Combine(Root, name);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }

    public List<HistoryEntry> History(string search = "") => Directory.EnumerateFiles(Path.Combine(Root, "history"), "*.json")
        .Select(p => Read<HistoryEntry?>(Path.Combine("history", Path.GetFileName(p)), null))
        .OfType<HistoryEntry>()
        .Where(e => e.Question.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Answer.Contains(search, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => e.Time).ToList();
    public void SaveHistory(HistoryEntry entry) => Write(Path.Combine("history", entry.Id + ".json"), entry);
    public void DeleteHistory(Guid id) => File.Delete(Path.Combine(Root, "history", id + ".json"));
}

public sealed class BudgetLedger
{
    private readonly LocalStore store;
    private List<Charge> charges;
    public BudgetLedger(LocalStore store)
    {
        this.store = store;
        charges = store.Read("ledger.json", new List<Charge>());
    }
    public decimal DayTotal(DateTimeOffset time) => charges.Where(c => c.Time.ToLocalTime().Date == time.ToLocalTime().Date).Sum(c => c.Amount);
    public decimal MonthTotal(DateTimeOffset time) => charges.Where(c => c.Time.ToLocalTime().Year == time.ToLocalTime().Year && c.Time.ToLocalTime().Month == time.ToLocalTime().Month).Sum(c => c.Amount);
    public Charge Reserve(Settings settings, string question, DateTimeOffset time)
    {
        var amount = settings.Reserve(question);
        if (amount > settings.RequestLimit) throw new InvalidOperationException("本次预留费用超过单次限额。请缩短输入、降低输出长度或调整限额。");
        if (DayTotal(time) + amount > settings.DailyLimit) throw new InvalidOperationException("今日可用额度不足，请调整设置或次日再试。");
        if (MonthTotal(time) + amount > settings.MonthlyLimit) throw new InvalidOperationException("本月可用额度不足，请调整设置或下月再试。");
        var charge = new Charge(Guid.NewGuid(), time, amount, true);
        Commit([.. charges, charge]);
        return charge;
    }
    public Charge Settle(Charge charge, Settings settings, Usage? usage, bool rejected = false)
    {
        var amount = rejected ? 0 : usage is null ? charge.Amount : (usage.InputTokens * settings.InputRate + usage.OutputTokens * settings.OutputRate) / 1_000_000m;
        var updated = charge with { Amount = amount, Estimated = usage is null && !rejected };
        Commit(charges.Select(c => c.Id == charge.Id ? updated : c).ToList());
        return updated;
    }
    private void Commit(List<Charge> next) { store.Write("ledger.json", next); charges = next; }
}
