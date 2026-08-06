using System.Text;
using System.Text.Json;

namespace ParrotCode;

/// <summary>
/// 工具调用累积器：按 index 拼接 OpenAI 流式 tool_calls 分片。
/// 协议无关——任何按 index 分片的 tool_calls（OpenAI/兼容协议）都适用。
/// AgentLoop 内部使用，Provider 只 yield ChatChunk.ToolCallDelta 分片。
/// </summary>
internal sealed class ToolCallAccumulator
{
    private readonly Dictionary<int, AccEntry> _entries = new();

    private sealed class AccEntry
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Arguments = new();
    }

    /// <summary>
    /// 累积一个分片。Id/Name 取首个非空值，Arguments 拼接所有片段。
    /// </summary>
    public void Accumulate(int index, string? id, string? name, string? argsFragment)
    {
        if (!_entries.TryGetValue(index, out var entry))
        {
            entry = new AccEntry();
            _entries[index] = entry;
        }
        if (id is not null) entry.Id = id;
        if (name is not null) entry.Name = name;
        if (argsFragment is not null) entry.Arguments.Append(argsFragment);
    }

    /// <summary>
    /// 构建完整 ToolCall 列表（按 index 升序）。
    /// Arguments 字符串解析为 JsonElement；空或非法 JSON 用空对象兜底。
    /// </summary>
    public IReadOnlyList<ToolCall> Build()
    {
        var result = new List<ToolCall>(_entries.Count);
        foreach (var kv in _entries.OrderBy(x => x.Key))
        {
            var entry = kv.Value;
            var argsStr = entry.Arguments.ToString();
            JsonElement input;
            if (string.IsNullOrWhiteSpace(argsStr))
            {
                input = JsonDocument.Parse("{}").RootElement.Clone();
            }
            else
            {
                try
                {
                    input = JsonDocument.Parse(argsStr).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // LLM 生成的 arguments 非法 JSON——用空对象兜底，
                    // 工具执行时 GetRequiredString 会返回"缺少必需参数"错误，回灌给 LLM 自我修正
                    input = JsonDocument.Parse("""{"_parse_error":"arguments 非法 JSON"}""").RootElement.Clone();
                }
            }
            result.Add(new ToolCall(Id: entry.Id ?? $"call_{kv.Key}", Name: entry.Name ?? string.Empty, Input: input));
        }
        return result;
    }

    public bool IsEmpty => _entries.Count == 0;
}
