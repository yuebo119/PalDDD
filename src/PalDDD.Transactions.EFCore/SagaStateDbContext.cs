// ─────────────────────────────────────────────────────────────
// 💾 SagaStateDbContext — EF Core Saga 状态存储（PrimitiveCollection + 并发令牌）
// ─────────────────────────────────────────────────────────────
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Transactions;

// ─────────────────────────────────────────────────────────────
// EF Core Saga 状态持久化
// ─────────────────────────────────────────────────────────────

/// <summary>EF Core Saga 状态存储基础上下文。</summary>
/// <typeparam name="TState">Saga 状态类型</typeparam>
[UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with RequiresUnreferencedCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCode require dynamic access",
    Justification = "EF Core DbContext base types are isolated in the optional EFCore adapter package.")]
[UnconditionalSuppressMessage("Trimming", "IL2091:Target generic argument does not satisfy member access requirements",
    Justification = "EF Core model construction requires broad member access for saga state entities in the optional EFCore adapter package.")]
public abstract class SagaStateDbContext<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.NonPublicConstructors
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.NonPublicFields
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.NonPublicProperties
        | DynamicallyAccessedMemberTypes.Interfaces)]
TState>(DbContextOptions options) : DbContext(options), ISagaStateStore<TState>
    where TState : SagaState
{
    /// <summary>Saga 状态表</summary>
    public DbSet<TState> SagaStates => Set<TState>();

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TState>> GetActiveSagasAsync(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        return await SagaStates
            .Where(s => s.Status == SagaStatus.Active)
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TState>> LeaseActiveSagasAsync(
        string owner,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var now = TimeProvider.System.GetUtcNow();
        var leasedUntil = now.Add(leaseDuration);
        var states = await SagaStates
            .Where(s => s.Status == SagaStatus.Active
                && (s.LeasedUntil == null || s.LeasedUntil <= now))
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var state in states)
        {
            state.LeasedBy = owner;
            state.LeasedUntil = leasedUntil;
        }

        await SaveChangesAsync(ct);
        return states;
    }

    /// <inheritdoc/>
    public async ValueTask<TState?> GetByIdAsync(PalUlid sagaId, CancellationToken ct)
        => await SagaStates.SingleOrDefaultAsync(s => s.SagaId == sagaId, ct);

    /// <inheritdoc/>
    /// <remarks>EF Core 版：DbContext 变更跟踪自动检测修改，state 参数可选。</remarks>
    async ValueTask<int> ISagaStateStore<TState>.SaveChangesAsync(TState state, CancellationToken ct)
        => await SaveChangesAsync(ct);

    /// <summary>配置 Saga 状态实体规则</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TState>(e =>
        {
            e.HasKey(x => x.SagaId);
            e.HasIndex(x => new { x.Status, x.CurrentState });
            e.HasIndex(x => new { x.Status, x.LeasedUntil, x.CreatedAt });
            e.Property(x => x.CurrentState).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Error).HasMaxLength(2048);
            e.Property(x => x.LeasedBy).HasMaxLength(256);

            // Dictionary<K,V> → JSON column（EF Core 不支持 PrimitiveCollection for Dictionary）
            e.Property(x => x.StepStartedAt)
                .HasConversion(
                    static value => JsonSerializer.Serialize(value, SagaStateJsonContext.Default.DictionaryStringDateTimeOffset),
                    static value => JsonSerializer.Deserialize(value, SagaStateJsonContext.Default.DictionaryStringDateTimeOffset)
                        ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal))
                .Metadata.SetValueComparer(StepStartedAtComparer);

            // Collection<string> → PrimitiveCollection（EF Core 11 原生 JSON 列支持）
            // 替代了手写 HasConversion + ValueComparer，由 EF Core 自动管理变更追踪
            e.PrimitiveCollection(x => x.ExecutedStepKeys).ElementType().HasMaxLength(256);
        });
    }

    // ─────────────────────────────────────────────────────────────
    // StepStartedAt 的 Dictionary ValueComparer（EF Core 无原生支持）
    // ─────────────────────────────────────────────────────────────

    private static readonly ValueComparer<Dictionary<string, DateTimeOffset>> StepStartedAtComparer = new(
        static (left, right) => DictionaryEquals(left, right),
        static value => DictionaryHashCode(value),
        static value => CloneDictionary(value));

    private static bool DictionaryEquals(
        Dictionary<string, DateTimeOffset>? left,
        Dictionary<string, DateTimeOffset>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var other) || other != value) return false;
        return true;
    }

    private static int DictionaryHashCode(Dictionary<string, DateTimeOffset>? value)
    {
        var hash = new HashCode();
        if (value is not null)
            foreach (var item in value.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                hash.Add(item.Key, StringComparer.Ordinal);
                hash.Add(item.Value);
            }
        return hash.ToHashCode();
    }

    private static Dictionary<string, DateTimeOffset> CloneDictionary(Dictionary<string, DateTimeOffset>? value)
        => value is null
            ? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            : new Dictionary<string, DateTimeOffset>(value, StringComparer.Ordinal);
}

[JsonSerializable(typeof(Dictionary<string, DateTimeOffset>))]
internal sealed partial class SagaStateJsonContext : JsonSerializerContext;
