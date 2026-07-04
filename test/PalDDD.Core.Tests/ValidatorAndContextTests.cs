namespace PalDDD.Core.Tests;

public sealed class ValidationResultTests
{
    [Test]
    public async Task Success_IsValid()
    {
        var result = PalValidationResult.Success();
        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Errors).IsEmpty();
    }

    [Test]
    public async Task Failed_WithErrors_IsNotValid()
    {
        var errors = new List<PalValidationError>
        {
            new("Name", "不能为空"),
            new("Age", "必须大于 0")
        };
        var result = PalValidationResult.Failed([.. errors]);
        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Failed_SingleError_IsNotValid()
    {
        var result = PalValidationResult.Failed("Email", "格式不正确");
        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors).HasSingleItem();
        await Assert.That(result.Errors[0].PropertyName).IsEqualTo("Email");
        await Assert.That(result.Errors[0].Message).IsEqualTo("格式不正确");
    }

    [Test]
    public async Task Equals_ByIsValid()
    {
        var a = PalValidationResult.Success();
        var b = PalValidationResult.Success();
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task NotEquals_DifferentResults_ReturnsTrue()
    {
        var success = PalValidationResult.Success();
        var failed = PalValidationResult.Failed("X", "error");
        await Assert.That(success.Equals(failed)).IsFalse();
        await Assert.That(success != failed).IsTrue();
    }
}

public sealed class ValidationErrorTests
{
    [Test]
    public async Task RecordStruct_Equality()
    {
        var a = new PalValidationError("Name", "不能为空");
        var b = new PalValidationError("Name", "不能为空");
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    }
}
