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
/// </summary>
public sealed class UlidStringConverter : IValueConverter<Ulid, string>
{
    /// <inheritdoc />
    public string ToProvider(Ulid value) => value.ToString();

    /// <inheritdoc />
    public Ulid FromProvider(string value) => Ulid.Parse(value);
}
