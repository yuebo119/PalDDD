// ─────────────────────────────────────────────────────────────
// 📦 Unit — 空返回类型（替代 void）
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Core;

/// <summary>
/// 零大小结构体，替代 void 用于无返回值的命令/查询。
/// <para>
/// 💡 通俗解释 —— 为什么需要 Unit 类型？<br/>
/// 在 C# 泛型中，void 不能作为类型参数。当你有一个命令处理器接口
/// ICommandHandler&lt;TCommand, TResult&gt; 但某些命令没有返回值时，<br/>
/// 你会写 ICommandHandler&lt;DoSomethingCommand, Unit&gt; 而不是被迫定义两套接口。<br/>
/// 这类似于 F# 的 unit 类型或 Haskell 的 () 类型。
/// </para>
/// <para>
/// 💡 为什么用 readonly record struct？<br/>
/// 1. 零大小 —— 编译器会优化掉 Unit 实例的存储<br/>
/// 2. 值相等性 —— 所有 Unit 实例都相等<br/>
/// 3. 栈分配 —— 无堆压力
/// </para>
/// <para>✅ AOT 安全：纯值类型，无虚方法，编译期完全确定。</para>
/// </summary>
public readonly record struct Unit
{
    /// <summary>返回 "()" 表示空值 —— 模仿 F# 的 unit 表示法</summary>
    public override string ToString() => "()";
}
