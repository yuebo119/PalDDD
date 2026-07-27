using PalORM;

namespace PalDDD.PalORM.Converters;

/// <summary>
/// byte[] ↔ Base64 string 编译期值转换器。
/// <para>
/// PalORM 白名单（PALORM016）拒绝 <c>byte[]</c> —— 必须通过 <c>[Converter(typeof(ByteArrayBase64Converter))]</c>
/// 转为 Base64 字符串存储。覆盖 Outbox.Payload / EventLog.Payload / EventLog.Metadata / Idempotency.ResponsePayload 等二进制列。
/// </para>
/// <para><b>存储格式</b>：Base64 字符串（TEXT 列）。与 Dapper 实现的 BLOB 存储不兼容 —— 数据迁移时需 BASE64 解码脚本。</para>
/// <para><b>AOT 安全</b>：源生成器编译期 emit，零反射。Convert.ToBase64String/FromBase64String 为 BCL 静态方法，trimming 安全。</para>
/// </summary>
public sealed class ByteArrayBase64Converter : IValueConverter<byte[], string>
{
    /// <inheritdoc />
    public string ToProvider(byte[] value) => Convert.ToBase64String(value);

    /// <inheritdoc />
    public byte[] FromProvider(string value) => Convert.FromBase64String(value);
}
