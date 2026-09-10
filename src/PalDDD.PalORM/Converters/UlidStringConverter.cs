using System.Globalization;
using ByteAether.Ulid;
using PalORM;

namespace PalDDD.PalORM.Converters;

/// <summary>
/// Ulid ↔ string 编译期值转换器。
/// <para>
/// PalORM 白名单不含 <see cref="Ulid"/>（PALORM016）—— 必须通过 <c>[Converter(typeof(UlidStringConverter))]</c>
/// 显式声明转换。Provider 端用 26 字符 Crockford Base32 字符串（<see cref="Ulid.ToString()"/>），
/// 与 PalDDD.Dapper 的 SQLite TypeHandler 序列化方式一致（TEXT 列存储）。
/// </para>
/// <para><b>AOT 安全</b>：源生成器在编译期 emit 调用代码，零反射。转换器自身为顶级 public 非泛型类 + 无参构造，满足 <see cref="ConverterAttribute"/> 约束。</para>
/// <para>
/// <b>v65 P3（脏数据降级）</b>：<see cref="FromProvider"/> 改用 <see cref="Ulid.TryParse(string, IFormatProvider, out Ulid)"/>
/// ——严格 <c>Ulid.Parse</c> 对 NULL/畸形字符串抛 <c>FormatException</c>，异常从源生成物化路径深处逃逸
/// 且无列上下文。降级语义对齐 <c>DapperEventLog.EventLogRow.ParseUlid</c> / <c>OutboxMessageRow.TryParseUlid</c>：
/// 失败返回 <see cref="Ulid.Empty"/>（本接口返回类型为非空 <see cref="Ulid"/>，"null 降级"对应空 Ulid）。
/// ⚠️ 副作用：PK 列脏数据不再响亮失败，而是物化为 <see cref="Ulid.Empty"/>——消费方若需区分
/// "真实空值"与"解析失败"，请在领域层校验 <c>!= Ulid.Empty</c>。
/// </para>
/// </summary>
public sealed class UlidStringConverter : IValueConverter<Ulid, string>
{
    /// <inheritdoc />
    public string ToProvider(Ulid value) => value.ToString();

    /// <inheritdoc />
    public Ulid FromProvider(string value)
        // 显式判 null（镜像 OutboxMessageRow.TryParseUlid 的前置判定）：DB NULL 可能以 null 字符串
        // 到达（形参声明非空但 PalORM 物化路径不强制），不依赖 TryParse(null) 的宽容行为。
        => value is not null && Ulid.TryParse(value, CultureInfo.InvariantCulture, out var ulid) ? ulid : Ulid.Empty;
}
