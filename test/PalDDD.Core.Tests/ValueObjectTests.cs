namespace PalDDD.Core.Tests;

public sealed class ValueObjectTests
{
    [Test]
    public async Task ValueObject_StoresValue()
    {
        var vo = new ValueObject<int>(42);
        await Assert.That(vo.Value).IsEqualTo(42);
    }

    [Test]
    public async Task ValueObject_PreservesValueExactly()
    {
        // ValueObject<T> 不做隐式值域约束 —— 值域约束由派生类型自行实现。
        // 此处验证值原样存储，不被恒等 Clamp 改变。
        var vo = new ValueObject<int>(42);
        await Assert.That(vo.Value).IsEqualTo(42);
    }

    [Test]
    public async Task ValueObject_ImplicitConversion()
    {
        var vo = new ValueObject<int>(99);
        int value = vo;
        await Assert.That(value).IsEqualTo(99);
    }

    [Test]
    public async Task ValueObject_DefaultIsZero()
    {
        var vo = new ValueObject<int>(0);
        await Assert.That(vo.Value).IsEqualTo(0);
    }

    [Test]
    public async Task ValueObject_MaxValue()
    {
        var vo = new ValueObject<int>(int.MaxValue);
        await Assert.That(vo.Value).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task ValueObject_MinValue()
    {
        var vo = new ValueObject<int>(int.MinValue);
        await Assert.That(vo.Value).IsEqualTo(int.MinValue);
    }

    // ═══════════════════════════════════════════════════════════════
    // TryFormat — IUtf8SpanFormattable 实现测试
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task TryFormat_Int_ProducesUtf8Output()
    {
        var vo = new ValueObject<int>(42);
        Span<byte> destination = stackalloc byte[16];

        var success = vo.TryFormat(destination, out var bytesWritten, default, null);

        await Assert.That(success).IsTrue();
        await Assert.That(bytesWritten).IsEqualTo(2);
        await Assert.That(destination[0]).IsEqualTo((byte)'4');
        await Assert.That(destination[1]).IsEqualTo((byte)'2');
    }

    [Test]
    public async Task TryFormat_Int_ProducesCorrectUtf8ForNegative()
    {
        var vo = new ValueObject<int>(-7);
        Span<byte> destination = stackalloc byte[16];

        var success = vo.TryFormat(destination, out var bytesWritten, default, null);

        await Assert.That(success).IsTrue();
        await Assert.That(bytesWritten).IsEqualTo(2);
        await Assert.That(destination[0]).IsEqualTo((byte)'-');
        await Assert.That(destination[1]).IsEqualTo((byte)'7');
    }

    [Test]
    public async Task TryFormat_Int_DestinationTooSmall_ReturnsFalse()
    {
        var vo = new ValueObject<int>(12345);
        Span<byte> destination = stackalloc byte[2]; // 不够 5 字节

        var success = vo.TryFormat(destination, out var bytesWritten, default, null);

        await Assert.That(success).IsFalse();
        await Assert.That(bytesWritten).IsEqualTo(0);
    }

    [Test]
    public async Task TryFormat_Long_ProducesUtf8Output()
    {
        var vo = new ValueObject<long>(999);
        Span<byte> destination = stackalloc byte[16];

        var success = vo.TryFormat(destination, out var bytesWritten, default, null);

        await Assert.That(success).IsTrue();
        // "999" = 3 个字节
        await Assert.That(bytesWritten).IsEqualTo(3);
        await Assert.That(destination[0]).IsEqualTo((byte)'9');
        await Assert.That(destination[1]).IsEqualTo((byte)'9');
        await Assert.That(destination[2]).IsEqualTo((byte)'9');
    }

    [Test]
    public async Task ToString_WithFormat_UsesUnderlyingTypeFormat()
    {
        var vo = new ValueObject<int>(255);

        var result = vo.ToString("X4", null);

        // 255 的 4 位十六进制 = "00FF"
        await Assert.That(result).IsEqualTo("00FF");
    }
}

public sealed class RowVersionTests
{
    [Test]
    public async Task Next_IncrementsValue()
    {
        var v = new RowVersion(5);
        await Assert.That(v.Next().Value).IsEqualTo(6);
    }

    [Test]
    public async Task ImplicitConversion_ToInt()
    {
        var v = new RowVersion(10);
        int i = v;
        await Assert.That(i).IsEqualTo(10);
    }

    [Test]
    public async Task Equality_ByValue()
    {
        await Assert.That(new RowVersion(1)).IsEqualTo(new RowVersion(1));
        await Assert.That(new RowVersion(1)).IsNotEqualTo(new RowVersion(2));
    }
}

public sealed class DeletedTests
{
    [Test]
    public async Task Deleted_HasValue()
    {
        var d = new Deleted(true);
        await Assert.That(d.Value).IsTrue();
    }

    [Test]
    public async Task NotDeleted_HasValue()
    {
        var d = new Deleted(false);
        await Assert.That(d.Value).IsFalse();
    }

    [Test]
    public async Task Default_IsNotDeleted()
    {
        var d = default(Deleted);
        await Assert.That(d.Value).IsFalse();
    }

    [Test]
    public async Task Yes_IsDeleted()
    {
        await Assert.That(Deleted.Yes.Value).IsTrue();
    }

    [Test]
    public async Task No_IsNotDeleted()
    {
        await Assert.That(Deleted.No.Value).IsFalse();
    }

    [Test]
    public async Task ImplicitConversion_ToBool()
    {
        var d = new Deleted(true);
        bool b = d;
        await Assert.That(b).IsTrue();
    }

    [Test]
    public async Task ImplicitConversion_FromBool()
    {
        Deleted d = true;
        await Assert.That(d.Value).IsTrue();
    }

    [Test]
    public async Task ToString_Deleted()
    {
        await Assert.That(Deleted.Yes.ToString()).IsEqualTo("deleted");
    }

    [Test]
    public async Task ToString_Active()
    {
        await Assert.That(Deleted.No.ToString()).IsEqualTo("active");
    }
}

public sealed class UpdateTimeTests
{
    [Test]
    public async Task UpdateTime_StoresDateTimeOffset()
    {
        var now = DateTimeOffset.UtcNow;
        var ut = new UpdateTime(now);
        await Assert.That(ut.Value).IsEqualTo(now);
    }

    [Test]
    public async Task Now_ReturnsUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var ut = UpdateTime.Now();
        var after = DateTimeOffset.UtcNow;
        await Assert.That(ut.Value >= before).IsTrue();
        await Assert.That(ut.Value <= after).IsTrue();
    }

    [Test]
    public async Task ImplicitConversion_ToDateTimeOffset()
    {
        var now = DateTimeOffset.UtcNow;
        var ut = new UpdateTime(now);
        DateTimeOffset dt = ut;
        await Assert.That(dt).IsEqualTo(now);
    }

    [Test]
    public async Task ToString_ISO8601()
    {
        var now = DateTimeOffset.UtcNow;
        var ut = new UpdateTime(now);
        await Assert.That(ut.ToString()).IsEqualTo(now.ToString("O"));
    }
}
