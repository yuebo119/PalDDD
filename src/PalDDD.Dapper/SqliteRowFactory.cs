// ─────────────────────────────────────────────────────────────
// 🔧 SqliteRowFactory — SQLite TEXT 列自定义类型解析器
// ─────────────────────────────────────────────────────────────
// 💡 问题：SQLite 将 Ulid/Guid/DateTimeOffset 存储为 TEXT 列。
//
// 💡 本类提供静态解析方法——供 Dapper.AOT 编译时拦截器或手动调用。
//
// 💡 AOT 状态：✅ DapperAotInitializer.cs 已启用 [module:DapperAot]。
//   TypeHandler 通过 [ModuleInitializer] 注册，Dapper.AOT 拦截器编译时发现
//   并直接生成调用代码，零运行时 IL.Emit。
//   RegisterTypeHandlers() 保留作为回退和非 AOT 场景的手动入口。
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
