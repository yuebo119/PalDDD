using FsCheck;
using FsCheck.Fluent;
using System.Text;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 🎲 属性测试 — 用 FsCheck 生成随机输入，覆盖人工难列举的边界组合
// ═══════════════════════════════════════════════════════════════
// 原则：测试"性质"（property）而非"示例"（example）。
// 用 FsCheck 核心 API（Prop.ForAll + Check.QuickThrowOnFailure），
// 在 TUnit [Test] 中驱动。
// 默认 100 次随机迭代，自动发现人工遗漏的边界。
// ═══════════════════════════════════════════════════════════════

public sealed class ValueObjectPropertyTests
{
    [Test]
    public void ValueObject_ArbitraryInt_PreservesValue()
    {
        Prop.ForAll((int value) =>
        {
            var vo = new ValueObject<int>(value);
            return vo.Value == value;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void ValueObject_ImplicitConversion_RoundTrip()
    {
        Prop.ForAll((int value) =>
        {
            var vo = new ValueObject<int>(value);
            int back = vo;
            return back == value;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void TryFormat_MatchesToString()
    {
        Prop.ForAll((int value) =>
        {
            var vo = new ValueObject<int>(value);
            Span<byte> buffer = stackalloc byte[32];
            var success = vo.TryFormat(buffer, out var bytesWritten, default, null);
            if (!success) return false;
            var utf8String = Encoding.UTF8.GetString(buffer[..bytesWritten]);
            return utf8String == vo.ToString();
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void RowVersion_Next_AlwaysIncrement()
    {
        Prop.ForAll((int start) =>
        {
            var v = new RowVersion(start);
            return v.Next().Value == start + 1;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void RowVersion_Equality_ByValue()
    {
        Prop.ForAll((int a, int b) =>
        {
            var va = new RowVersion(a);
            var vb = new RowVersion(b);
            return (va == vb) == (a == b);
        }).QuickCheckThrowOnFailure();
    }
}

public sealed class AggregateInvariantPropertyTests
{
    private static readonly Gen<string> NonEmptyString =
        Gen.Choose(1, 50).SelectMany(len =>
            Gen.ArrayOf(Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()), len)
               .Select(chars => new string(chars)));

    [Test]
    public void AddLine_ValidItem_TotalAmountEqualsSum()
    {
        var gen = from productId in NonEmptyString
                  from quantity in Gen.Choose(1, 1000)
                  from unitPrice in Gen.Choose(0, 10000)
                  select (productId, quantity, unitPrice);

        Prop.ForAll(Arb.From(gen), tuple =>
        {
            var order = new InvariantOrder(Guid.NewGuid(), "Alice");
            order.AddLine(tuple.productId, tuple.quantity, tuple.unitPrice);
            return order.TotalAmount == tuple.quantity * tuple.unitPrice;
        }).QuickCheckThrowOnFailure();
    }

    [Test]
    public void AddLine_MultipleItems_TotalAmountAccumulates()
    {
        var gen = from p1 in NonEmptyString
                  from q1 in Gen.Choose(1, 100)
                  from price1 in Gen.Choose(0, 1000)
                  from p2 in NonEmptyString
                  from q2 in Gen.Choose(1, 100)
                  from price2 in Gen.Choose(0, 1000)
                  select (p1, q1, price1, p2, q2, price2);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var order = new InvariantOrder(Guid.NewGuid(), "Alice");
            order.AddLine(t.p1, t.q1, t.price1);
            order.AddLine(t.p2, t.q2, t.price2);
            return order.TotalAmount == t.q1 * t.price1 + t.q2 * t.price2;
        }).QuickCheckThrowOnFailure();
    }
}
