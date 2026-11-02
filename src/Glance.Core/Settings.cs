using System.Text;
using System.Text.Json;

namespace Glance.Core;

public sealed record Settings
{
    public string Endpoint { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-flash";
    public string EncryptedKey { get; init; } = "";
    public string Hotkey { get; init; } = "Ctrl+Alt+Space";
    public string SystemPrompt { get; init; } = "你是桌面知识查询助手。直接回答当前问题，默认用简体中文。先用一句话解释，再给必要的要点或一个例子。简明、准确，约150字。使用纯文本，避免Markdown标记。遇到歧义时简短列出常见含义。";
    public bool Thinking { get; init; }
    public double Temperature { get; init; } = 0.5;
    public double TopP { get; init; } = 1;
    public int MaxTokens { get; init; } = 512;
    public int TimeoutSeconds { get; init; } = 90;
    public decimal InputRate { get; init; } = 0.3m;
    public decimal OutputRate { get; init; } = 1.2m;
    public decimal RequestLimit { get; init; } = 0.02m;
    public decimal DailyLimit { get; init; } = 0.5m;
    public decimal MonthlyLimit { get; init; } = 5m;
    public string ExtraJson { get; init; } = "{}";

    public Uri CompletionUri()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("接口须使用 HTTPS；本机调试可使用 HTTP。请填写基础地址或完整 chat/completions 地址。修正地址中的查询参数或用户信息。");
        var url = uri.AbsoluteUri.TrimEnd('/');
        return new Uri(url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? url : url + "/chat/completions");
    }

    public void Validate()
    {
        _ = CompletionUri();
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 128) throw new ArgumentException("请填写模型名称（最多128字符）。");
        if (string.IsNullOrWhiteSpace(SystemPrompt) || SystemPrompt.Length > 8000) throw new ArgumentException("回答指令须为1至8000字符。");
        if (!double.IsFinite(Temperature) || Temperature < 0 || Temperature > 2 || !double.IsFinite(TopP) || TopP <= 0 || TopP > 1)
            throw new ArgumentException("Temperature 范围为0至2；Top P 范围为大于0且至多1。");
        if (MaxTokens < 1 || MaxTokens > 32768 || TimeoutSeconds < 10 || TimeoutSeconds > 600) throw new ArgumentException("输出长度范围为1至32768 tokens；超时范围为10至600秒。");
        if (InputRate <= 0 || OutputRate <= 0 || RequestLimit <= 0 || DailyLimit <= 0 || MonthlyLimit <= 0)
            throw new ArgumentException("单价与费用限额须大于0。");
        using var extra = JsonDocument.Parse(ExtraJson);
        if (extra.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("附加参数须为 JSON 对象。");
        string[] protectedFields = ["model", "messages", "stream", "stream_options", "max_tokens", "max_completion_tokens", "thinking", "reasoning_effort", "n", "tools", "tool_choice", "functions", "function_call", "temperature", "top_p"];
        foreach (var property in extra.RootElement.EnumerateObject())
            if (protectedFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"{property.Name} 请通过上方专用设置编辑；工具调用和多回答参数已禁用。");
    }

    // UTF-8 bytes deliberately over-reserve input for the supported text models.
    public int InputTokenReserve(string question) => Encoding.UTF8.GetByteCount(SystemPrompt) + Encoding.UTF8.GetByteCount(question) + 256;
    public decimal Reserve(string question) => (InputTokenReserve(question) * InputRate + MaxTokens * OutputRate) / 1_000_000m;
}
