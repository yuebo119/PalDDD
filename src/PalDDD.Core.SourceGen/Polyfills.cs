// netstandard2.0 兼容性填充
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    { }
}

// P3 修复（十七轮）：删除 System.Index polyfill 死代码——已 grep 确认三个生成器
// （Identity/Enum/MessageRegistry）的生成模板均未使用 ^ 索引或 Range 语法
// （[.. x] 为 C# 12 集合表达式 spread，不依赖 Index/Range），polyfill 无消费方。
