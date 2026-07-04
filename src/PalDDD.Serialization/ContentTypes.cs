// ─────────────────────────────────────────────────────────────
// 📋 ContentTypes — 标准内容类型常量
// ─────────────────────────────────────────────────────────────
namespace PalDDD.Serialization;

/// <summary>已知消息负载内容类型常量。</summary>
public static class ContentTypes
{
    /// <summary>由 System.Text.Json 序列化的 UTF-8 JSON 负载。</summary>
    public const string Json = "application/json";

    /// <summary>MemoryPack 二进制负载。</summary>
    public const string MemoryPack = "application/x-memorypack";
}
