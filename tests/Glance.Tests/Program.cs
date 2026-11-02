using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Glance.Core;
using Glance.Desktop;

if (args.Length == 2 && args[0] == "--ui-fixture")
{
    var fixture = new LocalStore(Path.GetFullPath(args[1]));
    fixture.Write("settings.json", new Settings { Endpoint = "http://127.0.0.1:53218/v1", EncryptedKey = KeyVault.Encrypt("synthetic-local-ui-test-key") });
    fixture.SaveHistory(new HistoryEntry(Guid.NewGuid(), DateTimeOffset.Now.AddMinutes(-12), "什么是机会成本？", "机会成本是选择一个方案时，放弃的其他方案中价值最高的一项。\n\n例如，用两小时读书，就放弃了这两小时可用于运动或工作的收益。它帮助我们比较选择背后的取舍。", "deepseek-flash", "已完成", 0.000123m, false));
    Console.WriteLine("Synthetic UI fixture prepared."); return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
var root = Path.Combine(Path.GetTempPath(), "GlanceTests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var defaults = new Settings();

Test("Defaults, endpoint normalization and parameter validation", () =>
{
    defaults.Validate(); Check(!defaults.Thinking); Check(defaults.Model == "deepseek-flash");
    Check((defaults with { Endpoint = "https://example.test/v1/" }).CompletionUri().ToString() == "https://example.test/v1/chat/completions");
    Check((defaults with { Endpoint = "https://example.test/chat/completions/" }).CompletionUri().ToString() == "https://example.test/chat/completions");
    foreach (var url in new[] { "http://example.test", "file:///C:/secret", "https://user:pass@example.test", "https://example.test?a=b" }) Throws<ArgumentException>(() => (defaults with { Endpoint = url }).Validate());
    (defaults with { Endpoint = "http://127.0.0.1:53218" }).Validate();
    foreach (var field in new[] { "messages", "max_tokens", "stream", "thinking", "model", "n", "tools", "temperature", "reasoning_effort" })
        Throws<ArgumentException>(() => (defaults with { ExtraJson = "{\"" + field + "\":1}" }).Validate());
    Throws<ArgumentException>(() => (defaults with { MaxTokens = 0 }).Validate());
    Throws<ArgumentException>(() => (defaults with { InputRate = 0 }).Validate());
    Throws<ArgumentException>(() => (defaults with { Temperature = double.NaN }).Validate());
    Throws<JsonException>(() => (defaults with { ExtraJson = "{" }).Validate());
});

Test("DPAPI encrypt/decrypt and tamper rejection", () =>
{
    const string secret = "synthetic-local-test-key-123";
    var cipher = KeyVault.Encrypt(secret); Check(cipher != secret); Check(!cipher.Contains(secret)); Check(KeyVault.Decrypt(cipher) == secret);
    var corrupted = Convert.FromBase64String(cipher); corrupted[^1] ^= 1;
    Throws<System.Security.Cryptography.CryptographicException>(() => KeyVault.Decrypt(Convert.ToBase64String(corrupted)));
    Check(KeyVault.Decrypt("") == "");
});

Test("Hotkey parsing", () =>
{
    Check(GlobalHotkey.Parse("Ctrl+Alt+Space") == (3u, 32u));
    Check(GlobalHotkey.Parse("Ctrl+Shift+Q") == (6u, 81u));
    Throws<ArgumentException>(() => GlobalHotkey.Parse("Q"));
    Throws<ArgumentException>(() => GlobalHotkey.Parse("Ctrl+LWin"));
});

Test("History export preserves Unicode, line breaks and metadata", () =>
{
    var entry = new HistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, "熵\n是什么", "第一行\n第二行", "test-model", "已停止", .012m, true);
    var markdown = HistoryExporter.Markdown([entry]); Check(markdown.StartsWith("## 熵 是什么")); Check(markdown.Contains("第一行\n第二行")); Check(markdown.Contains("$0.012"));
    var restored = JsonSerializer.Deserialize<List<HistoryEntry>>(HistoryExporter.Json([entry]))!; Check(restored.Single() == entry);
    Check(HistoryExporter.Json([]) == "[]");
});

Test("Atomic settings/history persistence and search", () =>
{
    var store = new LocalStore(Path.Combine(root, "store"));
    store.Write("settings.json", defaults); Check(store.Read("settings.json", new Settings()).Model == defaults.Model);
    var first = new HistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, "Entropy", "熵：描述不确定程度。", defaults.Model, "已完成", 0.001m, false);
    store.SaveHistory(first); store.SaveHistory(first with { Answer = "更新内容" });
    Check(store.History("ENTROPY").Count == 1); Check(store.History("更新").Count == 1); Check(store.History("absent").Count == 0);
    store.DeleteHistory(first.Id); Check(store.History().Count == 0);
    File.WriteAllText(Path.Combine(store.Root, "settings.json"), "broken");
    Throws<IOException>(() => store.Read("settings.json", defaults)); Check(File.ReadAllText(Path.Combine(store.Root, "settings.json")) == "broken");
});

Test("Budget reservation persists across restart and history deletion", () =>
{
    var store = new LocalStore(Path.Combine(root, "budget")); var ledger = new BudgetLedger(store); var now = DateTimeOffset.Now;
    var charge = ledger.Reserve(defaults, "什么是熵？", now);
    Check(charge.Estimated && charge.Amount > 0);
    Check(new BudgetLedger(store).DayTotal(now) == charge.Amount);
    store.SaveHistory(new HistoryEntry(charge.Id, now, "熵", "回答", defaults.Model, "已停止", charge.Amount, true)); store.DeleteHistory(charge.Id);
    Check(ledger.DayTotal(now) == charge.Amount);
    var settled = ledger.Settle(charge, defaults, new Usage(10, 20));
    Check(settled.Amount == (10 * defaults.InputRate + 20 * defaults.OutputRate) / 1_000_000m); Check(!settled.Estimated);
    var canceled = ledger.Reserve(defaults, "cancel", now); Check(ledger.Settle(canceled, defaults, null).Amount == canceled.Amount);
    var rejected = ledger.Reserve(defaults, "reject", now); Check(ledger.Settle(rejected, defaults, null, true).Amount == 0);
    Throws<InvalidOperationException>(() => ledger.Reserve(defaults with { RequestLimit = 0.0000001m }, "large", now));
    Throws<InvalidOperationException>(() => ledger.Reserve(defaults with { DailyLimit = charge.Amount / 2 }, "large", now));
    Throws<InvalidOperationException>(() => ledger.Reserve(defaults with { MonthlyLimit = charge.Amount / 2 }, "large", now));
    Check(ledger.DayTotal(now.AddDays(1)) == 0); Check(ledger.MonthTotal(now.AddMonths(1)) == 0);
});

AsyncTest("Streaming UTF-8, keepalive, same-chunk usage, single-turn payload", async () =>
{
    string? payload = null;
    var handler = new FakeHandler(async (request, token) =>
    {
        Check(request.Headers.Authorization?.Parameter == "synthetic-test");
        payload = await request.Content!.ReadAsStringAsync(token);
        return Sse(": keepalive\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"熵是\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"一种度量。\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":10}}\n\ndata: [DONE]\n\n");
    });
    using var http = new HttpClient(handler); var text = new StringBuilder(); var progress = new CompletionProgress();
    await new CompletionClient(http).StreamAsync(defaults with { ExtraJson = "{\"stop\":[\"END\"]}" }, "synthetic-test", "熵", s => text.Append(s), progress, CancellationToken.None);
    Check(text.ToString() == "熵是一种度量。"); Check(progress.Usage == new Usage(30, 10));
    using var parsed = JsonDocument.Parse(payload!); var json = parsed.RootElement;
    Check(json.GetProperty("messages").GetArrayLength() == 2); Check(json.GetProperty("thinking").GetProperty("type").GetString() == "disabled");
    Check(json.GetProperty("stream").GetBoolean()); Check(json.GetProperty("max_tokens").GetInt32() == 512);
    Check(json.GetProperty("stop")[0].GetString() == "END");
});

AsyncTest("Legacy usage-only chunk and multiline SSE event", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Sse("data: {\"choices\":\ndata: [{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"length\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":6}}\n\ndata: [DONE]\n\n"))));
    var p = new CompletionProgress(); var answer = ""; await new CompletionClient(http).StreamAsync(defaults, "test", "q", s => answer += s, p, CancellationToken.None);
    Check(p.Usage == new Usage(5, 6)); Check(answer == "ok"); Check(p.FinishReason == "length");
});

AsyncTest("HTTP rejection hides remote body and releases reservation", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-remote-content") })));
    var p = new CompletionProgress();
    try { await new CompletionClient(http).StreamAsync(defaults, "test", "q", _ => { }, p, CancellationToken.None); throw new Exception("Expected error"); }
    catch (ApiException ex) { Check(!ex.Message.Contains("secret-remote-content")); Check(p.Rejected); }
});

AsyncTest("Interrupted stream preserves content and reports failure", async () =>
{
    using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Sse("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"))));
    var text = "";
    await ThrowsAsync<ApiException>(() => new CompletionClient(http).StreamAsync(defaults, "test", "q", s => text += s, new CompletionProgress(), CancellationToken.None)); Check(text == "partial");
});

AsyncTest("Cancellation interrupts network wait", async () =>
{
    using var http = new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(5000, ct); return Sse(""); }));
    using var cts = new CancellationTokenSource(100);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    await ThrowsAsync<OperationCanceledException>(() => new CompletionClient(http).StreamAsync(defaults, "test", "q", _ => { }, new CompletionProgress(), cts.Token));
    Check(watch.Elapsed < TimeSpan.FromSeconds(2));
});

AsyncTest("Pre-canceled request sends no data", async () =>
{
    var called = false;
    using var http = new HttpClient(new FakeHandler((_, _) => { called = true; return Task.FromResult(Sse("")); }));
    var progress = new CompletionProgress();
    await ThrowsAsync<OperationCanceledException>(() => new CompletionClient(http).StreamAsync(defaults, "test", "q", _ => { }, progress, new CancellationToken(true)));
    Check(!called && !progress.Started);
});

AsyncTest("SSE error and malformed data report failure", async () =>
{
    foreach (var data in new[] { "data: {\"error\":{\"message\":\"private\"}}\n\n", "data: {broken}\n\n" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Sse(data))));
        try { await new CompletionClient(http).StreamAsync(defaults, "test", "q", _ => { }, new CompletionProgress(), CancellationToken.None); throw new Exception("Expected error"); }
        catch (Exception ex) when (ex is ApiException or JsonException) { Check(!ex.Message.Contains("private")); }
    }
});

Test("Desktop startup initializes tray/hotkey and renders compact layouts", () =>
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        MainWindow? window = null;
        try
        {
            var app = new App(); app.InitializeComponent();
            window = new MainWindow(new LocalStore(Path.Combine(root, "desktop")), defaults with { Hotkey = "Ctrl+Alt+Shift+F9" });
            window.Show();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(((System.Windows.Controls.TextBlock)window.FindName("ShortcutLabel")).Text == "Ctrl+Alt+Shift+F9 唤起", "Global hotkey must initialize after XAML creates a window handle");
            Check(!((System.Windows.Controls.Button)window.FindName("AskButton")).IsEnabled, "Empty query must be disabled");
            window.Width = 400; window.Height = 420; window.UpdateLayout();
            Check(((System.Windows.Controls.TextBox)window.FindName("Question")).ActualWidth > 180, "Compact question width: " + ((System.Windows.Controls.TextBox)window.FindName("Question")).ActualWidth);
            Check(((System.Windows.Controls.TextBox)window.FindName("Answer")).ActualHeight > 50, "Compact answer height: " + ((System.Windows.Controls.TextBox)window.FindName("Answer")).ActualHeight);
            foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
            {
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(400 * scale), (int)(420 * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window); Check(bitmap.PixelWidth == (int)(400 * scale));
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { window?.Quit(); }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start();
    Check(thread.Join(TimeSpan.FromSeconds(20)), "Desktop smoke test timed out");
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
});

var failures = 0;
try
{
    foreach (var (name, run) in tests)
    {
        try { await run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
}
finally { Directory.Delete(root, true); }
return failures == 0 ? 0 : 1;

static HttpResponseMessage Sse(string content) => new(HttpStatusCode.OK) { Content = new StreamContent(new ByteStream(Encoding.UTF8.GetBytes(content))) };
sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class ByteStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
