namespace PalDDD.Analyzers.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PalDDD.Analyzers;

/// <summary>
/// R10（D-F5，2026-10-15 性能实施轮）：符号比对族谓词的语义矩阵 + 零分配断言。
/// 这些谓词位于 IDE 增量分析热路径——每类型分析发生数十次比对（基类链 × 接口集 ×
/// attribute 集逐候选），原实现每次比对构造全名串/闭包/前缀串。分配断言在旧实现
/// 等价体下判红（红证明在案），新实现零分配；语义矩阵同时锁定结构比对比全名串
/// 比对的逐字等价（含 ITM-220 嵌套名与 v26 P3 global 命名空间分支）。
/// 2026-10-27 preview 发布首跑实证：分配断言用 GC.GetAllocatedBytesForCurrentThread
/// （线程级量具），并行执行时同线程其他测试的分配计入 delta——tag-ref CI 上偶发
/// perOp=4.03 误红（同提交 dev-ref CI 同测试绿）。类级 NotInParallel 隔离测量窗口。
/// </summary>
[NotInParallel("analyzer-alloc")]
public sealed class AnalyzerAllocationTests
{
    private const int Iterations = 10_000;

    private static readonly CSharpCompilation Fixture = BuildFixture();

    [Test]
    public async Task MetadataNameEquals_SemanticsMatrix_AndZeroAllocation()
    {
        // ── 语义矩阵（与旧全名串比对逐字等价）──
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer"), "Ns.Outer")).IsTrue();
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer+Inner"), "Ns.Outer+Inner")).IsTrue();
        // ITM-220：嵌套名不与顶层同名碰撞
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer+Inner"), "Ns.Inner")).IsFalse();
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("GlobalProbe+GlobalInner"), "GlobalProbe+GlobalInner")).IsTrue();
        // 泛型元数 `n（定义与构造泛型接口同形）
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.IMarker`1"), "Ns.IMarker`1")).IsTrue();
        var constructed = GetType("Ns.Impl").AllInterfaces[0];
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(constructed, "Ns.IMarker`1")).IsTrue();
        // 反例（前缀错配/命名空间错配/元数错配）
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer"), "Ns.OuterX")).IsFalse();
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer"), "System.Object")).IsFalse();
        await Assert.That(StrategicDddAnalyzer.MetadataNameEquals(GetType("Ns.Outer"), "Ns.Outer`1")).IsFalse();

        // ── 分配断言：命中与未命中两腿各 N 次比对零分配 ──
        var candidates = new[]
        {
            GetType("Ns.Outer"),
            GetType("Ns.Outer+Inner"),
            GetType("Ns.Impl"),
            constructed,
            GetType("GlobalProbe+GlobalInner")
        };
        WarmUp(() =>
        {
            _ = StrategicDddAnalyzer.MetadataNameEquals(candidates[0], "Ns.Outer+Inner");
            _ = StrategicDddAnalyzer.MetadataNameEquals(candidates[0], "System.Object");
        });
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            _ = StrategicDddAnalyzer.MetadataNameEquals(candidates[i % candidates.Length], "Ns.Outer+Inner");
            _ = StrategicDddAnalyzer.MetadataNameEquals(candidates[i % candidates.Length], "System.Object");
        }
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(Iterations * 2);
        await Assert.That(perOp).IsLessThan(1.0);
    }

    [Test]
    public async Task HasExplicitInterfaceImplementationNamed_Semantics_AndZeroAllocation()
    {
        // ── 语义：显式实现（简单名匹配）与无显式实现两形态 ──
        var explicitImplementation = GetType("Ns.Named").GetMembers()
            .OfType<IPropertySymbol>()
            .Single(static p => !p.ExplicitInterfaceImplementations.IsEmpty);
        var noneImplementation = Fixture.GetSpecialType(SpecialType.System_String).GetMembers("Length")
            .OfType<IPropertySymbol>()
            .Single();
        await Assert.That(StrategicDddAnalyzer.HasExplicitInterfaceImplementationNamed(explicitImplementation, "Name")).IsTrue();
        await Assert.That(StrategicDddAnalyzer.HasExplicitInterfaceImplementationNamed(explicitImplementation, "Other")).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasExplicitInterfaceImplementationNamed(noneImplementation, "Length")).IsFalse();

        // ── 分配断言：无显式实现形态（旧 Any(闭包) 在此形态同样每次分配闭包对象）──
        WarmUp(() => _ = StrategicDddAnalyzer.HasExplicitInterfaceImplementationNamed(noneImplementation, "Length"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            _ = StrategicDddAnalyzer.HasExplicitInterfaceImplementationNamed(noneImplementation, "Length");
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
        await Assert.That(perOp).IsLessThan(1.0);
    }

    [Test]
    public async Task BelongsToBoundedContext_Semantics_AndZeroAllocation()
    {
        // ── 语义（序数等于 或 ctx + '.' 前缀；拼接形态 StartsWith(ctx + ".") 等价拆分）──
        await Assert.That(StrategicDddAnalyzer.BelongsToBoundedContext("orders", "orders")).IsTrue();
        await Assert.That(StrategicDddAnalyzer.BelongsToBoundedContext("orders.created.v1", "orders")).IsTrue();
        // 前缀字符命中但无点分隔（旧形态 StartsWith("orders.") 判否）
        await Assert.That(StrategicDddAnalyzer.BelongsToBoundedContext("ordersx", "orders")).IsFalse();
        // 前缀错配（ordering 不以 orders 开头）
        await Assert.That(StrategicDddAnalyzer.BelongsToBoundedContext("ordering.created", "orders")).IsFalse();
        await Assert.That(StrategicDddAnalyzer.BelongsToBoundedContext("a.b", "")).IsFalse();

        WarmUp(() => _ = StrategicDddAnalyzer.BelongsToBoundedContext("orders.created.v1", "orders"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            _ = StrategicDddAnalyzer.BelongsToBoundedContext("orders.created.v1", "orders");
            _ = StrategicDddAnalyzer.BelongsToBoundedContext("ordering.created", "orders");
        }
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(Iterations * 2);
        await Assert.That(perOp).IsLessThan(1.0);
    }

    [Test]
    public async Task HasVersionSuffix_Semantics_AndZeroAllocation()
    {
        // ── 语义（".v" + schemaVersion 序数后缀的逐位等价）──
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("orders.created.v1", 1)).IsTrue();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v12", 12)).IsTrue();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v10", 10)).IsTrue();
        // 负号腿（int 全域等价；调用方已门控 >= 1，防御性保留）
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v-1", -1)).IsTrue();
        // 反例：版本错位/前导零/缺段/非数字尾
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("orders.created.v1", 12)).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v12", 1)).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v01", 1)).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("v1", 1)).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v", 1)).IsFalse();
        await Assert.That(StrategicDddAnalyzer.HasVersionSuffix("x.v2extra", 2)).IsFalse();

        WarmUp(() => _ = StrategicDddAnalyzer.HasVersionSuffix("orders.created.v1", 1));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
        {
            _ = StrategicDddAnalyzer.HasVersionSuffix("orders.created.v1", 1);
            _ = StrategicDddAnalyzer.HasVersionSuffix("x.v01", 1);
        }
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)(Iterations * 2);
        await Assert.That(perOp).IsLessThan(1.0);
    }

    private static CSharpCompilation BuildFixture()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            public class GlobalProbe { public class GlobalInner { } }

            namespace Ns
            {
                public class Outer { public class Inner { } }
                public interface IMarker<T> { }
                public class Impl : IMarker<int> { }
                public interface INamed { string Name { get; } }
                public class Named : INamed { string INamed.Name => "x"; }
            }
            """);
        return CSharpCompilation.Create(
            "analyzer-probe",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    }

    private static INamedTypeSymbol GetType(string metadataName)
        => Fixture.GetTypeByMetadataName(metadataName)
            ?? throw new InvalidOperationException($"Fixture type not found: {metadataName}");

    /// <summary>预热：确保被测谓词已 JIT（分层编译/OSR 的编译路径不污染测量段）。</summary>
    private static void WarmUp(Action action)
    {
        for (var i = 0; i < 100; i++)
            action();
    }
}
