using System.Collections.Concurrent;
using Terminal.Gui;

namespace ParrotCode;

/// <summary>
/// IHitlGate 实现（迭代 7c-3：内联提示，不用模态 Dialog）。
/// 保持 IHitlGate 接口不变——BatchToolExecutor 的注入方式不变。
///
/// 工作流程：
/// 1. RequestAsync 检查缓存命中 → 直接返回 AllowSession
/// 2. 调用 UI 回调在主线程显示内联提示（状态行 Label + Button）
/// 3. 用户点击 Button 后，UI 回调返回决策
/// 4. 缓存会话级允许
///
/// 相比 Dialog 方案的优势：
/// - 无模态弹窗，无阴影渲染问题（Dialog 阴影在透明背景下会导致乱码/错乱）
/// - 内联提示符合 CLI 工具传统交互模式（如 ClaudeCode）
/// - 复用已有的状态行位置
/// </summary>
public sealed class HitlPrompt : IHitlGate
{
    private readonly ConcurrentDictionary<string, byte> _sessionCache = new();
    private readonly Func<ToolCall, CancellationToken, Task<HitlDecision>> _uiCallback;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="uiCallback">UI 回调：在主线程显示内联提示，等待用户点击 Button，返回决策。</param>
    internal HitlPrompt(Func<ToolCall, CancellationToken, Task<HitlDecision>> uiCallback)
    {
        _uiCallback = uiCallback ?? throw new ArgumentNullException(nameof(uiCallback));
    }

    public async Task<HitlDecision?> RequestAsync(ToolCall call, CancellationToken cancellationToken)
    {
        // 1. 会话缓存命中——直接返回 AllowSession（不提示）
        if (_sessionCache.ContainsKey(call.Name))
            return new HitlDecision(HitlChoice.AllowSession);

        // 2. 取消时立即返回 Deny
        if (cancellationToken.IsCancellationRequested)
            return HitlDecision.Deny("已取消");

        // 3. 调用 UI 回调（在主线程显示内联提示，等待用户点击 Button）
        var decision = await _uiCallback(call, cancellationToken);

        // 4. 缓存会话级允许
        if (decision.ShouldCache)
            _sessionCache[call.Name] = 0;

        return decision;
    }

    public bool IsAllowedThisSession(string toolName) => _sessionCache.ContainsKey(toolName);
}
