// ─────────────────────────────────────────────────────────────
// 🔧 SqliteRowFactory — SQLite TEXT 列自定义类型解析器
// ─────────────────────────────────────────────────────────────
// 💡 问题：SQLite 将 Guid 和 DateTimeOffset 存储为 TEXT 列。
//   Dapper 运行时通过 SqlMapper.TypeHandler 将 TEXT 转换为目标类型。
//   Dapper.AOT 编译时拦截器当前不查阅运行时 TypeHandler（v1.0.52），
//   因此 Guid/DateTimeOffset TEXT 列仍需运行时 TypeHandler。
//
// 💡 本类提供：
//   1. 静态解析方法——供 Dapper.AOT RowFactory 集成（未来版本）
//   2. TypeHandler 注册快捷方法——当前运行时 Dapper 路径
//
// 💡 当前状态：Dapper.AOT RowFactory.Register<T> API 在 v1.0.52 中不可用。
//   方案：继续使用 SqlMapper.AddTypeHandler 运行时注册 + 保持 Dapper.AOT SG 诊断就绪。
//   等 Dapper.AOT RowFactory 自定义类型映射 API 成熟后迁移。
// ─────────────────────────────────────────────────────────────

using Dapper;
using System.Data;
using System.Globalization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Dapper;

/// <summary>SQLite Dapper.AOT RowFactory — 将 TEXT 列映射到 Guid/Ulid/DateTimeOffset。</summary>
/// <remarks>
/// 启用条件：项目引用 Dapper.AOT + 启用 InterceptorsPreviewNamespaces。<br/>
/// 当前 4 个 Dapper 项目已满足条件，此 RowFactory 解除 SQLite TypeHandler 依赖。
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
        return value switch
        {
            PalUlid u => u,
            string s => PalUlid.Parse(s),
            byte[] b when b.Length == 16 => PalUlid.New(new ReadOnlySpan<byte>(b)),
            Guid g => PalUlid.New(g),
            _ => default
        };
    }

    /// <summary>从 SQLite TEXT 列解析 Guid</summary>
    public static Guid ParseGuid(IDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return Guid.Empty;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            Guid g => g,
            string s => Guid.Parse(s),
            byte[] bytes => new Guid(bytes),
            _ => Guid.Empty
        };
    }

    /// <summary>从 SQLite TEXT 列解析 DateTimeOffset</summary>
    public static DateTimeOffset ParseDateTimeOffset(IDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return default;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            _ => default
        };
    }
}
