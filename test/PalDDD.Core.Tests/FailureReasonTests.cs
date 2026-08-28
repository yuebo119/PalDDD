namespace PalDDD.Core.Tests;

/// <summary>FailureReason.Normalize/Truncate 契约（v8 评审补测：截断/空白归一/代理对完整性四路径；
/// v29 P3 补 Truncate：null 语义保持/截断/代理对守卫）。</summary>
public sealed class FailureReasonTests
{
    [Test]
    public async Task Normalize_NullMessage_ReturnsNoMessagePlaceholder()
        => await Assert.That(FailureReason.Normalize(null)).IsEqualTo("(no message)");

    [Test]
    public async Task Normalize_BlankMessage_ReturnsNoMessagePlaceholder()
    {
        await Assert.That(FailureReason.Normalize("")).IsEqualTo("(no message)");
        await Assert.That(FailureReason.Normalize("   ")).IsEqualTo("(no message)");
    }

    [Test]
    public async Task Normalize_ShortMessage_ReturnsUnchanged()
        => await Assert.That(FailureReason.Normalize("broker timeout")).IsEqualTo("broker timeout");

    [Test]
    public async Task Normalize_OverlongMessage_TruncatesToMaxLength()
    {
        var longMessage = new string('x', FailureReason.MaxLength + 100);
        var normalized = FailureReason.Normalize(longMessage);
        await Assert.That(normalized.Length).IsEqualTo(FailureReason.MaxLength);
    }

    [Test]
    public async Task Normalize_TruncationAtSurrogatePair_DoesNotLeaveLoneHighSurrogate()
    {
        // 构造截断点恰落在高代理上的输入：'a'*(Max-1) + emoji（2 char 代理对）——
        // [..Max] 的末位是高代理（低代理被切），防御应回退一位
        var message = new string('a', FailureReason.MaxLength - 1) + "🎉";
        var normalized = FailureReason.Normalize(message);
        // 截断点落在 MaxLength（恰在高代理后）——防御回退一位，末字符必须是完整 'a'
        await Assert.That(normalized.Length).IsEqualTo(FailureReason.MaxLength - 1);
        await Assert.That(char.IsHighSurrogate(normalized[^1])).IsFalse();
    }

    [Test]
    public async Task Normalize_OverlongWhitespaceOnly_TruncatesThenNormalizesToPlaceholder()
    {
        var longBlank = new string(' ', FailureReason.MaxLength + 10);
        await Assert.That(FailureReason.Normalize(longBlank)).IsEqualTo("(no message)");
    }

    // ── v29 P3：Truncate（仅截断不归一，存储层 2040 兜底族共享收口）──────────────

    [Test]
    public async Task Truncate_NullValue_PreservesNull()
        => await Assert.That(FailureReason.Truncate(null, 2040)).IsNull();

    [Test]
    public async Task Truncate_ShortValue_ReturnsUnchanged()
    {
        await Assert.That(FailureReason.Truncate("broker timeout", 2040)).IsEqualTo("broker timeout");
        // 边界：恰好等于上限不截断
        await Assert.That(FailureReason.Truncate(new string('x', 2040), 2040)).IsEqualTo(new string('x', 2040));
    }

    [Test]
    public async Task Truncate_OverlongValue_TruncatesToMaxLength()
        => await Assert.That(FailureReason.Truncate(new string('x', 2041), 2040)!.Length).IsEqualTo(2040);

    [Test]
    public async Task Truncate_TruncationAtSurrogatePair_DoesNotLeaveLoneHighSurrogate()
    {
        // 构造截断点恰落在高代理上的输入：'a'*2039 + emoji（2 char 代理对）→ 长度 2041——
        // [..2040] 的末位是高代理（低代理被切），防御应回退一位（镜像 Normalize 同名用例形态）
        var value = new string('a', 2039) + "🎉";
        var truncated = FailureReason.Truncate(value, 2040)!;
        await Assert.That(truncated.Length).IsEqualTo(2039);
        await Assert.That(char.IsHighSurrogate(truncated[^1])).IsFalse();
    }

    [Test]
    public async Task Truncate_NegativeMaxLength_ThrowsArgumentOutOfRange()
    {
        // v30 P3 守卫回归：负 maxLength 须在入口抛 ArgumentOutOfRangeException（参数契约可定位），
        // 而非落到切片表达式抛同型异常但错误信息指向内部实现
        await Assert.That(() => FailureReason.Truncate("value", -1)).Throws<ArgumentOutOfRangeException>();
        // null + 负值：参数校验优先于 null 短路（契约违规不静默放行）
        await Assert.That(() => FailureReason.Truncate(null, -1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Truncate_ZeroMaxLength_ReturnsEmptyString()
    {
        // v30 P3 契约文档化：maxLength=0 返回空串（非 null 输入截到零长——
        // Length <= 0 对非空字符串恒 false，走截断分支切到 0 位）
        await Assert.That(FailureReason.Truncate("value", 0)).IsEqualTo(string.Empty);
    }
}
