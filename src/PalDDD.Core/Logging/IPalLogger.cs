// ═══════════════════════════════════════════════════════════════
// 📝 IPalLogger<T> — 框架统一日志门面
// ═══════════════════════════════════════════════════════════════
// 💡 设计原则：
//   ｜ 框架代码只依赖 IPalLogger<T>，不直接依赖 ILogger<T> 或 ZLogger
//   ｜ 当前默认实现为 ZLogger（零分配 UTF8 结构化日志）
//   ｜ 未来替换只需修改 AddPalLogging() 中的注册，消费方零改动
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;

namespace PalDDD.Core.Logging;

/// <summary>框架统一日志门面——当前实现为 ZLogger，未来可替换。</summary>
/// <remarks>
/// ⚠️ <b>message 模板语义（v38 P3 公共接口可见化，对齐 PalLogger 适配层 CA2254 抑制声明
/// "IPalLogger 门面接收纯字符串消息，模板由调用方构造"）</b>：message 按 .NET 日志模板
/// 解析——花括号 <c>{...}</c> 被下游（MEL/ZLogger）视为结构化槽位而非字面量，输出字面
/// 花括号需转义（<c>{{</c> / <c>}}</c>）。结构化参数请用带参重载而非向 message 拼接——
/// 当前接口未提供带参重载（接口扩展属破坏性变更，随 v3.0 一并裁决），结构化参数场景
/// 暂走底层 <c>ILogger&lt;T&gt;</c>。
/// </remarks>
public interface IPalLogger<T>
{
    /// <summary>调试日志</summary>
    void Debug(string message);

    /// <summary>信息日志</summary>
    void Information(string message);

    /// <summary>警告日志</summary>
    void Warning(string message);

    /// <summary>错误日志 · 必须携带异常</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Error 是日志领域的标准命名，与 ILogger 的 LogError 扩展方法一致。")]
    void Error(Exception ex, string message);

    /// <summary>是否启用指定级别——用于避免昂贵的参数计算</summary>
    bool IsEnabled(LogLevel level);
}
