using System.Buffers;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using PalUlid = ByteAether.Ulid.Ulid;

namespace PalDDD.Core.Tests;

[GenerateId(typeof(Guid))]
public readonly partial record struct CustomerId;

[GenerateId(typeof(int))]
public readonly partial record struct OrderNumber;

[GenerateId(typeof(long))]
public readonly partial record struct LongRowId;

[GenerateId(typeof(string))]
public readonly partial record struct TenantKey;

[GenerateId(typeof(PalUlid))]
public readonly partial record struct UlidKey;

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

    // ── v25 P3 生成器族：Guid/int/long JsonReadBody 坏 token 守卫 ──
    // 契约：S.T.J converter 的 Read 对不匹配 token 应抛 JsonException（与 Ulid/string 分支
    // 对齐）——修复前 GetGuid/GetInt32/GetInt64 对坏 token 抛 InvalidOperationException，
    // 上层 catch (JsonException) 无法捕获。

    [Test]
    public async Task GuidIdentity_JsonNumberToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("123"u8);
            reader.Read();
            var converter = new CustomerIdJsonConverter();
            converter.Read(ref reader, typeof(CustomerId), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task GuidIdentity_JsonInvalidString_ThrowsJsonException()
    {
        // v33 P2 探针：token 类型正确但格式非法（"not-a-guid"）——修复前 GetGuid 抛
        // FormatException（非 JsonException，S.T.J converter 契约破裂）；修复后
        // TryParse → JsonException
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("\"not-a-guid\""u8);
            reader.Read();
            var converter = new CustomerIdJsonConverter();
            converter.Read(ref reader, typeof(CustomerId), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task IntIdentity_JsonFractionalNumber_ThrowsJsonException()
    {
        // v33 P2 探针：Number token 但非整数（1.5）——修复前 GetInt32 抛 FormatException；
        // 修复后 TryGetInt32 → JsonException
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("1.5"u8);
            reader.Read();
            var converter = new OrderNumberJsonConverter();
            converter.Read(ref reader, typeof(OrderNumber), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task GuidIdentity_JsonNullToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("null"u8);
            reader.Read();
            var converter = new CustomerIdJsonConverter();
            converter.Read(ref reader, typeof(CustomerId), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task IntIdentity_JsonStringToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("\"42\""u8);
            reader.Read();
            var converter = new OrderNumberJsonConverter();
            converter.Read(ref reader, typeof(OrderNumber), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task IntIdentity_JsonNullToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("null"u8);
            reader.Read();
            var converter = new OrderNumberJsonConverter();
            converter.Read(ref reader, typeof(OrderNumber), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task LongIdentity_JsonStringToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("\"42\""u8);
            reader.Read();
            var converter = new LongRowIdJsonConverter();
            converter.Read(ref reader, typeof(LongRowId), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    // ── v30 P3 生成器族：string 分支 JsonReadBody 坏 token 守卫 ──
    // 契约：同 Guid/int/long 分支——非 String token（Number/True 等）原从 GetString()
    // 抛 InvalidOperationException，上层 catch (JsonException) 无法捕获；修复后
    // 守卫前置统一抛 JsonException（Null token 的 ?? throw 语义保留）。

    [Test]
    public async Task StringIdentity_JsonNumberToken_ThrowsJsonException()
    {
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("123"u8);
            reader.Read();
            var converter = new TenantKeyJsonConverter();
            converter.Read(ref reader, typeof(TenantKey), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task StringIdentity_JsonEmptyString_ThrowsJsonException()
    {
        // v34 P2 探针：String token 但值为 ""——修复前借道 From 抛 ArgumentException
        //（非 JsonException 契约破裂）；修复后空串转 JsonException
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("\"\""u8);
            reader.Read();
            var converter = new TenantKeyJsonConverter();
            converter.Read(ref reader, typeof(TenantKey), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task UlidIdentity_JsonEscapedInvalid_ThrowsJsonException()
    {
        // v34 P2 探针：转义字符串（\u006E = 'n'，拼接出非法 Ulid 内容）——修复前 escaped
        // 腿 Parse 抛 FormatException；修复后 TryParse → JsonException
        await Assert.That(() =>
        {
            var reader = new Utf8JsonReader("\"\\u006Eot-a-ulid\""u8);
            reader.Read();
            var converter = new UlidKeyJsonConverter();
            converter.Read(ref reader, typeof(UlidKey), JsonSerializerOptions.Default);
        }).Throws<JsonException>();
    }

    [Test]
    public async Task GeneratedType_ImplementsISpanParsable()
    {
        var type = typeof(CustomerId);
        await Assert.That(type.GetInterfaces()).Contains(typeof(ISpanParsable<>).MakeGenericType(type));
    }

    // ── v26 P3 生成器族：string Id 的 default 结构 ToString 防 NRE ──

    [Test]
    public async Task StringIdentity_DefaultToString_ReturnsEmptyInsteadOfThrowing()
    {
        // v26 P3 生成器族：default(TenantKey).Value == null——原生成物 ToString() 统一
        // 生成 Value.ToString()!，null.ToString() 抛 NullReferenceException；修复后仅
        // string 分支生成 Value ?? string.Empty（值类型分支保持原样——?? 对非可空值
        // 类型不编译）
        var text = default(TenantKey).ToString();
        await Assert.That(text).IsEqualTo(string.Empty);
    }

    // ── v27 P3 生成器族：string Id 的 default 结构 JsonWrite 防 ANE ──

    [Test]
    public async Task StringIdentity_DefaultJsonWrite_WritesNullTokenInsteadOfThrowing()
    {
        // v27 P3 生成器族：default(TenantKey).Value == null——原生成物 Write 统一生成
        // WriteStringValue(value.Value)，null 时抛 ArgumentNullException；修复后 string
        // 分支 default 写 null token（对齐 v26 ToStringBody 的 default 防御）
        var converter = new TenantKeyJsonConverter();
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);

        converter.Write(writer, default, JsonSerializerOptions.Default);
        writer.Flush();

        var reader = new Utf8JsonReader(output.WrittenSpan);
        await Assert.That(reader.Read()).IsTrue();
        await Assert.That(reader.TokenType).IsEqualTo(JsonTokenType.Null);
    }

    // ── ITM-629 回归：Ulid converter 的多段 ReadOnlySequence 读取路径 ──
    // 修复前生成物无条件取 reader.ValueSpan——ValueSpan 仅对单段有效，string token
    // 跨段（HasValueSequence=true）时抛 InvalidOperationException（非 JsonException，
    // 破坏 converter 契约并绕过上层 catch(JsonException)）；修复后该分支回退
    // GetString() 字符串重载。本测试从 token 正中切段强制走多段 reader 路径，
    // 证明反序列化成功且值正确（修复前此路径必抛 InvalidOperationException）。

    [Test]
    public async Task UlidIdentity_MultiSegmentSequence_DeserializesWithoutValueSpan()
    {
        var expected = UlidKey.From(PalUlid.New());
        // JSON 文本 = 完整 string token（含两侧引号），从正中切成两段拼 sequence
        var json = Encoding.UTF8.GetBytes($"\"{expected.Value}\"");
        var sequence = SplitIntoTwoSegments(json, json.Length / 2);

        // 多段 reader 路径：Utf8JsonReader(ReadOnlySequence) 重载，token 跨段
        // （JsonSerializer 无 sequence 公共重载——其内部即此 reader 形态）
        var reader = new Utf8JsonReader(sequence);
        await Assert.That(reader.Read()).IsTrue();
        // 前置守卫：token 跨段强制 HasValueSequence=true——否则测试静默退化为单段快路径
        await Assert.That(reader.HasValueSequence).IsTrue();

        var converter = new UlidKeyJsonConverter();
        var result = converter.Read(ref reader, typeof(UlidKey), JsonSerializerOptions.Default);

        await Assert.That(result).IsEqualTo(expected);
    }

    /// <summary>把缓冲区切成两段拼成跨段 <see cref="ReadOnlySequence{T}"/>——
    /// string token 跨越段边界，reader 呈现 HasValueSequence=true（多段读取路径）。</summary>
    private static ReadOnlySequence<byte> SplitIntoTwoSegments(byte[] payload, int splitAt)
    {
        var first = new SequenceSegment(payload.AsMemory(0, splitAt));
        var last = first.Append(payload.AsMemory(splitAt));
        return new ReadOnlySequence<byte>(first, 0, last, payload.Length - splitAt);
    }

    /// <summary>最小双段 sequence 载体——仅服务于上面的多段读取回归测试。</summary>
    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SequenceSegment Append(ReadOnlyMemory<byte> nextMemory)
        {
            var next = new SequenceSegment(nextMemory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
