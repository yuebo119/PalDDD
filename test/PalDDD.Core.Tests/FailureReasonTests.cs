namespace PalDDD.Core.Tests;

/// <summary>FailureReason.Normalize 契约（v8 评审补测：截断/空白归一/代理对完整性四路径）。</summary>
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
}
