// ═══════════════════════════════════════════════════════════════
// 🚀 DapperAotInitializer — SQLite TypeHandler 注册（AOT 就绪诊断）
// ═══════════════════════════════════════════════════════════════
// 💡 设计说明：
//   ｜ 优化（二十五轮 API 扫描 A5）勘正：旧注释称"Dapper.AOT v1.0.52 存在 TypeHandler
//   ｜   双向限制，待 v1.0.53+ 修复后启用"——XML 核实 1.0.52 已含完整双向面：
//   ｜   Dapper.TypeHandler<T>.SetValue(DbParameter, T) / Parse(DbParameter) +
//   ｜   TypeHandlerAttribute<,>（Dapper.AOT 1.0.52 net8.0 XML 证实），API 前提成立，
//   ｜   "等上游修复"不成立。
//   ｜ 真实障碍：本文件注册的三个 handler 继承经典 SqlMapper.TypeHandler<T>——
//   ｜   Parse(object) 装箱签名，未对齐 AOT 侧 Dapper.TypeHandler<T> 抽象。
//   ｜ 启用前置：迁移三个 handler 基类 + 全量回归 + NativeAOT 发布实测（迁移列为后续项）。
//   ｜ 当前通过 [ModuleInitializer] 注册 TypeHandler，经典 Dapper 运行时路径生效。
//   ｜ Dapper 查询参数中的 Ulid/Guid/DateTimeOffset 已通过 ToSqliteParameter() 手动转为 string。
// ═══════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using PalUlid = ByteAether.Ulid.Ulid;

// 🔧 待三个 handler 迁移至 Dapper.TypeHandler<T>（AOT 侧抽象，Parse(DbParameter) 非装箱签名）
//    + 全量回归 + NativeAOT 发布实测后启用（二十五轮 API 扫描 A5 勘正，见上）
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
        // P2 修复（十一轮·实测发现）：snake_case 列名 → PascalCase 属性映射是 Store 层
        // 正确性的必要全局状态，此前只在 DI 注册路径（DapperServiceCollectionExtensions）
        // 与测试夹具设置——绕过 DI 直连构造（公共构造签名显式支持）时字符串列静默映射为空。
        // 与 TypeHandler 同级放 ModuleInitializer，直连构造自足；DI/测试路径重复设置幂等。
        global::Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
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
