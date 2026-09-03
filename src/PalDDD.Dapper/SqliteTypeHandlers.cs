// ─────────────────────────────────────────────────────────────
// 🔧 SQLite 类型处理器 — Dapper TypeHandler
// ─────────────────────────────────────────────────────────────
// 💡 为什么需要？
//   ｜ SQLite 的 TEXT 列返回 string，无法直接 cast 为 Ulid/Guid/DateTimeOffset。
//   ｜ 通过 Dapper.SqlMapper.TypeHandler<T> 继承，经典 Dapper 运行时路径注册。
//
// ⚠️ AOT 状态：[module:DapperAot] 当前未启用（DapperAotInitializer.cs:25（v10 勘正：行号 21→25） 注释禁用）。
//   TypeHandler 通过 [ModuleInitializer] 注册，经典 Dapper IL.Emit 反射物化路径生效。
//   NativeAOT 发布时会运行时失败——PalORM 适配层（PalDDD.PalORM）提供真 AOT 替代。
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
            string s => PalUlid.Parse(s, CultureInfo.InvariantCulture), // v64 对齐族形态标准（v62 Guid 先例）
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
            // v27 P3（B 片 N10）：解析样式统一 AssumeUniversal——与姊妹 SqliteRowFactory.
            // ParseDateTimeOffset 的 string 分支对齐（原 RoundtripKind 为两文件仅存分叉）。
            // 库存均经 SetValue 以 "O" 带偏移格式写入，两种样式 round-trip 等价；差异仅在
            // 无偏移脏数据（手工运维写入等）：AssumeUniversal 按 UTC 解释（偏移 +00:00），
            // 与库内 UTC 存储约定一致且不依赖宿主时区，语义统一。
            string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            DateTimeOffset d => d,
            // P1 修复（七轮评审）：Npgsql timestamptz / MySqlConnector DATETIME 的
            // GetValue 返回 DateTime——此 handler 经 [ModuleInitializer] 全局注册，
            // Dapper 对注册类型把原始盒装值直接喂给 Parse。缺 DateTime 分支时
            // PG/MySQL 读路径全部 InvalidCastException。
            DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateTimeOffset")
        };
    }
}
