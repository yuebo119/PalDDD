// ─────────────────────────────────────────────────────────────
// 🔧 SQLite 类型处理器 — Dapper 运行时 TypeHandler
// ─────────────────────────────────────────────────────────────
// 💡 为什么需要？
//   ｜ SQLite 的 TEXT 列返回 string，无法直接 cast 为 Guid/DateTimeOffset。
//   ｜ Dapper Store 适配器使用运行时 Dapper（非 AOT 拦截），
//   ｜ 运行时 Dapper 查阅 TypeHandlerCache<T> 调用这些处理器完成转换。
//   ｜ 生产环境 PG/MySQL 使用原生 uuid/timestamptz 类型，无需 TypeHandler。
//
// ✅ AOT 安全性：
//   ✅ Dapper Store 适配器层依赖 DbConnection 运行时注入，不参与 AOT 发布
//   ✅ sealed class，无虚分派
//   ✅ 不依赖反射
//
// 📐 DDD 位置：基础设施层 — Dapper SQLite 特定，不影响领域/应用层。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Dapper;

/// <summary>Dapper 运行时 Ulid 类型处理器（SQLite TEXT ↔ Ulid）</summary>
public sealed class SqliteUlidTypeHandler : SqlMapper.TypeHandler<PalUlid>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, PalUlid value)
        => parameter.Value = value.ToString();

    /// <inheritdoc/>
    public override PalUlid Parse(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            string s => PalUlid.Parse(s),
            PalUlid u => u,
            byte[] b when b.Length == 16 => PalUlid.New(new ReadOnlySpan<byte>(b)),
            Guid g => PalUlid.New(g),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Ulid")
        };
    }
}

/// <summary>Dapper 运行时 Guid 类型处理器（SQLite TEXT ↔ Guid）</summary>
public sealed class SqliteGuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid value)
        => parameter.Value = value.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override Guid Parse(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            string s => Guid.Parse(s, CultureInfo.InvariantCulture),
            Guid g => g,
            byte[] b when b.Length == 16 => new Guid(b),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Guid")
        };
    }
}

/// <summary>Dapper 运行时 DateTimeOffset 类型处理器（SQLite TEXT ↔ DateTimeOffset）</summary>
public sealed class SqliteDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        => parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override DateTimeOffset Parse(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset d => d,
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset")
        };
    }
}
