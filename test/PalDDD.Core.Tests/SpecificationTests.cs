namespace PalDDD.Core.Tests;

public class SpecificationTests
{
    private sealed record TestEntity(int Id, string Name, decimal Amount, bool IsActive);

    // ═══════════════════════════════════════════════════════════════
    // IsSatisfiedBy — 内存判断
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task IsSatisfiedBy_MatchingEntity_ReturnsTrue()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100);
        var entity = new TestEntity(1, "A", 200, true);

        await Assert.That(spec.IsSatisfiedBy(entity)).IsTrue();
    }

    [Test]
    public async Task IsSatisfiedBy_NonMatchingEntity_ReturnsFalse()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100);
        var entity = new TestEntity(1, "A", 50, true);

        await Assert.That(spec.IsSatisfiedBy(entity)).IsFalse();
    }

    [Test]
    public async Task IsSatisfiedBy_All_AlwaysReturnsTrue()
    {
        var entity = new TestEntity(1, "A", 0, false);
        await Assert.That(Spec<TestEntity>.All.IsSatisfiedBy(entity)).IsTrue();
    }

    [Test]
    public async Task IsSatisfiedBy_None_AlwaysReturnsFalse()
    {
        var entity = new TestEntity(1, "A", 0, false);
        await Assert.That(Spec<TestEntity>.None.IsSatisfiedBy(entity)).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // And 组合
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task And_BothSatisfied_ReturnsTrue()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .And(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 200, true);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsTrue();
    }

    [Test]
    public async Task And_OneNotSatisfied_ReturnsFalse()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .And(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 200, false);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsFalse();
    }

    [Test]
    public async Task And_NeitherSatisfied_ReturnsFalse()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .And(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 50, false);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // Or 组合
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Or_BothSatisfied_ReturnsTrue()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .Or(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 200, true);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsTrue();
    }

    [Test]
    public async Task Or_OneSatisfied_ReturnsTrue()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .Or(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 200, false);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsTrue();
    }

    [Test]
    public async Task Or_NeitherSatisfied_ReturnsFalse()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .Or(Spec<TestEntity>.Where(e => e.IsActive));

        var entity = new TestEntity(1, "A", 50, false);
        await Assert.That(spec.IsSatisfiedBy(entity)).IsFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    // Not 组合
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Not_InvertsResult()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100).Not();

        var matching = new TestEntity(1, "A", 200, true);
        var nonMatching = new TestEntity(2, "B", 50, false);

        await Assert.That(spec.IsSatisfiedBy(matching)).IsFalse();
        await Assert.That(spec.IsSatisfiedBy(nonMatching)).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    // 链式组合
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task ChainedAndOrNot_ComplexCondition()
    {
        // (Amount > 100 AND IsActive) OR (Amount <= 100 AND NOT IsActive)
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .And(Spec<TestEntity>.Where(e => e.IsActive))
            .Or(
                Spec<TestEntity>.Where(e => e.Amount <= 100)
                    .And(Spec<TestEntity>.Where(e => e.IsActive).Not())
            );

        await Assert.That(spec.IsSatisfiedBy(new TestEntity(1, "A", 200, true))).IsTrue();   // 高金额+活跃
        await Assert.That(spec.IsSatisfiedBy(new TestEntity(2, "B", 200, false))).IsFalse(); // 高金额+非活跃
        await Assert.That(spec.IsSatisfiedBy(new TestEntity(3, "C", 50, false))).IsTrue();   // 低金额+非活跃
        await Assert.That(spec.IsSatisfiedBy(new TestEntity(4, "D", 50, true))).IsFalse();   // 低金额+活跃
    }

    // ═══════════════════════════════════════════════════════════════
    // ToExpression — EF Core 集成
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task ToExpression_ReturnsCompilableExpression()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100);
        var expr = spec.ToExpression();
        var compiled = expr.Compile();

        await Assert.That(compiled(new TestEntity(1, "A", 200, true))).IsTrue();
        await Assert.That(compiled(new TestEntity(2, "B", 50, false))).IsFalse();
    }

    [Test]
    public async Task ToExpression_AndCombination_IsCompilable()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100)
            .And(Spec<TestEntity>.Where(e => e.IsActive));
        var compiled = spec.ToExpression().Compile();

        await Assert.That(compiled(new TestEntity(1, "A", 200, true))).IsTrue();
        await Assert.That(compiled(new TestEntity(2, "B", 200, false))).IsFalse();
        await Assert.That(compiled(new TestEntity(3, "C", 50, true))).IsFalse();
    }

    [Test]
    public async Task ToExpression_NotCombination_IsCompilable()
    {
        var spec = Spec<TestEntity>.Where(e => e.IsActive).Not();
        var compiled = spec.ToExpression().Compile();

        await Assert.That(compiled(new TestEntity(1, "A", 100, true))).IsFalse();
        await Assert.That(compiled(new TestEntity(2, "B", 100, false))).IsTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    // 边界
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Where_NullPredicate_ThrowsArgumentNullException()
    {
        await Assert.That(() => Spec<TestEntity>.Where(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task And_NullOther_ThrowsArgumentNullException()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100);
        await Assert.That(() => spec.And(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Or_NullOther_ThrowsArgumentNullException()
    {
        var spec = Spec<TestEntity>.Where(e => e.Amount > 100);
        await Assert.That(() => spec.Or(null!)).Throws<ArgumentNullException>();
    }
}
