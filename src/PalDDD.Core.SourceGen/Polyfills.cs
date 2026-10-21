// netstandard2.0 兼容性填充
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    { }
}

// P3 修复（十七轮）：删除 System.Index polyfill 死代码——生成器<b>自身源码</b>未使用
// ^ 索引或 Range 语法（[.. x] 为 C# 12 集合表达式 spread，不依赖 Index/Range）。
// 2026-10-21 勘正：IdentityGenerator 的 Ulid 模板 emit 了 buffer[..written]，但该模板
// 文本由用户 net11 工程编译（自带 System.Index/Range），不在生成器 netstandard2.0 工程
// 编译，polyfill 仍无消费方。
