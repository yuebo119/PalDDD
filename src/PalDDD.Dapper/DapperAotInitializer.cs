// ═══════════════════════════════════════════════════════════════
// 🚀 DapperAotInitializer — SQLite TypeHandler 注册（AOT 就绪诊断）
// ═══════════════════════════════════════════════════════════════
// 💡 设计说明（2026-09-13 探针实证更新，Dapper.AOT 1.0.52 → 1.1.0）：
//   ｜ 1.1.0 实证矩阵（scratch 项目 NativeAOT 二进制实跑，非文档推断）：
//   ｜   ✅ const 直引 / 实例属性 / switch 选常量三形状全部生成拦截器（属性与 switch
//   ｜      均被编译期常量追踪——二十五轮顾虑的方言分支形状实际被支持）；
//   ｜   ✅ 拦截器在 .NET 11 接线（JIT 堆栈走生成代码 + AOT 实跑全绿）——c-sharpcorner
//   ｜      "1.0.52 拦截器不接线"指控对 1.1.0 不成立；
//   ｜   ✅ 声明式 [Dapper.TypeHandler] 消费经典 SqlMapper.TypeHandler<T>（shim），
//   ｜      "迁移三个 handler 基类"前置免除（推翻二十五轮 A5 勘正的基类迁移项）；
//   ｜   ❌ CommandDefinition 拼写照 DAP057 拒绝不生成拦截器；
//   ｜   ❌ 带匿名参数的 CommandDefinition 在 NativeAOT 下 PlatformNotSupportedException
//   ｜      （经典路径参数绑定走 Reflection.Emit，AOT 禁用——"AOT 假象"的运行时根因）。
//   ｜ 唯一真实启用障碍：ct 只能经 CommandDefinition 传递（直接重载无 ct 参数，CS1739 实证），
//   ｜   拦截器只支持直接重载——二者互斥。2026-09-13 用户裁决："铺垫不动"（ct 保留，AOT
//   ｜   需求由 PalORM 栈承担），本文件即铺垫完成态。
//   ｜ 启用动作清单（未来若重启）：① [module: DapperAot] 启用 ② 全部调用点 CommandDefinition
//   ｜   → 直接重载（SQL 执行层 ct 收缩，连接超时兜底）③ 删下方运行时注册（声明式接管）
//   ｜   ④ csproj 移除 NoWarn 的 DAP005 ⑤ NativeAOT 发布实测。
//   ｜ 双轨现状（探针实证）：未启用 DapperAot 时声明式特性不被消费（无效无害），运行时
//   ｜   注册是经典路径的必要注册；启用后生成代码改走声明式，运行时注册变冗余可删。
// ═══════════════════════════════════════════════════════════════

using System.Runtime.CompilerServices;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using PalUlid = ByteAether.Ulid.Ulid;

// 🔬 实验分支（experiment/dapper-aot-full）：全量启用——调用点已全部改直接重载（34 处，
//    ct 收缩为实验目的显式接受），声明式 TypeHandler 接管，运行时注册退役。
[module: DapperAot]

// 声明式 TypeHandler（1.1.0 特性）：启用后生成代码据此绑定（DAP053 指导口径），
// 经典 SqlMapper.TypeHandler<T> 经 shim 消费，基类无需迁移。
[assembly: Dapper.TypeHandler(typeof(PalUlid), typeof(global::PalDDD.Dapper.SqliteUlidTypeHandler))]
[assembly: Dapper.TypeHandler(typeof(Guid), typeof(global::PalDDD.Dapper.SqliteGuidTypeHandler))]
[assembly: Dapper.TypeHandler(typeof(DateTimeOffset), typeof(global::PalDDD.Dapper.SqliteDateTimeOffsetTypeHandler))]

namespace PalDDD.Dapper;

/// <summary>Dapper.AOT 全局状态初始化（AOT 模式下仅列名映射，TypeHandler 走声明式）</summary>
internal static class DapperAotInitializer
{
    [SuppressMessage("Usage", "CA2255",
        Justification = "snake_case 列名映射是绕过 DI 直连构造场景的必要全局状态，模块初始化器保证直连构造自足。")]
    [ModuleInitializer]
    public static void Initialize()
    {
        // 运行时 TypeHandler 注册已退役（声明式 [Dapper.TypeHandler] 接管，见上）。
        // P2 修复（十一轮·实测发现）保留：snake_case 列名 → PascalCase 属性映射——
        // ⚠️ 实验验证点：经典 Dapper 消费此全局设置；AOT 生成 RowFactory 是否同样尊重
        // 尚未实证（snake_case 测试若红即命中此风险，处置见实验报告）。
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
