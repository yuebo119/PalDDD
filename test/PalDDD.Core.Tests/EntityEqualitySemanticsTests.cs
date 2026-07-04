namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🏛️ DDD 战术模式最佳实践示范 — 实体相等性语义
// ═══════════════════════════════════════════════════════════════
// 补全 Entity<TId>.Equals 的设计契约（源码注释明确但未测）：
// 1. 瞬时实体即使 Id 相同也不相等
// 2. 不同具体类型同 Id 不相等（GetType() 必须匹配）
// 3. 哈希一致性（a.Equals(b) → a.GetHashCode()==b.GetHashCode()）
// ═══════════════════════════════════════════════════════════════

// ─── 测试用实体 ───

public sealed class Customer : AggregateRoot<Guid>
{
    public string Name { get; set; }

    public Customer(Guid id, string name) : base(id) => Name = name;
}

public sealed class Product : AggregateRoot<Guid>
{
    public string Sku { get; set; }

    public Product(Guid id, string sku) : base(id) => Sku = sku;
}

// ─── 测试 ───

public sealed class EntityEqualitySemanticsTests
{
    [Test]
    public async Task TransientEntities_WithSameDefaultId_AreNotEqual()
    {
        // Guid.Empty 是 default(Guid) — 两个瞬时实体即使 Id 相同也不相等
        var a = new Customer(Guid.Empty, "A");
        var b = new Customer(Guid.Empty, "B");

        await Assert.That(a.IsTransient()).IsTrue();
        await Assert.That(b.IsTransient()).IsTrue();
        await Assert.That(a.Equals(b)).IsFalse();
        await Assert.That(a == b).IsFalse();
    }

    [Test]
    public async Task TransientAndPersistent_WithSameId_AreNotEqual()
    {
        var id = Guid.NewGuid();
        var transient = new Customer(Guid.Empty, "T");
        var persistent = new Customer(id, "P");

        await Assert.That(transient.IsTransient()).IsTrue();
        await Assert.That(persistent.IsTransient()).IsFalse();
        await Assert.That(transient.Equals(persistent)).IsFalse();
    }

    [Test]
    public async Task DifferentConcreteTypes_SameId_AreNotEqual()
    {
        var id = Guid.NewGuid();
        var customer = new Customer(id, "Alice");
        var product = new Product(id, "SKU-001");

        // 契约：GetType() != other.GetType() → 不相等
        await Assert.That(customer.Equals(product)).IsFalse();
    }

    [Test]
    public async Task SameType_SameId_DifferentProperties_AreEqual()
    {
        var id = Guid.NewGuid();
        var a = new Customer(id, "Alice");
        var b = new Customer(id, "Bob");

        // 实体相等性基于 Id，不基于属性
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task SameType_DifferentId_AreNotEqual()
    {
        var a = new Customer(Guid.NewGuid(), "Alice");
        var b = new Customer(Guid.NewGuid(), "Alice");

        await Assert.That(a.Equals(b)).IsFalse();
        await Assert.That(a != b).IsTrue();
    }

    [Test]
    public async Task TransientEntity_HashCode_StableAcrossCalls()
    {
        var transient = new Customer(Guid.Empty, "T");

        var hash1 = transient.GetHashCode();
        var hash2 = transient.GetHashCode();

        await Assert.That(hash1).IsEqualTo(hash2);
    }

    [Test]
    public async Task PersistentEntity_HashCode_BasedOnId()
    {
        var id = Guid.NewGuid();
        var a = new Customer(id, "Alice");
        var b = new Customer(id, "Bob");

        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task HashCode_ConsistentWithEquals()
    {
        var id = Guid.NewGuid();
        var a = new Customer(id, "Alice");
        var b = new Customer(id, "Bob");

        // 契约：a.Equals(b) → a.GetHashCode() == b.GetHashCode()
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }

    [Test]
    public async Task OperatorEquality_NullHandling()
    {
        Customer? nullA = null;
        Customer? nullB = null;
        var entity = new Customer(Guid.NewGuid(), "Alice");

        // null == null → true
        await Assert.That(nullA == nullB).IsTrue();

        // entity == null → false
        await Assert.That(entity == nullA).IsFalse();

        // null == entity → false
        await Assert.That(nullA == entity).IsFalse();

        // entity != null → true
        await Assert.That(entity != nullA).IsTrue();
    }

    [Test]
    public async Task Equals_NullObject_ReturnsFalse()
    {
        var entity = new Customer(Guid.NewGuid(), "Alice");

        await Assert.That(entity.Equals((object?)null)).IsFalse();
    }

    [Test]
    public async Task Equals_SameReference_ReturnsTrue()
    {
        var entity = new Customer(Guid.NewGuid(), "Alice");

        await Assert.That(entity.Equals((object)entity)).IsTrue();
    }

    [Test]
    public async Task ToString_ContainsTypeNameAndId()
    {
        var id = Guid.NewGuid();
        var entity = new Customer(id, "Alice");

        var str = entity.ToString();
        await Assert.That(str).Contains("Customer");
        await Assert.That(str).Contains(id.ToString());
    }
}
