using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace PalDDD.Core.Tests;

/// <summary>
/// EnumGenerator + IdentityGenerator 直接测试 — 用 CSharpGeneratorDriver 传源码，
/// 验证诊断输出和生成代码内容（补充消费端间接测试的盲区）。
/// </summary>
public sealed class SourceGeneratorDirectTests
{
    // ── EnumGenerator 测试 ──

    [Test]
    public async Task EnumGenerator_WithFields_GeneratesRegistrationCode()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class OrderStatus : SmartEnum<OrderStatus, string>
            {
                public static readonly OrderStatus Pending = new("pending", "待处理");
                public static readonly OrderStatus Shipped = new("shipped", "已发货");
            }
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "OrderStatus.g.cs");
        await Assert.That(source).Contains("RegisterValues");
        await Assert.That(source).Contains("Pending");
        await Assert.That(source).Contains("Shipped");
    }

    [Test]
    public async Task EnumGenerator_NoFields_ReportsWarning()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class EmptyStatus : SmartEnum<EmptyStatus, string>
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM001")).IsTrue();
    }

    [Test]
    public async Task EnumGenerator_ClassWithoutAttribute_DoesNotGenerate()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public partial class NotGenerated : SmartEnum<NotGenerated, string>
            {
                public static readonly NotGenerated A = new("a", "A");
            }
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var hasGenerated = result.Compilation.SyntaxTrees.Any(t => t.FilePath.EndsWith("NotGenerated.g.cs", StringComparison.Ordinal));
        await Assert.That(hasGenerated).IsFalse();
    }

    // ── IdentityGenerator 测试 ──

    [Test]
    public async Task IdentityGenerator_GuidType_GeneratesFromAndNew()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct TestOrderId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestOrderId.g.cs");
        await Assert.That(source).Contains("public static TestOrderId New()");
        // v35 P3（DA2）：Guid 裸名 → global::System.Guid——用户命名空间含同名 Guid 类型时
        // 裸名被遮蔽，模板特化限定名（断言同步生成器输出变更）
        await Assert.That(source).Contains("public static TestOrderId From(global::System.Guid value)");
        await Assert.That(source).Contains("ISpanParsable<TestOrderId>");
    }

    [Test]
    public async Task IdentityGenerator_IntType_GeneratesNumericOperators()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(int))]
            public readonly partial record struct TestIntId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestIntId.g.cs");
        // 数值类型应有显式/隐式转换运算符
        await Assert.That(source).Contains("operator");
        // 数值类型 New() 应抛 NotSupportedException（服务端分配）
        await Assert.That(source).Contains("NotSupportedException");
    }

    [Test]
    public async Task IdentityGenerator_UlidType_GeneratesWithoutPalid001()
    {
        // P1 回归（十七轮）：白名单 case 曾写 "ByteAether.Ulid" 而 ToDisplayString() 返回
        // "ByteAether.Ulid.Ulid"——[GenerateId(typeof(Ulid))] 恒报 PALID001（框架主推类型不可用）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using ByteAether.Ulid;

            namespace TestDomain;

            [GenerateId(typeof(Ulid))]
            public readonly partial record struct TestUlidId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestUlidId.g.cs");
        await Assert.That(source).Contains("Ulid.New()");
    }

    [Test]
    public async Task IdentityGenerator_LongType_GeneratesWithoutPalid001()
    {
        // P1 回归（十七轮）附：long 白名单十六轮零覆盖（靠巧合通过）——锁定
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(long))]
            public readonly partial record struct TestLongId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestLongId.g.cs");
        await Assert.That(source).Contains("operator");
    }

    [Test]
    public async Task IdentityGenerator_UlidViaExternAlias_GeneratesWithoutPalid001()
    {
        // v25 P3 生成器族：extern alias 下白名单比对改符号语义——ToDisplayString() 对
        // extern-aliased 类型可能带 "UlidAlias::" 前缀，Replace("global::","") 剥不掉使
        // "ByteAether.Ulid.Ulid" 精确匹配失败误报 PALID001（int/long 的同型失配 v8 已用
        // SpecialType 修，本测试锁定 Ulid 的 extern alias 路径）。
        // 桩要点：ByteAether.Ulid 程序集仅以别名引用（排除 global 引用）——双引用时
        // Roslyn 以 global 引用为 canonical，display 无别名前缀，无法复现失配路径。
        var ulidAssemblyPath = typeof(ByteAether.Ulid.Ulid).Assembly.Location;
        var references = GetReferences()
            .Where(r => !string.Equals(Path.GetFileName(r.Display), "ByteAether.Ulid.dll", StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(ulidAssemblyPath).WithAliases(ImmutableArray.Create("UlidAlias")))
            .ToArray();

        var result = RunIdentityGeneratorWithReferences(
            references,
            """
            extern alias UlidAlias;

            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(UlidAlias::ByteAether.Ulid.Ulid))]
            public readonly partial record struct ExternAliasUlidId;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID001")).IsFalse();
        var source = GetGeneratedSource(result, "ExternAliasUlidId.g.cs");
        await Assert.That(source).Contains("Ulid.New()");
    }

    [Test]
    public async Task IdentityGenerator_StringType_GeneratesNullGuard()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(string))]
            public readonly partial record struct TestStringId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestStringId.g.cs");
        // string 类型 From(null) 应抛 ArgumentException
        await Assert.That(source).Contains("ArgumentException");
    }

    [Test]
    public async Task IdentityGenerator_StructWithoutAttribute_DoesNotGenerate()
    {
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public readonly partial record struct NotGeneratedId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    // ── 八轮评审 P3：白名单诊断 / record struct-only / 全局命名空间 / record 声明诊断 ──

    [Test]
    public async Task IdentityGenerator_UnsupportedSourceType_ReportsPalid001()
    {
        // decimal 不在白名单（Guid/Ulid/int/long/string）——编译期报 PALID001 而非生成恒失败 TryParse
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(typeof(decimal))]
            public readonly partial record struct TestDecimalId;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID001")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_PlainStructDeclaration_ReportsPalid002()
    {
        // P3（九轮）：普通 partial struct 挂 [GenerateId] 报 PALID002——静默跳过让错误
        // 延迟到使用点 CS0117（无指向性）；诊断后不生成代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial struct TestPlainStructId;
            """);

        await Assert.That(result.Diagnostics).HasSingleItem();
        await Assert.That(result.Diagnostics[0].Id).IsEqualTo("PALID002");
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_GlobalNamespace_GeneratesWithoutNamespaceDeclaration()
    {
        // 全局命名空间：不再产出 "namespace _;"（旧 fallback 使生成物落入 _ 命名空间不合并）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct TestGlobalId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "TestGlobalId.g.cs");
        await Assert.That(source).DoesNotContain("namespace _;");
        await Assert.That(source).DoesNotContain("namespace TestGlobalId");
        await Assert.That(source).Contains("public readonly partial record struct TestGlobalId");
    }

    [Test]
    public async Task EnumGenerator_RecordDeclaration_ReportsPalenum003()
    {
        // record 声明此前被 predicate 静默跳过——现在报 PALENUM003 引导改用 class
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial record OrderStatusRecord : SmartEnum<OrderStatusRecord, string>
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM003")).IsTrue();
    }

    // ── 二十一轮 P2：双 partial 声明不再崩溃（PALID004/PALENUM005）──

    [Test]
    public async Task IdentityGenerator_DuplicatePartialDeclarations_ReportsPalid004_AndDoesNotCrash()
    {
        // 同一类型两个 partial 声明均挂 [GenerateId]：ForAttributeWithMetadataName 每声明
        // 触发一次 transform，两个 candidate 的 hint 相同——修复前 AddSource 同 hint 第二次
        // 调用抛 ArgumentException 使整个生成器崩溃；修复后报 PALID004 且仅首个声明生成代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct DupId
            {
                public int Extra1 => 1;
            }

            [GenerateId(typeof(Guid))]
            public readonly partial record struct DupId
            {
                public int Extra2 => 2;
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID004")).IsTrue();
        // 去重后恰好一份生成物（GetGeneratedSource 内部 Single 会因重复 hint 抛异常）
        var source = GetGeneratedSource(result, "DupId.g.cs");
        await Assert.That(source).Contains("public readonly partial record struct DupId");
    }

    [Test]
    public async Task EnumGenerator_DuplicatePartialDeclarations_ReportsPalenum005_AndDoesNotCrash()
    {
        // 镜像 IdentityGenerator 双 partial 场景——修复前同 hint AddSource 崩溃，
        // 修复后报 PALENUM005 且仅首个声明生成代码（字段跨 partial 合并收集）
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class DupStatus : SmartEnum<DupStatus, string>
            {
                public static readonly DupStatus A = new("a", "A");
            }

            [GenerateEnum]
            public partial class DupStatus : SmartEnum<DupStatus, string>
            {
                public static readonly DupStatus B = new("b", "B");
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM005")).IsTrue();
        var source = GetGeneratedSource(result, "DupStatus.g.cs");
        await Assert.That(source).Contains("RegisterValues");
    }

[Test]
    public async Task EnumGenerator_CrossFilePartial_CollectsFieldsFromBothTrees()
    {
        var result = RunGeneratorTwoTrees<EnumGeneratorProxy>(
            "using PalDDD.Core;\nnamespace TestDomain;\n[GenerateEnum]\npublic partial class CrossStatus : SmartEnum<CrossStatus, string>\n{\n}",
            "using PalDDD.Core;\nnamespace TestDomain;\npublic partial class CrossStatus : SmartEnum<CrossStatus, string>\n{\n    public static readonly CrossStatus B = new(\"b\", \"B\");\n}");

        var crashed = result.Diagnostics.Any(d => d.Id == "CS8785");
        var source = crashed ? "" : GetGeneratedSource(result, "CrossStatus.g.cs");
        await Assert.That(source).Contains("RegisterValues");
        await Assert.That(source).Contains("B");
    }

    // ── ITM-074 回归：null 源类型不再 NRE 崩溃（PALID005）──

    [Test]
    public async Task IdentityGenerator_NullSourceType_ReportsPalid005_AndDoesNotCrash()
    {
        // [GenerateId(null)] 编译期合法（构造参数允许 null）——修复前 transform 内
        // (INamedTypeSymbol)null! 在 ToDisplayString() 处 NRE，整个生成器崩溃、
        // 该编译全部生成物丢失；修复后报 PALID005 且不生成代码、不崩溃
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateId(null)]
            public readonly partial record struct NullTypeId;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID005")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    // ── v26 P3 生成器族：非 partial 声明诊断（PALENUM006）──

    [Test]
    public async Task EnumGenerator_NonPartialClass_ReportsPalenum006()
    {
        // v26 P3 生成器族：非 partial class 挂 [GenerateEnum] 此前被 predicate 的
        // PartialKeyword 前置过滤静默跳过（零诊断零生成）；现报 PALENUM006 引导补
        // partial 修饰符且不生成代码（生成物 partial class 与非 partial 用户声明
        // 无法合并，CS0260）
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public class NonPartialStatus : SmartEnum<NonPartialStatus, string>
            {
                public static readonly NonPartialStatus A = new("a", "A");
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM006")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    // ── v53 P2：非 partial 包含类型专用诊断（PALENUM009）——原复用 PALENUM007 的
    //    {1} 是人为字符串且"raise visibility"指引对该根因无效 ──

    [Test]
    public async Task EnumGenerator_NonPartialContainingType_ReportsPalenum009()
    {
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public class NonPartialOuter
            {
                [GenerateEnum]
                public partial class InnerStatus : SmartEnum<InnerStatus, string>
                {
                    public static readonly InnerStatus A = new("a", "A");
                }
            }
            """);

        var diag = result.Diagnostics.FirstOrDefault(d => d.Id == "PALENUM009");
        await Assert.That(diag).IsNotNull();
        // {1} 必须是实际包含类型名（原为人为字符串 "non-partial containing type"）
        await Assert.That(diag!.GetMessage()).Contains("NonPartialOuter");
        await Assert.That(result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))).IsEqualTo(0);
    }

    // ── v60：hint 无前缀方案——全局类型与命名空间内同名类型不碰撞 ──

    [Test]
    public async Task IdentityGenerator_GlobalAndNamespacedSameName_BothGenerate()
    {
        // 同编译内：全局 FooId 与 namespace Dup 内 FooId——hint 分别为 "FooId.g.cs" 与
        // "Dup.FooId.g.cs"（v60 无前缀方案；旧 "_" 哨兵在 namespace _ 场景碰撞）。
        // v61 记录：v60 评审 D 片另称"enum 包含类型"死胡同——经编译实证 C# 语法不允许
        // enum 体内声明嵌套类型（CS1513），场景不可构造，前置分支已回退
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct FooId;

            namespace Dup
            {
                [GenerateId(typeof(Guid))]
                public readonly partial record struct FooId;
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID004")).IsFalse();

        // ITM-645 补强：Test 名含 BothGenerate，原只断言"无 PALID004"（不证明真产出）——
        // 此处断言两个同名 FooId（全局 + Dup 命名空间）各自产出独立生成物（hint 无前缀方案）。
        var generatedPaths = result.Compilation.SyntaxTrees.Select(t => t.FilePath).ToList();
        await Assert.That(generatedPaths.Any(p =>
            p.EndsWith("FooId.g.cs", StringComparison.Ordinal)
            && !p.EndsWith("Dup.FooId.g.cs", StringComparison.Ordinal))).IsTrue();
        await Assert.That(generatedPaths.Any(p =>
            p.EndsWith("Dup.FooId.g.cs", StringComparison.Ordinal))).IsTrue();
    }

    // ── v53 P1：PALID007 非 partial 包含类型——原落 default 报 PALID001 错误指引 ──

    [Test]
    public async Task IdentityGenerator_NonPartialContainingType_ReportsPalid007NotPalid001()
    {
        // Guid 在白名单内——修复前此场景报 PALID001 "unsupported source type 'System.Guid'"
        //（指引彻底反向：真实修复是给 Outer 加 partial）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            public class NonPartialOuter
            {
                [GenerateId(typeof(Guid))]
                public readonly partial record struct InnerId;
            }
            """);

        var diag = result.Diagnostics.FirstOrDefault(d => d.Id == "PALID007");
        await Assert.That(diag).IsNotNull();
        await Assert.That(diag!.GetMessage()).Contains("NonPartialOuter");
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID001")).IsFalse();
        await Assert.That(result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal))).IsEqualTo(0);
    }

    // ── v26 P3 生成器族：SmartEnum 基类比对符号化（extern alias 下不误报 PALENUM002）──

    [Test]
    public async Task EnumGenerator_SmartEnumViaExternAlias_GeneratesWithoutPalenum002()
    {
        // v26 P3 生成器族：SmartEnum 基类比对符号化——OriginalDefinition.ToDisplayString()
        // 在 extern alias 下带别名前缀（"PalAlias::PalDDD.Core.SmartEnum<TSelf, TValue>"），
        // 字符串精确比对失配误报 PALENUM002（Error 级）；修复后用 Name + 命名空间链 +
        // Arity 判定（镜像 v25 IdentityGenerator 白名单符号语义化）。
        // 桩要点：PalDDD.Core 程序集仅以别名引用（排除 global 引用）——双引用时
        // Roslyn 以 global 引用为 canonical，display 无别名前缀，无法复现失配路径
        var coreAssemblyPath = typeof(GenerateMessageAttribute).Assembly.Location;
        var references = GetReferences()
            .Where(r => !string.Equals(Path.GetFileName(r.Display), "PalDDD.Core.dll", StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(coreAssemblyPath).WithAliases(ImmutableArray.Create("PalAlias")))
            .ToArray();

        var result = RunEnumGeneratorWithReferences(
            references,
            """
            extern alias PalAlias;

            namespace TestDomain;

            [PalAlias::PalDDD.Core.GenerateEnum]
            public partial class AliasStatus : PalAlias::PalDDD.Core.SmartEnum<AliasStatus, string>
            {
                public static readonly AliasStatus A = new("a", "A");
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM002")).IsFalse();
        var source = GetGeneratedSource(result, "AliasStatus.g.cs");
        await Assert.That(source).Contains("RegisterValues");
    }

    [Test]
    public async Task EnumGenerator_ClassNotDerivingSmartEnum_ReportsPalenum002()
    {
        // 2026-09-10 补正向触发守护（诊断覆盖门禁加固后暴露的缺口）：PALENUM002 在测试中
        // 此前仅有 .IsFalse() 反向断言（"某场景不误报"）——反向断言在实现被破坏后反而更易
        // 通过（不误报→破坏后仍不误报），不构成守护，与 PALENUM004/PALID003 同类。
        // 本测试正向触发：partial class 挂 [GenerateEnum] 但基类非 SmartEnum<TSelf,TValue>
        // → Error 级 PALENUM002（隔层/无继承路径，EnumGenerator.cs:273 判定）。
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class NotDerivedStatus
            {
                public static readonly NotDerivedStatus A = new();
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM002")).IsTrue();
    }

    // ── v26 P3 生成器族：PALID005 消息区分 null 与非 NamedType 源类型 ──

    [Test]
    public async Task IdentityGenerator_TypeParameterSourceType_ReportsPalid005WithNamedTypeMessage()
    {
        // v26 P3 生成器族：[GenerateId(typeof(T))] 的 T 是 ITypeParameterSymbol——
        // 原实现与 null 同报 "null source type" 消息（非 null 却称 null，误导排障）；
        // 修复后消息指明 "not a named type"（Id 不变，仍 PALID005）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public class Wrapper<T>
            {
                [GenerateId(typeof(T))]
                public readonly partial record struct WrapperId;
            }
            """);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "PALID005");
        await Assert.That(diagnostic.GetMessage()).Contains("not a named type");
    }

    // ── v33 P3：private/protected nested 声明拦截（PALENUM007 / PALID006 同族）──

    [Test]
    public async Task EnumGenerator_PrivateNestedClass_ReportsPalenum007()
    {
        // v33 P3：private nested class 挂 [GenerateEnum]——生成物（namespace 级 partial
        // class + [ModuleInitializer] 静态构造）以裸名引用该类型，可访问性低于 internal
        // 的嵌套类型对生成物不可见（CS0122 落在 auto-generated 文件）。编译期报
        // PALENUM007 且不生成坏代码
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public partial class Outer
            {
                [GenerateEnum]
                private partial class HiddenStatus : SmartEnum<HiddenStatus, string>
                {
                    public static readonly HiddenStatus A = new("a", "A");
                }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM007")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task EnumGenerator_FileLocalClass_ReportsPalenum007()
    {
        // v39 P3：file-local 类型（C# 12 file class）逃过可访问性链拦截——IsFileLocal=true
        // 时 DeclaredAccessibility 报 Internal（Accessibility 枚举无 File 成员），internal 腿
        // 在 GetBlockingAccessibility 中被放行；但 file-local 可见性仅限声明文件，生成物
        // emitted 到独立 generated 文件不可引用（CS0122 落在 auto-generated 文件，同
        // private nested 根因）。file-local 视为阻断层，编译期报 PALENUM007 且不生成坏代码
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            file partial class FileLocalStatus : SmartEnum<FileLocalStatus, string>
            {
                public static readonly FileLocalStatus A = new("a", "A");
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM007")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_PrivateNestedStruct_ReportsPalid006()
    {
        // v33 P3：private nested record struct 挂 [GenerateId]——生成物的 namespace 级
        // TypeConverter/JsonConverter 以裸名引用该类型 CS0122（同 PALENUM007 根因）。
        // 编译期报 PALID006 且不生成坏代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            public sealed class Outer
            {
                [GenerateId(typeof(Guid))]
                private readonly partial record struct HiddenId;
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID006")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_ProtectedOrInternalNestedStruct_ReportsPalid006()
    {
        // v36 P2：protected internal 声明此前经 GeneratorAccessibility 的 ProtectedOrInternal
        // 放行（该放行对 Enum/Message 姊妹成立——其生成物不带访问修饰符，与用户声明合并无
        // 冲突），对 Identity 不成立：生成物硬编码 public readonly partial record struct，与
        // protected internal 用户声明合并报 CS0262（net11 探针双向实证）。链检查通过后自身
        // 声明收紧为 public/internal，protected internal 编译期报 PALID006 且不生成坏代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            public class Outer
            {
                [GenerateId(typeof(Guid))]
                protected internal readonly partial record struct PartnerId;
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID006")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    [Test]
    public async Task IdentityGenerator_InternalTopLevelStruct_ReportsPalid006()
    {
        // v37 P2：internal 顶层 record struct 挂 [GenerateId]——v36 收紧阈值（public/internal）
        // 仍放行 internal（当初顾虑"误伤 internal 顶层合法场景"），但 internal 声明与硬编码
        // public 生成物合并必报 CS0262（双向探针实证，不存在可工作的 internal 用法）。
        // v37 阈值收紧为 Public-only，internal 编译期报 PALID006 且不生成坏代码
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            internal readonly partial record struct InternalId;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID006")).IsTrue();
        var generatedCount = result.Compilation.SyntaxTrees.Count(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal));
        await Assert.That(generatedCount).IsEqualTo(0);
    }

    // ── v35 P3（DA4）：普通 struct / interface 声明引导诊断（PALENUM008）──

    [Test]
    public async Task EnumGenerator_PlainStructDeclaration_ReportsPalenum008()
    {
        // v35 P3：普通 partial struct 挂 [GenerateEnum]——struct 的 BaseType 恒为 ValueType，
        // 无法继承 SmartEnum 基类，原落基类检查报 PALENUM002 "does not directly inherit
        // SmartEnum"（误导用户"改基类"——struct 根本没有可改的基类）；现报 PALENUM008
        // 引导改用 partial class。
        // ⚠️ 探针声明（v35 P3 实测）：GenerateEnumAttribute 的 AttributeTargets 限 Class，
        // 本桩挂载即产生 CS0592（compilation 诊断，不影响 generator driver 运行）——
        // ForAttributeWithMetadataName 的 transform 在 CS0592 下仍触发，PALENUM008 可达
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial struct StructStatus
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM008")).IsTrue();
        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM002")).IsFalse();
    }

    [Test]
    public async Task EnumGenerator_InterfaceDeclaration_ReportsPalenum008()
    {
        // v35 P3（DA4）姊妹：interface 的 BaseType 恒为 null，同样无法继承 SmartEnum——
        // 同报 PALENUM008（{1} 显示 "interface"）
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial interface IStatusContract
            {
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM008")).IsTrue();
        var diagnostic = result.Diagnostics.Single(d => d.Id == "PALENUM008");
        await Assert.That(diagnostic.GetMessage()).Contains("interface");
    }

    // ── v35 P3（DA2）：用户命名空间同名类型遮蔽生成物裸名（Ulid/Guid）──

    [Test]
    public async Task IdentityGenerator_UlidShadowedByUserType_GeneratesAliasedReferences()
    {
        // v35 P3：用户命名空间含同名 class Ulid 时，生成物裸名 Ulid（IPalIdentity<Ulid>/
        // public Ulid Value 等）解析到用户类型而非 ByteAether Ulid——生成物编译失败。
        // 修复后模板对 Ulid 输出 PalUlid 别名（ulidUsing 的 using+using 双发已备，v24 机制）。
        // 桩内声明 class Ulid 复现遮蔽环境；typeof(PalUlid) 保证白名单符号判定命中真实
        // ByteAether Ulid（typeof(Ulid) 在遮蔽环境下会解析到用户类，报 PALID001）
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using PalUlid = ByteAether.Ulid.Ulid;

            namespace TestDomain;

            public class Ulid { }

            [GenerateId(typeof(PalUlid))]
            public readonly partial record struct ShadowedUlidId;
            """);

        await Assert.That(result.Diagnostics).IsEmpty();
        var source = GetGeneratedSource(result, "ShadowedUlidId.g.cs");
        await Assert.That(source).Contains("IPalIdentity<PalUlid>");
        await Assert.That(source).Contains("public PalUlid Value { get; init; }");
        // 编译级验证：生成物与用户类型合并后零 Error——裸名遮蔽若回退，生成物中对
        // TestDomain.Ulid 的成员引用（New/TryParse 等）必在此复现 CS 错误
        var errors = result.Compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        await Assert.That(errors).IsEmpty();
    }

    // ── 泛型声明拦截断言（补 PALENUM004/PALID003 此前仅注释"镜像"无断言的缺口；
    //    mutation 实证：破坏检测后 289/289 全绿，同型 PALMSG006 有 2 测试守护）──

    [Test]
    public async Task EnumGenerator_OnGenericDeclaration_ReportsPalenum004()
    {
        // 泛型 SmartEnum 生成物以裸名声明 partial class（与用户泛型声明同名冲突），
        // 且 [ModuleInitializer] 不允许位于泛型类型成员——编译期报 PALENUM004 引导移出。
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            [GenerateEnum]
            public partial class GenericStatus<T> : SmartEnum<GenericStatus<T>, string>
            {
                public static readonly GenericStatus<T> Pending = new("pending", "待处理");
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM004")).IsTrue();
    }

    [Test]
    public async Task EnumGenerator_NestedInsideGenericDeclaration_ReportsPalenum004()
    {
        // 嵌套于泛型包含类型同样拦截——IsWithinGenericContainingType 递归检测路径，
        // 与自身泛型（Arity > 0）是两个独立分支。
        var result = RunEnumGenerator(
            """
            using PalDDD.Core;

            namespace TestDomain;

            public partial class Outer<T>
            {
                [GenerateEnum]
                public partial class InnerStatus : SmartEnum<InnerStatus, string>
                {
                    public static readonly InnerStatus Pending = new("pending", "待处理");
                }
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALENUM004")).IsTrue();
    }

    [Test]
    public async Task IdentityGenerator_OnGenericDeclaration_ReportsPalid003()
    {
        // 泛型 ID 生成物中 namespace 级 TypeConverter/JsonConverter 以裸名引用嵌套 ID，
        // 自身泛型时裸名声明与用户 partial record struct Foo<T> 同名冲突——报 PALID003。
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            [GenerateId(typeof(Guid))]
            public readonly partial record struct GenericId<T>;
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID003")).IsTrue();
    }

    [Test]
    public async Task IdentityGenerator_NestedInsideGenericDeclaration_ReportsPalid003()
    {
        // 嵌套于泛型包含类型的 ID 同样拦截（typeof(Outer.Foo) 在泛型外层无类型参数可用）。
        var result = RunIdentityGenerator(
            """
            using PalDDD.Core;
            using System;

            namespace TestDomain;

            public partial class Outer<T>
            {
                [GenerateId(typeof(Guid))]
                public readonly partial record struct InnerId;
            }
            """);

        await Assert.That(result.Diagnostics.Any(d => d.Id == "PALID003")).IsTrue();
    }

    // ── 辅助方法（参照 MessageRegistryGeneratorTests 的模式）──

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunEnumGenerator(string source)
        => RunGenerator<EnumGeneratorProxy>(source);

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunIdentityGenerator(string source)
        => RunGenerator<IdentityGeneratorProxy>(source);

private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunGeneratorTwoTrees<TProxy>(string source1, string source2)
        where TProxy : IGeneratorProxy, new()
    {
        var proxy = new TProxy();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.SourceGen.DirectTests",
            [CSharpSyntaxTree.ParseText(source1, parseOptions), CSharpSyntaxTree.ParseText(source2, parseOptions)],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var generator = proxy.LoadGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunGenerator<TProxy>(string source)
        where TProxy : IGeneratorProxy, new()
    {
        var proxy = new TProxy();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.SourceGen.DirectTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = proxy.LoadGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    // v25 P3 生成器族：extern alias 场景需注入带别名（UlidAlias）的程序集引用——
    // 复用 IdentityGeneratorProxy，仅替换引用集
    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunIdentityGeneratorWithReferences(
        MetadataReference[] references,
        string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.SourceGen.DirectTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new IdentityGeneratorProxy().LoadGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    // v26 P3 生成器族：EnumGenerator 的 extern alias 场景（PalDDD.Core 以别名引用）——
    // 镜像 RunIdentityGeneratorWithReferences，仅替换生成器代理
    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) RunEnumGeneratorWithReferences(
        MetadataReference[] references,
        string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "PalDDD.SourceGen.DirectTests",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new EnumGeneratorProxy().LoadGenerator();
        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updatedCompilation, out var diagnostics);
        return (updatedCompilation, diagnostics);
    }

    private static string GetGeneratedSource((Compilation Compilation, ImmutableArray<Diagnostic> _) result, string fileNameEndsWith)
    {
        var tree = result.Compilation.SyntaxTrees.Single(
            t => t.FilePath.EndsWith(fileNameEndsWith, StringComparison.Ordinal));
        return tree.ToString();
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedPlatformAssemblies is not null)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
                yield return MetadataReference.CreateFromFile(path);
        }

        yield return MetadataReference.CreateFromFile(typeof(GenerateMessageAttribute).Assembly.Location);
        // P1 回归（十七轮）：[GenerateId(typeof(Ulid))] 测试需要 ByteAether.Ulid 程序集引用
        yield return MetadataReference.CreateFromFile(typeof(ByteAether.Ulid.Ulid).Assembly.Location);
    }

    // ── Generator 代理（从编译后的 DLL 加载，避免 analyzer 引用问题）──

    private interface IGeneratorProxy
    {
        IIncrementalGenerator LoadGenerator();
    }

    private sealed class EnumGeneratorProxy : IGeneratorProxy
    {
        public IIncrementalGenerator LoadGenerator()
            => LoadFromAssembly("PalDDD.Core.SourceGen.EnumGenerator");
    }

    private sealed class IdentityGeneratorProxy : IGeneratorProxy
    {
        public IIncrementalGenerator LoadGenerator()
            => LoadFromAssembly("PalDDD.Core.SourceGen.IdentityGenerator");
    }

    private static IIncrementalGenerator LoadFromAssembly(string typeName)
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Name ?? "Debug";
        var generatorPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PalDDD.Core.SourceGen", "bin", configuration, "netstandard2.0",
            "PalDDD.Core.SourceGen.dll"));

        var assembly = System.Reflection.Assembly.LoadFrom(generatorPath);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(type)!;
    }
}
