using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.Core.Tests;

// ═══════════════════════════════════════════════════════════════
// 源码守卫测试（Roslyn 语法树判定，MIG-007/008/009）
// ═══════════════════════════════════════════════════════════════
// 承接来源（任务清单 docs/design/script-migration-tasks.md）：
//   .ai/scripts/gate-check.sh      G7  → 守卫 1（反射 API）
//                                  G8  → 守卫 2（Expression.Compile / dynamic）
//                                  G11 → 守卫 3（.Result / .Wait / GetAwaiter().GetResult / async void）
//                                  G12 → 守卫 4（await 必带 ConfigureAwait）
//                                  G1  → 守卫 5（具体异常类型 sealed，Middleware/Extensions/CodeFix 豁免）
//                                  G9  → 守卫 3（async void——补显式 void 返回类型的 lambda 形态后完全覆盖）
//                                  G10 → 守卫 6（TransactionScope 标识符禁令）
//                                  G17 → 守卫 6（UtcNow 直调 + TimeProvider.System 内联禁令，附注零残留后升格 FAIL）
//   scripts/verify-conventions.sh  V1（反射族）/ V2（async void）/ V3（.Result）/ V4（.Wait）
//                                  → 已下沉至本文件同名守卫，脚本仅保留 V5（TODO grep）/ V6（build）/ V7（test）
//
// 与 bash 版的判定差异（有意收紧，方向为更严）：
//   1. AOT 豁免只认真实 attribute（沿祖先链的方法/属性/访问器/类型声明），不再用
//      "命中行前 30 行文本含关键字" 的窗口近似。[SuppressMessage]/[UnconditionalSuppressMessage]
//      文案含 "RequiresDynamicCode" 字样不豁免——ITM-073 回归样本固化于守卫 1/2 矩阵。
//   2. .Result 豁免为节点级：位于 IsCompletedSuccessfully 条件 if 的 then 分支内
//      （bash 为前 3 行文本窗口，else 分支也被误豁免）。
//   3. await 按节点级判定 awaited 表达式是否以 ConfigureAwait(...) 结尾
//      （bash 为文件级 await 数 ≤ CA 数差值计数——裸 await 可被同文件多余 CA 抵消）。
//      await using / await foreach 语法上不是 AwaitExpression，天然不误报。
//   4. 守卫 5（G1）：按语法节点判定 public 类型声明，"public record XxxException"
//      （record 不带 class 关键字）bash 正则只匹配 "record class" 显式形态，此处两种写法均命中；
//      partial 分部声明逐分部判定（C# 修饰符须跨分部一致，缺 sealed 的分部本身即违规形态）。
//   5. 守卫 6（G10/G17）：标识符/成员访问节点级判定，天然排除注释与字符串字面量
//      （bash 只排注释行，字符串中的 TransactionScope/UtcNow 属误报面）；
//      G17 附注（TimeProvider.System 内联）在 src 零残留后由 WARN 升格为 FAIL 断言。
// 判定为纯语法层（ParseText，无语义模型/编译引用），保持测试轻量。
// ═══════════════════════════════════════════════════════════════

public sealed class SourceCodeGuardTests
{
    // ─────────────────────────────────────────────────────────────
    // 数据结构
    // ─────────────────────────────────────────────────────────────

    /// <summary>一处违规（文件相对路径 + 1 起始行号 + 规则名 + 描述）。</summary>
    private sealed record Violation(string File, int Line, string Rule, string Detail);

    /// <summary>矩阵样本：源码 + 期望违规总数 + 伪文件路径（守卫 3 的 PalORM 白名单按路径判定）。</summary>
    private sealed record GuardCase(string Name, string Source, int Expected, string FilePath);

    private static GuardCase Case(
        string name, string source, int expected,
        string filePath = "src/PalDDD.Core/Probe.cs") => new(name, source, expected, filePath);

    // ─────────────────────────────────────────────────────────────
    // src/ 全量源码加载（所有测试共享，懒加载一次）
    // ─────────────────────────────────────────────────────────────

    private sealed record SourceFile(string RelativePath, string FullPath, SyntaxTree Tree);

    private static readonly Lazy<IReadOnlyList<SourceFile>> Sources = new(LoadAllSources);

    // CA1859：返回具体 List（仅内部构建用，对 Lazy<IReadOnlyList<>> 消费方无影响）
    private static List<SourceFile> LoadAllSources()
    {
        var root = FindRepoRoot();
        var srcDir = Path.Combine(root, "src");
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var files = new List<SourceFile>();
        foreach (var full in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                                      .Where(NotUnderArtifactDir)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(full), options, path: full);
            files.Add(new(Path.GetRelativePath(root, full).Replace('\\', '/'), full, tree));
        }
        if (files.Count == 0)
            throw new InvalidOperationException($"未在 {srcDir} 下发现任何 .cs 文件——守卫扫描面为空，疑似仓库根定位错误。");
        return files;
    }
    /// <summary>向上查找仓库根（以 PalDDD.slnx + src/ 目录为特征）。</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx"))
                && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"自 {AppContext.BaseDirectory} 向上未找到含 PalDDD.slnx + src/ 的仓库根。");
    }

    private static bool NotUnderArtifactDir(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (segment is "obj" or "bin")
                return false;
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫判定器（纯语法层，矩阵测试与全仓测试共用）
    // ─────────────────────────────────────────────────────────────

    private static class GuardAnalyzer
    {
        // ── 通用：AOT 豁免（沿祖先声明链查真实 attribute） ──

        /// <summary>
        /// 沿祖先链（方法/构造器/属性/访问器/字段/类型等声明）查
        /// [RequiresDynamicCode] / [RequiresUnreferencedCode]（含全限定名与 Attribute 后缀变体）。
        /// </summary>
        private static bool HasAotExemption(SyntaxNode node)
        {
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is MemberDeclarationSyntax { AttributeLists.Count: > 0 } member
                    && member.AttributeLists.SelectMany(l => l.Attributes).Any(IsAotExemptionAttribute))
                    return true;
                if (ancestor is AccessorDeclarationSyntax { AttributeLists.Count: > 0 } accessor
                    && accessor.AttributeLists.SelectMany(l => l.Attributes).Any(IsAotExemptionAttribute))
                    return true;
            }
            return false;
        }

        private static bool IsAotExemptionAttribute(AttributeSyntax attribute) =>
            SimpleName(attribute) is "RequiresDynamicCode" or "RequiresUnreferencedCode";

        /// <summary>取 attribute 简名（限定名取最右标识符，剥 Attribute 后缀）。</summary>
        private static string SimpleName(AttributeSyntax attribute) => attribute.Name switch
        {
            IdentifierNameSyntax id => TrimAttributeSuffix(id.Identifier.ValueText),
            QualifiedNameSyntax q => TrimAttributeSuffix(q.Right.Identifier.ValueText),
            AliasQualifiedNameSyntax a => TrimAttributeSuffix(a.Name.Identifier.ValueText),
            _ => string.Empty,
        };

        private static string TrimAttributeSuffix(string name) =>
            name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

        // ── 通用：成员访问接收者简名（x.Y → "x"；System.Activator → "Activator"） ──

        private static string? ReceiverSimpleName(MemberAccessExpressionSyntax access) => access.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => null,
        };

        private static Violation Make(
            SourceFileOrPath file, SyntaxNode node, string rule, string detail) =>
            new(file.Relative, file.Tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, rule, detail);

        /// <summary>统一文件载体：全仓扫描用 SourceFile，矩阵用伪路径 + 独立树。</summary>
        internal sealed record SourceFileOrPath(string Relative, SyntaxTree Tree)
        {
            internal static SourceFileOrPath From(SourceFile f) => new(f.RelativePath, f.Tree);
            internal static SourceFileOrPath Synthetic(string relativePath, SyntaxTree tree) => new(relativePath, tree);
        }

        // ── 守卫 1（G7+V1）：运行时零反射 API ──
        // MakeGenericType（任意接收者）/ Activator.CreateInstance / Assembly.GetTypes /
        // Type.GetType ——命中即需沿祖先链带 AOT 豁免 attribute。

        internal static IEnumerable<Violation> FindReflectionViolations(SourceFileOrPath file)
        {
            foreach (var invocation in file.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
                var name = access.Name.Identifier.ValueText;
                var isTarget = name switch
                {
                    "MakeGenericType" => true,                       // 任意接收者（对齐 G7 文本模式）
                    "CreateInstance" => ReceiverSimpleName(access) == "Activator",
                    "GetTypes" => ReceiverSimpleName(access) == "Assembly",
                    "GetType" => ReceiverSimpleName(access) == "Type",
                    _ => false,
                };
                if (isTarget && !HasAotExemption(invocation))
                    yield return Make(file, invocation, "G7",
                        $"{name} 反射调用缺 [RequiresDynamicCode]/[RequiresUnreferencedCode] 豁免标注");
            }
        }

        // ── 守卫 2（G8）：运行时零 Expression.Compile / dynamic ──

        internal static IEnumerable<Violation> FindCompileAndDynamicViolations(SourceFileOrPath file)
        {
            var root = file.Tree.GetRoot();

            // .Compile()：成员访问 + 空参数列表（对齐 G8 的 \.Compile\(\) 字面模式）
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Compile" })
                    continue;
                if (invocation.ArgumentList.Arguments.Count != 0) continue;
                if (!HasAotExemption(invocation))
                    yield return Make(file, invocation, "G8",
                        "Expression .Compile() 调用缺 AOT 豁免标注（NativeAOT 不支持表达式树编译）");
            }

            // dynamic 类型引用：语法树天然排除注释/字符串中的 "dynamic" 字样；
            // 成员访问位置（x.dynamic / x?.dynamic()）的 dynamic 是标识符非类型，排除。
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                                           .Where(i => i.Identifier.ValueText == "dynamic"))
            {
                if (identifier.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, identifier))
                    continue;
                if (identifier.Parent is MemberBindingExpressionSyntax) continue;
                if (!HasAotExemption(identifier))
                    yield return Make(file, identifier, "G8", "dynamic 类型引用（AOT/裁剪红线）");
            }
        }

        // ── 守卫 3（G11+V2/V3/V4）：同步阻塞异步 + async void ──

        internal static IEnumerable<Violation> FindSyncBlockingViolations(SourceFileOrPath file)
        {
            var root = file.Tree.GetRoot();

            // .Result：仅 IsCompletedSuccessfully 条件 if 的 then 分支豁免（节点级，等价 G11 的
            // "前 3 行含 IsCompletedSuccessfully" 语义意图但更严——else 分支不再被误豁免）
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                       .Where(a => a.Name.Identifier.ValueText == "Result"))
            {
                if (!IsInsideCompletedSuccessfullyBranch(access))
                    yield return Make(file, access, "G11",
                        ".Result 同步阻塞——仅 IsCompletedSuccessfully 快速路径豁免");
            }

            // .Wait()（空参）/ GetAwaiter().GetResult()：仅 PalORM 适配层白名单豁免
            // （IPalOutboxStore 接口强制同步签名，见 gate-check.sh G11 注释；含全部 PalORM 方言包）
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var isWait = invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Wait",
                } access && invocation.ArgumentList.Arguments.Count == 0;

                var isGetResult = invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "GetResult",
                    Expression: InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAwaiter" },
                }
                };

                if (!(isWait || isGetResult)) continue;
                if (IsPalOrmAdapterPath(file.Relative))
                    continue; // PalORM 适配层：接口同步签名约束下的合法 sync-over-async
                yield return Make(file, invocation, "G11",
                    $"{(isWait ? ".Wait()" : "GetAwaiter().GetResult()")} 同步阻塞异步（PalORM 适配层外禁止）");
            }
        }

        /// <summary>.Result 节点是否位于某个 if 的 then 分支内，且该 if 条件引用 IsCompletedSuccessfully。</summary>
        private static bool IsInsideCompletedSuccessfullyBranch(SyntaxNode node)
        {
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is not IfStatementSyntax ifStatement) continue;
                if (ifStatement.Statement.Span.Contains(node.Span)
                    && ifStatement.Condition.DescendantNodesAndSelf()
                                          .OfType<IdentifierNameSyntax>()
                                          .Any(i => i.Identifier.ValueText == "IsCompletedSuccessfully"))
                    return true;
                // else / else-if 分支或条件不含标识符 → 继续向外层 if 查（嵌套 if 场景）
            }
            return false;
        }

        /// <summary>PalORM 适配层路径白名单：路径任一段为 PalDDD.PalORM 或以 PalDDD.PalORM. 开头。</summary>
        private static bool IsPalOrmAdapterPath(string relativePath)
        {
            foreach (var segment in relativePath.Split('/'))
                if (segment == "PalDDD.PalORM" || segment.StartsWith("PalDDD.PalORM.", StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>async void 声明（方法/局部函数/显式 void 返回类型的 lambda——
        /// 对齐 G9/V2 文本模式 async+void 相邻的全部合法 C# 书写位置；
        /// simple lambda 参数不可为 void，无显式返回类型的 async lambda 推断 Task，均天然不在此列）。</summary>
        internal static IEnumerable<Violation> FindAsyncVoidViolations(SourceFileOrPath file)
        {
            var root = file.Tree.GetRoot();
            foreach (var node in root.DescendantNodes()
                                     .Where(n => n is MethodDeclarationSyntax
                                                 or LocalFunctionStatementSyntax
                                                 or ParenthesizedLambdaExpressionSyntax))
            {
                var returnType = node switch
                {
                    MethodDeclarationSyntax m => m.ReturnType,
                    LocalFunctionStatementSyntax l => l.ReturnType,
                    ParenthesizedLambdaExpressionSyntax p => p.ReturnType,
                    _ => null,
                };
                var modifiers = node switch
                {
                    MethodDeclarationSyntax m => m.Modifiers,
                    LocalFunctionStatementSyntax l => l.Modifiers,
                    ParenthesizedLambdaExpressionSyntax p => p.Modifiers,
                    _ => default,
                };
                if (!modifiers.Any(SyntaxKind.AsyncKeyword)) continue;
                if (returnType is not PredefinedTypeSyntax { Keyword.ValueText: "void" }) continue;
                yield return Make(file, node, "V2", "async void 声明（异常逃逸无法观察，改 async Task）");
            }
        }

        // ── 守卫 4（G12）：await 必带 ConfigureAwait ──
        // 按节点级判定：每个 AwaitExpression 的 awaited 表达式须以 .ConfigureAwait(...) 结尾。
        // 语法豁免形态（对齐 G12 例外清单）：await Task.Yield() / await Task.CompletedTask；
        // await using / await foreach 语法上不是 AwaitExpression，天然不适用本守卫。

        internal static IEnumerable<Violation> FindMissingConfigureAwaitViolations(SourceFileOrPath file)
        {
            foreach (var awaitExpression in file.Tree.GetRoot()
                                                .DescendantNodes()
                                                .OfType<AwaitExpressionSyntax>())
            {
                var awaited = awaitExpression.Expression;
                if (EndsWithConfigureAwait(awaited)) continue;
                if (IsTaskYieldOrCompletedTask(awaited)) continue;
                yield return Make(file, awaitExpression, "G12",
                    "await 表达式缺 .ConfigureAwait(false)（库代码不绑定 SynchronizationContext）");
            }
        }

        private static bool EndsWithConfigureAwait(ExpressionSyntax awaited) =>
            awaited is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" },
            };

        private static bool IsTaskYieldOrCompletedTask(ExpressionSyntax awaited) => awaited switch
        {
            // await Task.Yield()
            InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Yield",
                    Expression: var receiver,
                },
            } => ReceiverIsTask(receiver),
            // await Task.CompletedTask
            MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "CompletedTask",
                Expression: var receiver,
            } => ReceiverIsTask(receiver),
            _ => false,
        };

        private static bool ReceiverIsTask(ExpressionSyntax receiver) => receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "Task",
            QualifiedNameSyntax q => q.Right.Identifier.ValueText == "Task",
            _ => false,
        };

        /// <summary>守卫 4 的扫描排除路径：G12 原 find 排除 *SourceGen*（源生成器项目运行时模板）。</summary>
        public static bool IsExcludedFromGuard4(string relativePath) =>
            relativePath.Contains("SourceGen", StringComparison.Ordinal);

        // ── 守卫 5（G1）：具体异常类型必须 sealed ──
        // public class/record 且类型名以 Exception 结尾，必须带 sealed 或 abstract 修饰符。
        // 豁免集对齐 G1 文本口径：类型名或文件路径含 Middleware/Extensions/CodeFix
        //（bash 行级 grep -v 的输出行 = 路径前缀 + 声明行文本，两者任一命中即豁免）。

        internal static IEnumerable<Violation> FindUnsealedExceptionViolations(SourceFileOrPath file)
        {
            foreach (var type in file.Tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type is not (ClassDeclarationSyntax or RecordDeclarationSyntax)) continue;
                if (!type.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
                if (!type.Identifier.ValueText.EndsWith("Exception", StringComparison.Ordinal)) continue;
                if (type.Modifiers.Any(SyntaxKind.SealedKeyword)) continue;
                if (type.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;
                if (IsExceptionDeclarationExempt(type.Identifier.ValueText, file.Relative)) continue;
                yield return Make(file, type, "G1",
                    $"{type.Identifier.ValueText} 具体异常类型未 sealed（Middleware/Extensions/CodeFix 豁免集外）");
            }
        }

        /// <summary>G1 豁免集：类型名或文件相对路径含 Middleware/Extensions/CodeFix 任一。</summary>
        private static bool IsExceptionDeclarationExempt(string typeName, string relativePath) =>
            typeName.Contains("Middleware", StringComparison.Ordinal)
            || typeName.Contains("Extensions", StringComparison.Ordinal)
            || typeName.Contains("CodeFix", StringComparison.Ordinal)
            || relativePath.Contains("Middleware", StringComparison.Ordinal)
            || relativePath.Contains("Extensions", StringComparison.Ordinal)
            || relativePath.Contains("CodeFix", StringComparison.Ordinal);

        // ── 守卫 6（G10+G17）：TransactionScope 禁令 + 时钟直调禁令 ──
        // G10：任何标识符含 TransactionScope 子串（类型引用/对象创建/家族枚举
        //  TransactionScopeOption/TransactionScopeAsyncFlowOption）——对齐 bash 子串口径，
        //  语法树天然排除注释与字符串字面量。
        // G17：DateTime/DateTimeOffset.UtcNow 成员访问直调（必须注入 TimeProvider）；
        //  附注 G17b：TimeProvider.System.GetUtcNow/GetTimestamp 内联调用（时钟双轨债务，
        //  src 零残留后由 bash WARN 升格为 FAIL 断言；virtual 时钟默认实现豁免——
        //  bash 以行级排除 "virtual DateTimeOffset GetUtcNow" 实现同一豁免：虚方法是
        //  可测试时钟设计契约（生产默认系统时钟，测试子类覆写注入 FakeTimeProvider，
        //  见 OutboxDbContext.GetUtcNow 注释），非时钟双轨债务）。

        internal static IEnumerable<Violation> FindTransactionScopeAndClockViolations(SourceFileOrPath file)
        {
            var root = file.Tree.GetRoot();

            // G10：TransactionScope 标识符（含限定名 System.Transactions.TransactionScope 的最右段）
            foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                                           .Where(i => i.Identifier.ValueText.Contains("TransactionScope", StringComparison.Ordinal)))
            {
                yield return Make(file, identifier, "G10",
                    $"{identifier.Identifier.ValueText} 引用 TransactionScope（用 DbContext 事务替代）");
            }

            // G17 主判定：UtcNow 直调（限定名 System.DateTime.UtcNow 与非限定 DateTime.UtcNow 均命中）
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                       .Where(a => a.Name.Identifier.ValueText == "UtcNow"
                                                && ReceiverRightmostSimpleName(a) is "DateTime" or "DateTimeOffset"))
            {
                yield return Make(file, access, "G17",
                    $"{ReceiverRightmostSimpleName(access)}.UtcNow 硬编码时钟（必须注入 TimeProvider）");
            }

            // G17 附注（G17b）：TimeProvider.System.GetUtcNow/GetTimestamp 内联——
            // 三段链 TimeProvider.System.Xxx 精确匹配（裸/限定 TimeProvider 均覆盖），
            // 不会命中注入实例的 GetUtcNow 调用；virtual 时钟默认实现内的调用豁免
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                       .Where(IsInlineSystemClockAccess))
            {
                yield return Make(file, access, "G17",
                    "TimeProvider.System 内联调用（时钟双轨，必须经构造注入的 TimeProvider；virtual 时钟默认实现豁免）");
            }
        }

        /// <summary>成员访问接收者的最右简名——表达式位置的限定名 System.DateTimeOffset
        /// 是 MemberAccess 而非类型位置的 QualifiedName，两形态均取最右段（"DateTimeOffset"）。</summary>
        private static string? ReceiverRightmostSimpleName(MemberAccessExpressionSyntax access) => access.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            _ => null,
        };

        /// <summary>是否为 TimeProvider.System.GetUtcNow/GetTimestamp 内联调用
        ///（virtual 时钟默认实现内的调用豁免——可测试时钟设计契约，非双轨债务）。</summary>
        private static bool IsInlineSystemClockAccess(MemberAccessExpressionSyntax access) =>
            access.Name.Identifier.ValueText is "GetUtcNow" or "GetTimestamp"
            && access.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "System" } systemAccess
            && systemAccess.Expression switch
            {
                IdentifierNameSyntax { Identifier.ValueText: "TimeProvider" } => true,
                // 限定形态 System.TimeProvider.System.GetUtcNow（接收者仍是 ...TimeProvider 段）
                MemberAccessExpressionSyntax { Name.Identifier.ValueText: "TimeProvider" } => true,
                QualifiedNameSyntax { Right.Identifier.ValueText: "TimeProvider" } => true,
                _ => false,
            }
            && !IsInsideVirtualClockDefaultImplementation(access);

        /// <summary>调用是否位于 virtual GetUtcNow/GetTimestamp 方法内（对齐 bash 行级排除
        /// "virtual DateTimeOffset GetUtcNow" 的语义意图——见守卫 6 头注释）。</summary>
        private static bool IsInsideVirtualClockDefaultImplementation(SyntaxNode node)
        {
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is MethodDeclarationSyntax { Identifier.ValueText: "GetUtcNow" or "GetTimestamp" } method
                    && method.Modifiers.Any(SyntaxKind.VirtualKeyword))
                    return true;
            }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 矩阵样本（红/绿固化——含 ITM-073 回归样本，永久保留不依赖临时文件）
    // ─────────────────────────────────────────────────────────────

    private static readonly GuardCase[] ReflectionCases =
    [
        Case("R1 无豁免 MakeGenericType 必须红",
            """
            using System;
            using System.Collections.Generic;
            class C { object M(Type t) => typeof(List<>).MakeGenericType(t); }
            """, 1),
        Case("R2 方法级 [RequiresDynamicCode] 豁免",
            """
            using System;
            using System.Collections.Generic;
            class C
            {
                [RequiresDynamicCode("_probe")]
                object M(Type t) => typeof(List<>).MakeGenericType(t);
            }
            """, 0),
        Case("R3 方法级全限定 [RequiresUnreferencedCode] 豁免",
            """
            using System;
            using System.Collections.Generic;
            class C
            {
                [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("_probe")]
                object M(Type t) => typeof(List<>).MakeGenericType(t);
            }
            """, 0),
        Case("R4 类型级 [RequiresDynamicCode] 豁免",
            """
            using System;
            using System.Collections.Generic;
            [RequiresDynamicCode("_probe")]
            class C { object M(Type t) => typeof(List<>).MakeGenericType(t); }
            """, 0),
        // ITM-073 回归样本：SuppressMessage 的 message 文案含 "RequiresDynamicCode" 字样，
        // bash 旧版按"文件含该字符串"误豁免——必须按真实 attribute 判定，本样本必须红
        Case("R5 SuppressMessage 文案含 RequiresDynamicCode 字样不豁免（ITM-073）",
            """
            using System;
            using System.Collections.Generic;
            class C
            {
                [System.Diagnostics.CodeAnalysis.SuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCodeAttribute may require dynamic code",
                    Justification = "已声明 RequiresDynamicCode 边界")]
                object M(Type t) => typeof(List<>).MakeGenericType(t);
            }
            """, 1),
        Case("R6 无豁免 Activator.CreateInstance",
            """
            using System;
            class C { object M() => Activator.CreateInstance(typeof(object)); }
            """, 1),
        Case("R7 无豁免 Assembly.GetTypes",
            """
            using System;
            class C { Type[] M() => Assembly.GetTypes(); }
            """, 1),
        Case("R8 无豁免 Type.GetType(string)",
            """
            using System;
            class C { Type? M(string name) => Type.GetType(name); }
            """, 1),
        Case("R9 方法级豁免的 Type.GetType",
            """
            using System;
            class C
            {
                [RequiresUnreferencedCode("_probe")]
                Type? M(string name) => Type.GetType(name);
            }
            """, 0),
    ];

    private static readonly GuardCase[] CompileAndDynamicCases =
    [
        Case("C1 无豁免 .Compile() 必须红",
            """
            using System;
            using System.Linq.Expressions;
            class C { Func<int> M(Expression<Func<int>> e) => e.Compile(); }
            """, 1),
        Case("C2 方法级 [RequiresDynamicCode] 内 .Compile() 豁免",
            """
            using System;
            using System.Linq.Expressions;
            class C
            {
                [RequiresDynamicCode("_probe")]
                Func<int> M(Expression<Func<int>> e) => e.Compile();
            }
            """, 0),
        // ITM-073 同族样本：UnconditionalSuppressMessage 文案含 RequiresDynamicCode 字样，
        // 不构成豁免（真实 src 中 ISpecification.IsSatisfiedBy 即此形态，靠私有 helper 真实标注豁免）
        Case("C6 SuppressMessage 文案含 RequiresDynamicCode + .Compile() 必须红（ITM-073）",
            """
            using System;
            using System.Linq.Expressions;
            class C
            {
                [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:Members annotated with RequiresDynamicCodeAttribute may require dynamic code",
                    Justification = "RequiresDynamicCode 边界内")]
                Func<int> M(Expression<Func<int>> e) => e.Compile();
            }
            """, 1),
        Case("C3 无豁免 dynamic 声明必须红",
            """
            class C { object M() { dynamic x = 1; return x; } }
            """, 1),
        Case("C4 方法级 [RequiresDynamicCode] 内 dynamic 豁免",
            """
            class C
            {
                [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("_probe")]
                object M() { dynamic x = 1; return x; }
            }
            """, 0),
        Case("C5 字符串与注释中的 dynamic 字样不计",
            """
            class C
            {
                string M() => "dynamic code / dynamic access";
                // dynamic code 注释同样不计
            }
            """, 0),
        Case("C7 成员名 dynamic（x.dynamic()）非类型引用不计",
            """
            class C
            {
                int M(DynamicHolder h) => h.dynamic();
                int N(DynamicHolder h) => h.dynamic;
            }
            class DynamicHolder { public int dynamic => 0; }
            """, 0),
    ];

    private static readonly GuardCase[] SyncBlockingCases =
    [
        Case("W1 裸 .Result 必须红",
            """
            using System.Threading.Tasks;
            class C { int M(Task<int> t) => t.Result; }
            """, 1),
        Case("W2 IsCompletedSuccessfully if 块分支内 .Result 豁免",
            """
            using System.Threading.Tasks;
            class C
            {
                int M(Task<int> t)
                {
                    if (t.IsCompletedSuccessfully) { return t.Result; }
                    return 0;
                }
            }
            """, 0),
        Case("W2b IsCompletedSuccessfully if 单语句分支内 .Result 豁免",
            """
            using System.Threading.Tasks;
            class C
            {
                int M(Task<int> t)
                {
                    if (t.IsCompletedSuccessfully) return t.Result;
                    return 0;
                }
            }
            """, 0),
        Case("W3 else 分支 .Result 仍红（节点级严于 3 行文本窗口）",
            """
            using System.Threading.Tasks;
            class C
            {
                int M(Task<int> t)
                {
                    if (t.IsCompletedSuccessfully) return 0;
                    else return t.Result;
                }
            }
            """, 1),
        Case("W4 非 PalORM 路径 .Wait() 必须红",
            """
            using System.Threading.Tasks;
            class C { void M(Task t) => t.Wait(); }
            """, 1),
        Case("W5 PalORM 适配层路径 .Wait() 豁免",
            """
            using System.Threading.Tasks;
            class C { void M(Task t) => t.Wait(); }
            """, 0, "src/PalDDD.PalORM/Stores/Probe.cs"),
        Case("W5b PalORM 方言包路径 GetAwaiter().GetResult() 豁免",
            """
            using System.Threading.Tasks;
            class C { void M(Task t) => t.GetAwaiter().GetResult(); }
            """, 0, "src/PalDDD.PalORM.MySql/Probe.cs"),
        Case("W6 非 PalORM 路径 GetAwaiter().GetResult() 必须红",
            """
            using System.Threading.Tasks;
            class C { void M(Task t) => t.GetAwaiter().GetResult(); }
            """, 1),
        Case("W7 带参 .Wait(1000) 不命中（对齐 G11 空参口径）",
            """
            using System.Threading.Tasks;
            class C { void M(Task t) => t.Wait(1000); }
            """, 0),
        Case("W8 async void 方法必须红",
            """
            using System.Threading.Tasks;
            class C { async void M() { await Task.CompletedTask; } }
            """, 1),
        Case("W9 async Task 方法不命中",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await Task.CompletedTask; } }
            """, 0),
        // G9 下沉补强样本：显式 void 返回类型的 async lambda（事件处理器常见形态）——
        // bash 文本 async\s+void 命中，下沉前守卫 3 只查方法/局部函数对此形态漏检
        Case("W10 async void lambda（显式 void 返回类型）必须红",
            """
            using System;
            using System.Threading.Tasks;
            class C { Action M() => async void () => { await Task.CompletedTask; }; }
            """, 1),
        Case("W11 无显式返回类型的 async lambda 推断 Task 不命中",
            """
            using System;
            using System.Threading.Tasks;
            class C { Func<Task> M() => async () => { await Task.CompletedTask; }; }
            """, 0),
    ];

    private static readonly GuardCase[] ExceptionSealingCases =
    [
        Case("E1 public class 非 sealed 异常必须红",
            """
            public class FooException : System.Exception { }
            """, 1),
        Case("E2 public sealed class 异常合规",
            """
            public sealed class FooException : System.Exception { }
            """, 0),
        Case("E3 public abstract 异常基类豁免",
            """
            public abstract class FooException : System.Exception { }
            """, 0),
        // bash G1 正则 public\s+(class|record class) 只匹配 "record class" 显式形态，
        // "public record XxxException" 被漏检——语法节点级判定两种写法均命中（有意收紧）
        Case("E4 public record（无 class 关键字）非 sealed 必须红（bash 漏检形态）",
            """
            public record FooException(string Code) : System.Exception(Code);
            """, 1),
        Case("E5 sealed record 异常合规",
            """
            public sealed record FooException(string Code) : System.Exception(Code);
            """, 0),
        Case("E6 类型名含 Middleware 豁免",
            """
            public class TracingMiddlewareException : System.Exception { }
            """, 0),
        Case("E7 文件路径含 Middleware 豁免（bash 行输出含路径前缀的等价口径）",
            """
            public class FooException : System.Exception { }
            """, 0, "src/PalDDD.Hosting.AspNetCore/Middleware/FooException.cs"),
        Case("E7b 文件路径含 CodeFix 豁免",
            """
            public class FooException : System.Exception { }
            """, 0, "src/PalDDD.Analyzers.CodeFixes/Probe.cs"),
        Case("E8 internal 异常不在扫描面（G1 仅 public）",
            """
            internal class FooException : System.Exception { }
            """, 0),
        Case("E9 非 Exception 后缀的未密封 public class 不命中",
            """
            public class Foo { }
            """, 0),
    ];

    private static readonly GuardCase[] TransactionScopeAndClockCases =
    [
        Case("T1 new TransactionScope() 必须红",
            """
            class C { object M() => new System.Transactions.TransactionScope(); }
            """, 1),
        // bash G10 为子串匹配 'TransactionScope'，家族枚举 TransactionScopeOption 同样命中
        Case("T2 TransactionScope 家族枚举（TransactionScopeOption）也红（对齐 bash 子串口径）",
            """
            class C { int M() => (int)System.Transactions.TransactionScopeOption.Required; }
            """, 1),
        Case("T3 注释与字符串中的 TransactionScope 不计",
            """
            class C
            {
                // TransactionScope 已被 DbContext 事务替代
                string M() => "TransactionScope forbidden";
            }
            """, 0),
        Case("U1 DateTimeOffset.UtcNow 直调必须红",
            """
            class C { System.DateTimeOffset M() => System.DateTimeOffset.UtcNow; }
            """, 1),
        Case("U2 DateTime.UtcNow 直调必须红",
            """
            class C { System.DateTime M() => System.DateTime.UtcNow; }
            """, 1),
        Case("U3 非限定 DateTime.UtcNow 同样命中",
            """
            class C { System.DateTime M() => DateTime.UtcNow; }
            """, 1),
        Case("U4 注入 TimeProvider 的 GetUtcNow 合规",
            """
            class C
            {
                private readonly System.TimeProvider _clock;
                public C(System.TimeProvider clock) => _clock = clock;
                public System.DateTimeOffset M() => _clock.GetUtcNow();
            }
            """, 0),
        // G17b：bash 为 WARN 级债务暴露，src 零残留后升格为 FAIL 断言（gate-check G17 注释既定升级路径）
        Case("U5 TimeProvider.System.GetUtcNow 内联必须红（G17b 零残留后升格）",
            """
            class C { System.DateTimeOffset M() => System.TimeProvider.System.GetUtcNow(); }
            """, 1),
        Case("U6 GetUtcNow 重写声明不命中（bash 排除 virtual 声明行的语法级等价）",
            """
            abstract class C : System.TimeProvider
            {
                public override System.DateTimeOffset GetUtcNow() => default;
            }
            """, 0),
        // 真实 src 形态固化（OutboxDbContext.cs:320 / SagaStateDbContext.cs:43）：virtual 时钟
        // 默认实现是可测试时钟设计契约（测试子类覆写注入 FakeTimeProvider），bash G17b 以
        // 行级排除 "virtual DateTimeOffset GetUtcNow" 豁免——语法级等价豁免必须放行此形态
        Case("U7 virtual GetUtcNow 默认实现的 TimeProvider.System 内联豁免（可测试时钟契约）",
            """
            abstract class C
            {
                protected virtual System.DateTimeOffset GetUtcNow() => System.TimeProvider.System.GetUtcNow();
            }
            """, 0),
        Case("U8 非 virtual 方法内的 TimeProvider.System.GetTimestamp 仍红",
            """
            class C { long M() => System.TimeProvider.System.GetTimestamp(); }
            """, 1),
    ];

    private static readonly GuardCase[] ConfigureAwaitCases =
    [
        Case("A1 裸 await 必须红",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await Task.Delay(1); } }
            """, 1),
        Case("A2 await + ConfigureAwait(false) 合规",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await Task.Delay(1).ConfigureAwait(false); } }
            """, 0),
        Case("A3 await using 非 AwaitExpression 不命中",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await using var x = GetResource(); } }
            """, 0),
        Case("A4 await foreach 非 AwaitExpression 不命中",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await foreach (var item in GetItems()) { } } }
            """, 0),
        Case("A5 await Task.Yield() 豁免",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await Task.Yield(); } }
            """, 0),
        Case("A6 await Task.CompletedTask 豁免",
            """
            using System.Threading.Tasks;
            class C { async Task M() { await Task.CompletedTask; } }
            """, 0),
        Case("A7 嵌套 await 内层裸仍红",
            """
            using System.Threading.Tasks;
            class C { async Task<int> M() => await (await Inner()).ConfigureAwait(false); }
            """, 1),
    ];

    // ─────────────────────────────────────────────────────────────
    // 测试 0：解析完整性自检（防语法解析失败导致守卫假绿）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 全部 src 源文件须无语法错误级诊断——若存在 Roslyn 解析器不认识的新语法，
    /// 语法树会缺节点，四守卫在该文件上静默漏检（门禁假绿）。此处先钉死解析完整性。
    /// </summary>
    [Test]
    public async Task Sources_AllFiles_ParseWithoutSyntaxErrors()
    {
        var broken = new List<string>();
        foreach (var file in Sources.Value)
        {
            foreach (var diagnostic in file.Tree.GetDiagnostics())
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    broken.Add($"{file.RelativePath}:{diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1} {diagnostic.Id} {diagnostic.GetMessage()}");
        }
        await Assert.That(broken).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 1（G7+V1）：反射 API
    // ─────────────────────────────────────────────────────────────

    /// <summary>全仓扫描：src 下反射 API 调用必须全部带真实 AOT 豁免标注（conventions §1.4 零反射红线）。</summary>
    [Test]
    public async Task Guard1_ReflectionCalls_AllHaveAotExemptions()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindReflectionViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>红/绿矩阵：方法级/类型级豁免生效，SuppressMessage 文案冒充豁免必须红（ITM-073）。</summary>
    [Test]
    public async Task Guard1_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in ReflectionCases)
        {
            var actual = GuardAnalyzer.FindReflectionViolations(
                GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source))).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 2（G8）：Expression.Compile / dynamic
    // ─────────────────────────────────────────────────────────────

    /// <summary>全仓扫描：src 下 .Compile() 与 dynamic 引用必须全部带 AOT 豁免标注。</summary>
    [Test]
    public async Task Guard2_CompileAndDynamic_AllHaveAotExemptions()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindCompileAndDynamicViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Guard2_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in CompileAndDynamicCases)
        {
            var actual = GuardAnalyzer.FindCompileAndDynamicViolations(
                GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source))).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 3（G11+V2/V3/V4）：同步阻塞 + async void
    // ─────────────────────────────────────────────────────────────

    /// <summary>全仓扫描：.Result 仅允许出现在 IsCompletedSuccessfully 快速路径分支内。</summary>
    [Test]
    public async Task Guard3_TaskResult_OnlyInsideCompletedBranch()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindSyncBlockingViolations(GuardAnalyzer.SourceFileOrPath.From(f))
                                     .Where(v => v.Detail.Contains(".Result", StringComparison.Ordinal)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>全仓扫描：.Wait()/GetAwaiter().GetResult() 仅允许 PalORM 适配层（接口同步签名约束）。</summary>
    [Test]
    public async Task Guard3_BlockingWaits_AbsentOutsidePalOrmAdapters()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindSyncBlockingViolations(GuardAnalyzer.SourceFileOrPath.From(f))
                                     .Where(v => !v.Detail.Contains(".Result", StringComparison.Ordinal)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>全仓扫描：async void 声明为零（conventions §1.5）。</summary>
    [Test]
    public async Task Guard3_AsyncVoidDeclarations_AbsentInSources()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindAsyncVoidViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Guard3_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in SyncBlockingCases)
        {
            var file = GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source));
            var actual = GuardAnalyzer.FindSyncBlockingViolations(file).Count()
                        + GuardAnalyzer.FindAsyncVoidViolations(file).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 4（G12）：await 必带 ConfigureAwait
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 守卫 4 已知基线偏差账本（file:line）。节点级判定比 bash 文件级差值计数更强，
    /// 暴露出被同文件多余 ConfigureAwait 名额抵消的存量裸 await——bash G12 的已知盲区。
    /// 断言采用"违规集 == 账本"模式：新增违规红（守卫有效）、账本项被修复后红（提示销账），
    /// 保证账本是活账本。修复存量后请同步移除对应登记。
    /// </summary>
    private static readonly string[] Guard4KnownDeviations =
    [
        // （已清空）唯一登记项 EventLogDbContext.cs:317 的裸 await 已于 MIG-008 收口修复
        // （MaxAsync(...).ConfigureAwait(false) ?? -1）——活账本模式按设计红提示销账后移除。
        // 保留空数组与"违规集 == 账本"断言：新增违规即红，账本机制持续生效。
    ];

    /// <summary>
    /// 全仓扫描：每个 await 表达式（节点级，强于 bash 文件级差值计数）须带 ConfigureAwait；
    /// 排除路径含 SourceGen 的文件（对齐 G12 原 find 排除）。存量违规须登记在偏差账本内。
    /// </summary>
    [Test]
    public async Task Guard4_AwaitExpressions_UseConfigureAwait()
    {
        var violations = Sources.Value
            .Where(f => !GuardAnalyzer.IsExcludedFromGuard4(f.RelativePath))
            .Select(f => GuardAnalyzer.FindMissingConfigureAwaitViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line}")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        var expected = Guard4KnownDeviations.OrderBy(v => v, StringComparer.Ordinal).ToList();
        await Assert.That(violations).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Guard4_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in ConfigureAwaitCases)
        {
            var actual = GuardAnalyzer.FindMissingConfigureAwaitViolations(
                GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source))).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 5（G1）：具体异常类型 sealed
    // ─────────────────────────────────────────────────────────────

    /// <summary>全仓扫描：public 具体异常类型必须 sealed/abstract（conventions §3.2，Middleware/Extensions/CodeFix 豁免）。</summary>
    [Test]
    public async Task Guard5_ExceptionTypes_SealedOrAbstractOrExempt()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindUnsealedExceptionViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Guard5_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in ExceptionSealingCases)
        {
            var actual = GuardAnalyzer.FindUnsealedExceptionViolations(
                GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source))).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }

    // ─────────────────────────────────────────────────────────────
    // 守卫 6（G10+G17）：TransactionScope 禁令 + 时钟直调禁令
    // ─────────────────────────────────────────────────────────────

    /// <summary>全仓扫描：src 下零 TransactionScope 引用、零 UtcNow 直调、零 TimeProvider.System 内联
    ///（conventions §10.4——时钟必须经构造注入的 TimeProvider）。</summary>
    [Test]
    public async Task Guard6_TransactionScopeAndHardcodedClock_AbsentInSources()
    {
        var violations = Sources.Value
            .Select(f => GuardAnalyzer.FindTransactionScopeAndClockViolations(GuardAnalyzer.SourceFileOrPath.From(f)))
            .SelectMany(v => v)
            .Select(v => $"{v.File}:{v.Line} [{v.Rule}] {v.Detail}")
            .ToList();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task Guard6_ExemptionMatrix_MatchesExpectedCounts()
    {
        var failures = new List<string>();
        foreach (var testCase in TransactionScopeAndClockCases)
        {
            var actual = GuardAnalyzer.FindTransactionScopeAndClockViolations(
                GuardAnalyzer.SourceFileOrPath.Synthetic(testCase.FilePath, CSharpSyntaxTree.ParseText(testCase.Source))).Count();
            if (actual != testCase.Expected)
                failures.Add($"样本[{testCase.Name}] 期望 {testCase.Expected} 处违规，实际 {actual} 处");
        }
        await Assert.That(failures).IsEmpty();
    }
}
