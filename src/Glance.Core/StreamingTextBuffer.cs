using System.Text;

namespace Glance.Core;

// One bounded transcript, coalesced independently of network chunk frequency.
public sealed class StreamingTextBuffer(int maximumCharacters = 262144)
{
    private readonly object gate = new();
    private readonly StringBuilder text = new();
    private int consumed;
    public int Length { get { lock (gate) return text.Length; } }
    public string Text { get { lock (gate) return text.ToString(); } }
    public void Append(string chunk)
    {
        lock (gate)
        {
            if (chunk.Length > maximumCharacters - text.Length) throw new ApiException("回答已达本机显示上限，已保留收到的内容。");
            text.Append(chunk);
        }
    }
    public string Drain(int maximumCharacters = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        lock (gate)
        {
            if (consumed == text.Length) return "";
            var count = Math.Min(maximumCharacters, text.Length - consumed);
            if (consumed + count < text.Length && char.IsHighSurrogate(text[consumed + count - 1]) && char.IsLowSurrogate(text[consumed + count]))
                count += count == 1 ? 1 : -1;
            var pending = text.ToString(consumed, count);
            consumed += count;
            return pending;
        }
    }
}
