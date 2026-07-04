// ─────────────────────────────────────────────────────────────
// 🧩 PostgreSqlSharding — 分库分表路由策略（取模分片 + 一致性哈希）
// ─────────────────────────────────────────────────────────────
// AOT 安全性：
//   ✅ 纯整数运算 — 零反射，零依赖。
//   ✅ 数据源在 DI 启动时初始化，运行时只做路由选择。
//
// 分片策略：
//   1. 取模分片（ModSharding）— id % shardCount → 确定分片
//   2. 一致性哈希（ConsistentHash）— 减少扩缩容时的数据迁移
//
// 架构设计（DDD/Clean Architecture 友好）：
//   - 分片逻辑完全在基础设施层，领域层只传入分片键。
//   - 通过接口抽象：不同分片策略实现 IShardingStrategy。
//   - 支持表级分片（table_0, table_1）和库级分片（db_0, db_1）。
//
// 使用方式：
//   var sharding = new ModShardingStrategy(shardCount: 4);
//   var shardId = sharding.GetShardId(orderGuid);
//   var table = $"outbox_messages_{shardId}";
//   var db    = shardedSources[shardId];
// ─────────────────────────────────────────────────────────────

using Npgsql;
using System.Text;

namespace PalDDD.Dapper.PostgreSql;

// ── 分片策略接口 ──

/// <summary>分片策略接口 — 根据分片键返回分片 ID</summary>
public interface IShardingStrategy
{
    /// <summary>获取分片数量</summary>
    int ShardCount { get; }

    /// <summary>根据分片键计算分片 ID（0 ~ ShardCount-1）</summary>
    int GetShardId(Guid key);

    /// <summary>根据字符串分片键计算分片 ID</summary>
    int GetShardId(string key);

    /// <summary>根据整数分片键计算分片 ID</summary>
    int GetShardId(long key);
}

// ── 取模分片（最简单，扩缩容需全量迁移）──

/// <summary>取模分片策略。Guid 使用 FNV-1a 稳定哈希（非 Guid.GetHashCode）跨 .NET 版本一致。</summary>
public sealed class ModShardingStrategy : IShardingStrategy
{
    public int ShardCount { get; }

    public ModShardingStrategy(int shardCount)
    {
        if (shardCount <= 0) throw new ArgumentOutOfRangeException(nameof(shardCount));
        ShardCount = shardCount;
    }

    /// <summary>
    /// 使用 FNV-1a 哈希替代 Guid.GetHashCode()。
    /// 🔴 P0 修复：Guid.GetHashCode() 跨 .NET 版本不保证稳定，.NET 升级后数据全部分片错位。
    /// FNV-1a 是确定性哈希，所有 .NET 版本计算结果一致。
    /// </summary>
    public int GetShardId(Guid key) => (int)(StableHash(key.ToByteArray()) % (uint)ShardCount);

    public int GetShardId(string key) => (int)(StableHash(Encoding.UTF8.GetBytes(key)) % (uint)ShardCount);

    public int GetShardId(long key) => (int)((ulong)key % (ulong)ShardCount);

    /// <summary>FNV-1a 稳定哈希 — 跨平台、跨 .NET 版本一致</summary>
    private static uint StableHash(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261;
        foreach (var b in data) hash = (hash ^ b) * 16777619;
        return hash;
    }
}

// ── 一致性哈希分片（扩缩容时只迁移少量数据）──

/// <summary>一致性哈希分片策略（减少扩缩容迁移量）</summary>
/// <remarks>
/// 🔴 P0 修复 (2026-06-21)：原实现 BuildRing 按 shard 连续填充，等同于取模分片，
/// 扩容时仍需全量迁移。修复为：每个虚拟节点独立 FNV-1a 哈希到环上不同位置，
/// 查找时取哈希值后第一个虚拟节点。扩容时约 1/N 数据需要迁移。
/// </remarks>
public sealed class ConsistentHashSharding : IShardingStrategy
{
    private readonly (uint Hash, int Shard)[] _ring;

    public int ShardCount { get; }

    /// <param name="shardCount">物理分片数</param>
    /// <param name="virtualNodes">每分片虚拟节点数（默认 256，越大越均匀）</param>
    public ConsistentHashSharding(int shardCount, int virtualNodes = 256)
    {
        if (shardCount <= 0) throw new ArgumentOutOfRangeException(nameof(shardCount));
        ShardCount = shardCount;

        var entries = new (uint Hash, int Shard)[shardCount * virtualNodes];
        int idx = 0;
        for (int s = 0; s < shardCount; s++)
        {
            for (int v = 0; v < virtualNodes; v++)
            {
                // 每个虚拟节点独立 FNV-1a 哈希。"vnode-{s}-{v}" 确保哈希分布均匀
                var label = Encoding.UTF8.GetBytes($"vnode-{s}-{v}");
                uint hash = 2166136261;
                foreach (var b in label) hash = (hash ^ b) * 16777619;
                entries[idx++] = (hash, s);
            }
        }

        // 按哈希排序构建环
        Array.Sort(entries, (a, b) => a.Hash.CompareTo(b.Hash));
        _ring = entries;
    }

    public int GetShardId(Guid key) => HashToShard(Fnv1A(key.ToByteArray()));

    public int GetShardId(string key) => HashToShard(Fnv1A(Encoding.UTF8.GetBytes(key)));

    public int GetShardId(long key) => HashToShard(Fnv1A(BitConverter.GetBytes(key)));

    /// <summary>FNV-1a 哈希 — 稳定、跨平台一致</summary>
    private static uint Fnv1A(ReadOnlySpan<byte> data)
    {
        uint hash = 2166136261;
        foreach (var b in data) hash = (hash ^ b) * 16777619;
        return hash;
    }

    /// <summary>二分查找环上第一个大于等于 key 哈希的虚拟节点</summary>
    private int HashToShard(uint hash)
    {
        var span = _ring.AsSpan();
        var lo = 0;
        var hi = span.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (span[mid].Hash < hash) lo = mid + 1;
            else hi = mid - 1;
        }
        // lo == span.Length 表示所有节点哈希都小于 key，回到环起点
        return span[lo % span.Length].Shard;
    }
}

// ── 分片数据源管理 ──

/// <summary>分片数据源管理器 — 管理多个 PostgreSQL 分片连接</summary>
public sealed class ShardedDataSourceManager : IAsyncDisposable
{
    private readonly NpgsqlDataSource[] _shards;

    public int ShardCount => _shards.Length;

    /// <param name="connectionStrings">每个分片的连接字符串（索引对应分片 ID）</param>
    public ShardedDataSourceManager(string[] connectionStrings, string applicationName = "Pal.DDD-Shard")
    {
        _shards = new NpgsqlDataSource[connectionStrings.Length];
        for (int i = 0; i < connectionStrings.Length; i++)
        {
            var builder = new NpgsqlDataSourceBuilder(connectionStrings[i]);
            builder.ConnectionStringBuilder.ApplicationName = $"{applicationName}-{i}";
            _shards[i] = builder.Build();
        }
    }

    /// <summary>根据分片 ID 获取对应数据源</summary>
    public NpgsqlDataSource GetShard(int shardId) => _shards[shardId];

    /// <summary>根据分片键和策略获取对应数据源</summary>
    public NpgsqlDataSource GetShard(IShardingStrategy strategy, Guid key)
        => _shards[strategy.GetShardId(key)];

    /// <summary>获取所有分片数据源</summary>
    public IReadOnlyList<NpgsqlDataSource> AllShards => _shards;

    public async ValueTask DisposeAsync()
    {
        foreach (var ds in _shards) await ds.DisposeAsync().ConfigureAwait(false);
    }
}

// ── 表级分片工具 ──

/// <summary>表级分片工具 — 生成分片表名</summary>
public static class ShardedTableName
{
    /// <summary>获取分片表名：outbox_messages → outbox_messages_3</summary>
    public static string For(string baseTable, int shardId)
        => $"{baseTable}_{shardId}";

    /// <summary>获取所有分片表名</summary>
    public static string[] All(string baseTable, int shardCount)
        => Enumerable.Range(0, shardCount).Select(i => For(baseTable, i)).ToArray();

    /// <summary>创建分片表（复制基础表结构）</summary>
    public static string CreateShardedTableDdl(string baseTable, int shardCount)
        => string.Join(";\n", Enumerable.Range(0, shardCount)
            .Select(i => $"CREATE TABLE IF NOT EXISTS {For(baseTable, i)} (LIKE {baseTable} INCLUDING ALL)"));
}
