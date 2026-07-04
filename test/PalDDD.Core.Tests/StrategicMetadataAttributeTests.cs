using System.Reflection;

namespace PalDDD.Core.Tests;

public sealed class StrategicMetadataAttributeTests
{
    [Test]
    public async Task BoundedContextAttribute_PreservesName()
    {
        var attribute = new BoundedContextAttribute("ordering");

        await Assert.That(attribute.Name).IsEqualTo("ordering");
    }

    [Test]
    public async Task BoundedContextAttribute_RejectsBlankName()
    {
        await Assert.That(() => new BoundedContextAttribute(" ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task DomainCapabilityAttribute_PreservesName()
    {
        var attribute = new DomainCapabilityAttribute("order-fulfillment");

        await Assert.That(attribute.Name).IsEqualTo("order-fulfillment");
    }

    [Test]
    public async Task ProcessManagerAttribute_PreservesName()
    {
        var attribute = new ProcessManagerAttribute("order-fulfillment");

        await Assert.That(attribute.Name).IsEqualTo("order-fulfillment");
    }

    // ═══════════════════════════════════════════════════════════════
    // 对称性测试 — AllowMultiple / Inherited / 边界值
    // ═══════════════════════════════════════════════════════════════

    /// <summary>战略属性 AllowMultiple=false 防止重复标注（架构完整性）</summary>
    [Test]
    public async Task StrategicAttributes_HaveAllowMultipleFalse()
    {
        await Assert.That(GetAllowMultiple<BoundedContextAttribute>()).IsFalse();
        await Assert.That(GetAllowMultiple<DomainCapabilityAttribute>()).IsFalse();
        await Assert.That(GetAllowMultiple<ProcessManagerAttribute>()).IsFalse();
    }

    /// <summary>AggregateNameAttribute Inherited=false 防止子类继承父类聚合名</summary>
    [Test]
    public async Task AggregateNameAttribute_IsNotInherited()
    {
        var attr = typeof(AggregateNameAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        await Assert.That(attr.Inherited).IsFalse();
        await Assert.That(attr.AllowMultiple).IsFalse();
    }

    /// <summary>BoundedContextAttribute 空白名称拒绝覆盖 null/空字符串</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task BoundedContextAttribute_RejectsInvalidNames(string? invalidName)
    {
        var ex = await Assert.That(() => new BoundedContextAttribute(invalidName!)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("Name");
    }

    /// <summary>AggregateNameAttribute 空白名称拒绝覆盖 null/空字符串</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AggregateNameAttribute_RejectsInvalidNames(string? invalidName)
    {
        var ex = await Assert.That(() => new AggregateNameAttribute(invalidName!)).Throws<ArgumentException>();
        await Assert.That(ex!.Message).Contains("Name");
    }

    /// <summary>GenerateMessageAttribute 默认值对称性</summary>
    [Test]
    public async Task GenerateMessageAttribute_DefaultValues()
    {
        var attr = new GenerateMessageAttribute();

        await Assert.That(attr.Name).IsNull();
        await Assert.That(attr.SchemaVersion).IsEqualTo(1);
    }

    /// <summary>GenerateMessageAttribute 设置属性保持对称</summary>
    [Test]
    public async Task GenerateMessageAttribute_SetsProperties()
    {
        var attr = new GenerateMessageAttribute
        {
            Name = "test-event.v2",
            SchemaVersion = 2
        };

        await Assert.That(attr.Name).IsEqualTo("test-event.v2");
        await Assert.That(attr.SchemaVersion).IsEqualTo(2);
    }

    /// <summary>GenerateIdAttribute IdType 通过构造函数注入后保持不变</summary>
    [Test]
    public async Task GenerateIdAttribute_PreservesIdType()
    {
        var attr = new GenerateIdAttribute(typeof(Guid));

        await Assert.That(attr.IdType).IsEqualTo(typeof(Guid));
    }

    /// <summary>GenerateEnumAttribute 无参构造函数 — 纯标记属性</summary>
    [Test]
    public async Task GenerateEnumAttribute_IsMarkerAttribute()
    {
        var attr = new GenerateEnumAttribute();

        await Assert.That(attr).IsNotNull();
    }

    private static bool GetAllowMultiple<T>() where T : Attribute
    {
        var usage = typeof(T).GetCustomAttribute<AttributeUsageAttribute>()!;
        return usage.AllowMultiple;
    }
}
