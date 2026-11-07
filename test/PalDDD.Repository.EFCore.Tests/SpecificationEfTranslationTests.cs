using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PalDDD.Core;
using System.Linq.Expressions;

namespace PalDDD.Repository.EFCore.Tests;

// ─────────────────────────────────────────────────────────────
// 2026-11-07 审计 P2-B：Specification 组合规约的 EF Core 翻译回归网
// ─────────────────────────────────────────────────────────────
//
// 机制：SpecificationTests 的「ToExpression — EF Core 集成」小节三个用例都只
// expr.Compile() 内存求值，而内存 Compile 对 Expression.Invoke 也能工作，
// 故 Expression.Invoke 回归（EF 无法翻译）无法被现有测试捕获。
// docs/conventions.md 明确禁止 Expression.Invoke——组合规约必须走 ParameterReplacer。
// 本文件在真实 EF provider 上执行 ToExpression() 翻译，并直接检测 Invoke 节点。

/// <summary>Specification 组合规约的 EF Core 翻译锁定（审计 P2-B）。</summary>
public sealed class SpecificationEfTranslationTests
{
    private sealed class SpecEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// And/Or/Not 组合规约必须能被 EF Core 翻译为 SQL（SQLite 关系型 provider）。
    /// <para>
    /// 若组合实现改用 <c>Expression.Invoke</c>，EF 会在翻译期抛
    /// <see cref="NotSupportedException"/>——本用例立即转红。
    /// </para>
    /// </summary>
    [Test]
    public async Task And_Or_Not_Translatable(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<SpecDbContext>().UseSqlite(connection).Options;
        await using var context = new SpecDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Entities.AddRangeAsync(
        [
            new SpecEntity { Id = 1, Name = "A", Amount = 200, IsActive = true },
            new SpecEntity { Id = 2, Name = "B", Amount = 200, IsActive = false },
            new SpecEntity { Id = 3, Name = "C", Amount = 50, IsActive = false },
        ], cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        var spec = Spec<SpecEntity>.Where(e => e.Amount > 100)
            .And(Spec<SpecEntity>.Where(e => e.IsActive))
            .Or(Spec<SpecEntity>.Where(e => e.Name == "C"));

        var matches = await context.Entities
            .Where(spec.ToExpression())
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);

        // (Amount > 100 && IsActive) || Name == "C" → A(1) 与 C(3)
        await Assert.That(matches.Select(e => e.Id)).IsEquivalentTo([1, 3]);
    }

    /// <summary>
    /// 组合规约的表达式树不得含 <see cref="ExpressionType.Invoke"/> 节点——
    /// EF Core 等 LINQ 提供器无法把它翻译为 SQL（内存 Compile 可以，故单测必须直接检测）。
    /// </summary>
    [Test]
    public async Task No_ExpressionInvoke(CancellationToken cancellationToken)
    {
        var spec = Spec<SpecEntity>.Where(e => e.Amount > 100)
            .And(Spec<SpecEntity>.Where(e => e.IsActive))
            .Or(Spec<SpecEntity>.Where(e => e.Name == "C").Not());

        var detector = new InvokeDetector();
        detector.Visit(spec.ToExpression());

        await Assert.That(detector.Found).IsFalse()
            .Because("Expression.Invoke 无法被 EF Core 翻译——组合规约必须走 ParameterReplacer");
    }

    private sealed class SpecDbContext(DbContextOptions<SpecDbContext> options) : DbContext(options)
    {
        public DbSet<SpecEntity> Entities => Set<SpecEntity>();
    }

    /// <summary>表达式树中的 <c>Expression.Invoke</c> 节点检测器。</summary>
    private sealed class InvokeDetector : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Found = true;
            return base.VisitInvocation(node);
        }
    }
}
