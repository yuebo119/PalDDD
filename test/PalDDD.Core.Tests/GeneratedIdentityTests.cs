using System.Buffers;
using System.ComponentModel;
using System.Text.Json;

namespace PalDDD.Core.Tests;

[GenerateId(typeof(Guid))]
public readonly partial record struct CustomerId;

[GenerateId(typeof(int))]
public readonly partial record struct OrderNumber;

[GenerateId(typeof(string))]
public readonly partial record struct TenantKey;

public sealed class GeneratedIdentityTests
{
    [Test]
    public async Task GeneratedJsonConverter_RoundTripsGuidIdentity()
    {
        var id = CustomerId.From(Guid.Parse("3d58a70e-cb5c-4abc-9693-a765f8fb4a88"));
        var converter = new CustomerIdJsonConverter();
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);

        converter.Write(writer, id, JsonSerializerOptions.Default);
        writer.Flush();

        var reader = new Utf8JsonReader(output.WrittenSpan);
        await Assert.That(reader.Read()).IsTrue();
        var result = converter.Read(ref reader, typeof(CustomerId), JsonSerializerOptions.Default);

        await Assert.That(result).IsEqualTo(id);
    }

    [Test]
    public async Task GeneratedTypeConverter_ConvertsFromString()
    {
        var converter = TypeDescriptor.GetConverter(typeof(CustomerId));

        var result = converter.ConvertFromInvariantString("3d58a70e-cb5c-4abc-9693-a765f8fb4a88");
        await Assert.That(result).IsTypeOf<CustomerId>();
        var customerId = (CustomerId)result!;

        await Assert.That(customerId.Value).IsEqualTo(Guid.Parse("3d58a70e-cb5c-4abc-9693-a765f8fb4a88"));
    }

    [Test]
    public async Task GuidIdentity_New_GeneratesUniqueValue()
    {
        var a = CustomerId.New();
        var b = CustomerId.New();
        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a.Value).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task NumericIdentity_New_ThrowsBecauseServerMustAssign()
    {
        // 数值类型 Id 由数据库/服务端分配，客户端 New() 无意义 —— 应明确报错而非静默返回 default。
        await Assert.That(() => OrderNumber.New()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task GuidIdentity_ParseViaSpanParsable_RoundTrips()
    {
        var id = CustomerId.From(Guid.Parse("3d58a70e-cb5c-4abc-9693-a765f8fb4a88"));
        var parsed = CustomerId.Parse(id.ToString(), null);
        await Assert.That(parsed).IsEqualTo(id);
    }

    [Test]
    public async Task GuidIdentity_TryParse_InvalidInput_ReturnsFalse()
    {
        await Assert.That(CustomerId.TryParse("not-a-guid", out var _)).IsFalse();
        await Assert.That(CustomerId.TryParse("not-a-guid".AsSpan(), null, out var result)).IsFalse();
        await Assert.That(result).IsEqualTo(default);
    }

    [Test]
    public async Task NumericIdentity_Parse_ViaSpanParsable()
    {
        var id = OrderNumber.From(42);
        var parsed = OrderNumber.Parse("42", null);
        await Assert.That(parsed).IsEqualTo(id);
    }

    [Test]
    public async Task StringIdentity_RejectsNullOrEmpty()
    {
        await Assert.That(() => TenantKey.From(null!)).Throws<ArgumentException>();
        await Assert.That(() => TenantKey.From(string.Empty)).Throws<ArgumentException>();
        await Assert.That(TenantKey.TryParse(null, out var nullResult)).IsFalse();
        await Assert.That(nullResult).IsEqualTo(default);
        await Assert.That(TenantKey.TryParse(string.Empty, out var emptyResult)).IsFalse();
        await Assert.That(emptyResult).IsEqualTo(default);
        await Assert.That(TenantKey.TryParse(ReadOnlySpan<char>.Empty, null, out var spanResult)).IsFalse();
        await Assert.That(spanResult).IsEqualTo(default);
    }

    [Test]
    public async Task StringIdentity_ParseViaSpanParsable_AcceptsNonEmptyValue()
    {
        var parsed = TenantKey.Parse("tenant-a".AsSpan(), null);

        await Assert.That(parsed.Value).IsEqualTo("tenant-a");
    }

    [Test]
    public async Task StringIdentity_JsonNull_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("null"u8);
            reader.Read();
            var converter = new TenantKeyJsonConverter();
            converter.Read(ref reader, typeof(TenantKey), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task GeneratedType_ImplementsISpanParsable()
    {
        var type = typeof(CustomerId);
        await Assert.That(type.GetInterfaces()).Contains(typeof(ISpanParsable<>).MakeGenericType(type));
    }
}
