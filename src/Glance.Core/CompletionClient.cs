using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Glance.Core;

public sealed record Usage(int InputTokens, int OutputTokens);
public sealed class ApiException(string message) : Exception(message);
public sealed class CompletionProgress
{
    public Usage? Usage { get; set; }
    public string? FinishReason { get; set; }
    public bool Rejected { get; set; }
    public bool Started { get; set; }
}

public sealed class CompletionClient(HttpClient http)
{
    public async Task StreamAsync(Settings settings, string key, string question, Action<string> onText, CompletionProgress progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model.Trim(),
            ["messages"] = new[] { new { role = "system", content = settings.SystemPrompt }, new { role = "user", content = question } },
            ["stream"] = true, ["stream_options"] = new { include_usage = true },
            ["thinking"] = new { type = settings.Thinking ? "enabled" : "disabled" },
            ["max_tokens"] = settings.MaxTokens, ["temperature"] = settings.Temperature, ["top_p"] = settings.TopP
        };
        using var extra = JsonDocument.Parse(settings.ExtraJson);
        foreach (var p in extra.RootElement.EnumerateObject()) payload[p.Name] = p.Value.Clone();
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.CompletionUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        cancellationToken.ThrowIfCancellationRequested();
        progress.Started = true;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            progress.Rejected = true;
            throw new ApiException((int)response.StatusCode switch
            {
                401 or 403 => "Key 或接口访问权限有误，请在设置中检查。",
                402 => "服务账户余额不足，请检查 DeepSeek 账户。",
                429 => "服务请求频率达到上限，请稍后再试。",
                >= 500 => "模型服务暂时繁忙，请稍后再试。",
                _ => $"请求被服务拒绝（HTTP {(int)response.StatusCode}），请检查模型与参数。"
            });
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var eventData = new StringBuilder();
        var completed = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                if (eventData.Length > 0) completed |= Process(eventData.ToString(), onText, progress);
                break;
            }
            if (line.Length == 0)
            {
                if (eventData.Length > 0)
                {
                    completed |= Process(eventData.ToString(), onText, progress);
                    eventData.Clear();
                    if (completed) break;
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventData.Length > 0) eventData.Append('\n');
                eventData.Append(line.AsSpan(5).TrimStart());
                if (eventData.Length > 2_000_000) throw new ApiException("服务返回的数据过长，请检查接口。");
            }
        }
        if (!completed && progress.FinishReason is null) throw new ApiException("连接提前结束，已保留收到的内容。可重新查询。");
        if (progress.FinishReason is not null and not "stop" and not "length") throw new ApiException("服务中断了回答，已保留收到的内容。");
    }

    private static bool Process(string data, Action<string> onText, CompletionProgress progress)
    {
        if (data.Trim() == "[DONE]") return true;
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) throw new ApiException("服务返回错误，请检查模型与参数后重试。");
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
            usage.TryGetProperty("prompt_tokens", out var input) && usage.TryGetProperty("completion_tokens", out var output))
        {
            var i = input.GetInt32(); var o = output.GetInt32();
            if (i < 0 || o < 0) throw new ApiException("服务用量数据异常。");
            progress.Usage = new Usage(i, o);
        }
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String) progress.FinishReason = finish.GetString();
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                onText(content.GetString()!);
        }
        return false;
    }
}
