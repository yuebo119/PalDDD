// ═══════════════════════════════════════════════════════════════
// 🚀 DapperAotInitializer — SQLite TypeHandler 注册（AOT 就绪诊断）
// ═══════════════════════════════════════════════════════════════
// 💡 设计说明：
//   ｜ Dapper.AOT v1.0.52 存在 TypeHandler 双向限制：
//   ｜   • 参数绑定 (SetValue): 直传原始 Ulid/DateTimeOffset → SqliteParameter.Bind 失败
//   ｜   • 结果读取 (Parse): RowFactory.GetValue<T> 无法将 string 转为 Ulid
//   ｜ 因此 [module:DapperAot] 暂不全局启用——保留在文件注释中供未来版本使用。
//   ｜ 当前通过 [ModuleInitializer] 注册 TypeHandler，经典 Dapper 运行时路径生效。
//   ｜ Dapper 查询参数中的 Ulid/Guid/DateTimeOffset 已通过 ToSqliteParameter() 手动转为 string。
//   ｜ 待 Dapper.AOT v1.0.53+ 修复 TypeHandler 双向支持后，取消注释 [module:DapperAot]。
// ═══════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using PalUlid = ByteAether.Ulid.Ulid;

// 🔧 待 Dapper.AOT v1.0.53+ 修复 TypeHandler 双向支持后启用
// [module: DapperAot]

namespace PalDDD.Dapper;

/// <summary>Dapper.AOT 编译时 TypeHandler 注册 + AOT 参数适配器</summary>
internal static class DapperAotInitializer
{
    [SuppressMessage("Usage", "CA2255",
        Justification = "Dapper.AOT 源生成器需要编译时可见的 TypeHandler 注册。模块初始化器确保在首次 Dapper 查询前完成注册。")]
    [ModuleInitializer]
    public static void Initialize()
    {
        SqlMapper.AddTypeHandler(new SqliteUlidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
        SqlMapper.AddTypeHandler(new SqliteDateTimeOffsetTypeHandler());
    }

    /// <summary>将 Ulid 转为 string，适配 SQLite TEXT 列参数绑定。</summary>
    public static object ToSqliteParameter(PalUlid value) => value.ToString();

    /// <summary>将 Guid 转为 string，适配 SQLite TEXT 列参数绑定。</summary>
    public static object ToSqliteParameter(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>将 DateTimeOffset 转为 string，适配 SQLite TEXT 列参数绑定。</summary>
    public static object ToSqliteParameter(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// MySQL 方言时间参数（P2 修复）：DATETIME(6) 列与带时区偏移的 "O" 格式比较依赖
    /// session tz 换算（8.0.19+ 才支持偏移字面量），非 UTC session 时租约判定漂移——
    /// 统一用无偏移 UTC "yyyy-MM-dd HH:mm:ss.ffffff"。
    /// </summary>
    public static object ToMySqlParameter(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
