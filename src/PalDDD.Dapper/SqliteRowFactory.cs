// ─────────────────────────────────────────────────────────────
// 🔧 SqliteRowFactory — SQLite TEXT 列自定义类型解析器
// ─────────────────────────────────────────────────────────────
// 💡 问题：SQLite 将 Ulid/Guid/DateTimeOffset 存储为 TEXT 列。
//
// 💡 本类提供静态解析方法——供手动调用或经典 Dapper 路径使用。
//
// ⚠️ AOT 状态：[module:DapperAot] 当前未启用（DapperAotInitializer.cs:21 注释禁用）。
//   TypeHandler 通过 [ModuleInitializer] 注册，经典 Dapper 运行时路径生效。
//   QueryAsync<T> 走 IL.Emit 反射物化，NativeAOT 发布会失败。
//   PalORM 适配层（PalDDD.PalORM）提供真 AOT 路径替代。
// ─────────────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Dapper;

/// <summary>SQLite Dapper RowFactory — 将 TEXT 列映射到 Guid/Ulid/DateTimeOffset。</summary>
/// <remarks>
/// ⚠️ AOT 状态：[module:DapperAot] 当前未启用——经典 Dapper 运行时路径生效，非 NativeAOT 兼容。<br/>
/// PalORM 适配层（PalDDD.PalORM）提供真 AOT 替代路径。
/// </remarks>
public static class SqliteRowFactory
{
    /// <summary>快速注册 SQLite TypeHandler（当前运行时 Dapper 路径）。<br/>
    /// 泛型 TypeHandler&lt;T&gt; 自动覆盖 T 和 T? 两种类型。</summary>
    public static void RegisterTypeHandlers()
    {
        SqlMapper.AddTypeHandler(new SqliteUlidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteDateTimeOffsetTypeHandler());
    }

    /// <summary>从 SQLite TEXT 列解析 Ulid</summary>
    public static PalUlid ParseUlid(IDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        var value = reader.GetValue(ordinal);
        // P3 修复（二十一轮）：未知类型兜底从静默 return default 改抛 InvalidCastException——
        // 对齐 SqliteTypeHandlers.Parse 语义（类型失配是数据契约错误，静默 default(Ulid)
        // 会把脏值伪装成合法 Ulid 往下游传）。
        return value switch
        {
            PalUlid u => u,
            string s => PalUlid.Parse(s),
            byte[] b when b.Length == 16 => PalUlid.New(new ReadOnlySpan<byte>(b)),
            Guid g => PalUlid.New(g),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Ulid")
        };
    }

    /// <summary>从 SQLite TEXT 列解析 Guid</summary>
    public static Guid ParseGuid(IDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return Guid.Empty;
        var value = reader.GetValue(ordinal);
        // P3 修复（二十一轮）：同 ParseUlid——静默 Guid.Empty 改抛（空 Guid 与合法值无法区分）
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s),
            byte[] bytes => new Guid(bytes),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to Guid")
        };
    }

    /// <summary>从 SQLite TEXT 列解析 DateTimeOffset</summary>
    public static DateTimeOffset ParseDateTimeOffset(IDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        var value = reader.GetValue(ordinal);
        // P3 修复（二十一轮）：同 ParseUlid——静默 default(DateTimeOffset)（0001-01-01）改抛
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset")
        };
    }
}
