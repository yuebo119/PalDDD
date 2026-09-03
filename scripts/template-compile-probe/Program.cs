// ─────────────────────────────────────────────────────────────
// 模板编译探针（v56 机械门禁）——逐块真实编译 .pal/prompts 模板代码
// 阻断"照抄必炸"缺陷（v53-v55 连续四轮在模板区发现 P1 的根治手段）
// 用法：dotnet run --project scripts/template-compile-probe
// 退出码：0=全部可编译（输出格式段另过 PDDD analyzer）；1=存在编译失败
// ─────────────────────────────────────────────────────────────
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using PalDDD.Analyzers;
using PalDDD.Core.SourceGen;

var root = FindRoot();
var promptsDir = Path.Combine(root, "src", "PalDDD.Prompts", ".pal", "prompts");
var frameworkRefs = FrameworkRefs.Collect();
var analyzer = new StrategicDddAnalyzer();

var templates = Directory.GetFiles(promptsDir, "*.prompt.md").OrderBy(f => f).ToArray();
Console.WriteLine($"模板目录: {promptsDir}（{templates.Length} 个模板）");

var failures = new List<string>();
var totalBlocks = 0;

foreach (var template in templates)
{
    var text = await File.ReadAllTextAsync(template);
    var blocks = Probe.ExtractBlocks(text);
    var name = Path.GetFileName(template);

    // 输出格式段（四反引号）：完整可编译声明 + PDDD analyzer 强制（桩注入——saga/DI 骨架
    // 输出段引用用户域事件类型，剔除逻辑保证块内自定义同名类型优先）
    // 同模板输出段先合并（如 bounded-context 的 DI 段 + app 段同属一个 Program.cs）。
    // v62 记录（v63 补声明）：合并按文档出现序拼接——类型声明段+顶级语句段乱序时将 CS8803，
    // 当前 9 模板两段均顶级语句未踩；模板演进触及时先保段序（类型在前）再合
    var mergedOutput = blocks.OutputBlocks.Select(b => b.Code).ToList();
    if (mergedOutput.Count > 1)
    {
        totalBlocks++;
        var merged = string.Join(Environment.NewLine, mergedOutput);
        var result = Probe.CompileBlock(merged, blocks.OutputBlocks[0].Line, $"{name}(merged)", stubs: true, withAnalyzers: true, frameworkRefs, analyzer);
        if (!result.Ok) failures.Add(result.Diagnosis);
    }
    else
    {
        foreach (var (code, line) in blocks.OutputBlocks)
        {
            totalBlocks++;
            var result = Probe.CompileBlock(code, line, name, stubs: true, withAnalyzers: true, frameworkRefs, analyzer);
            if (!result.Ok) failures.Add(result.Diagnosis);
        }
    }

    // 示例段（三反引号）：仅查编译错误（analyzer 豁免——samples 未挂 analyzer，v55 声明）
    foreach (var (code, line) in blocks.SampleBlocks)
    {
        totalBlocks++;
        var result = Probe.CompileBlock(code, line, name, stubs: true, withAnalyzers: false, frameworkRefs, analyzer, stubAcc: "internal");
        if (!result.Ok) failures.Add(result.Diagnosis);
    }
}

Console.WriteLine($"编译块总数: {totalBlocks}（输出格式段 + 示例段）");
if (failures.Count == 0)
{
    Console.WriteLine("TEMPLATE-GATE: PASS（全部代码块可编译）");
    return 0;
}

Console.WriteLine($"TEMPLATE-GATE: FAIL（{failures.Count} 块失败）");
foreach (var f in failures)
    Console.WriteLine(f);
return 1;

static string FindRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
        dir = dir.Parent!;
    return dir!.FullName;
}

internal static class Probe
{
    public static (List<(string Code, int Line)> OutputBlocks, List<(string Code, int Line)> SampleBlocks) ExtractBlocks(string text)
    {
        var output = new List<(string, int)>();
        var sample = new List<(string, int)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var fence = lines[i].TrimEnd('\r');
            if (fence.StartsWith("````csharp", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                var j = i + 1;
                for (; j < lines.Length && !lines[j].TrimEnd('\r').StartsWith("````", StringComparison.Ordinal); j++)
                    sb.AppendLine(lines[j].TrimEnd('\r'));
                output.Add((sb.ToString(), i + 2));
                i = j;
            }
            else if (fence.StartsWith("```csharp", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                var j = i + 1;
                for (; j < lines.Length && !lines[j].TrimEnd('\r').StartsWith("```", StringComparison.Ordinal); j++)
                    sb.AppendLine(lines[j].TrimEnd('\r'));
                sample.Add((sb.ToString(), i + 2));
                i = j;
            }
        }
        return (output, sample);
    }

    public static (bool Ok, string Diagnosis) CompileBlock(
        string code, int line, string template, bool stubs, bool withAnalyzers,
        ImmutableArray<MetadataReference> refs, DiagnosticAnalyzer analyzer, string stubAcc = "public")
    {
        // 桩与块合并单树（跨树 global-ns 引用在探针引用面下曾不可解析——单树根治）
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(stubs ? Wrap(code) + TemplateStubs.Render(code, $"{template}:{line}", stubAcc) : Wrap(code), path: $"{template}:{line}") };
        if (Environment.GetEnvironmentVariable("PROBE_DUMP") is not null)
        {
            var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".probe-dump");
            Directory.CreateDirectory(dumpDir);
            var safe = $"{template}:{line}".Replace(':', '_').Replace('.', '_');
            File.WriteAllText(Path.Combine(dumpDir, $"{safe}.block.cs"),
                (stubs ? Wrap(code) + TemplateStubs.Render(code, $"{template}:{line}", stubAcc) : Wrap(code))); // v64 P3-6：补 stubAcc（dump 与实际编译一致）
        }

        // 顶级语句块（DI/app 骨架段）必须 ConsoleApplication；纯类型声明块用 Library。
        // v57 P3 局限声明：子串启发式——enum/delegate 声明块（无四关键字）误判 Console；
        // 字面量含 "class " 的顶级块反向。当前 9 模板未踩，模板演进时校准
        var hasTopLevel = !(code.Contains("class ") || code.Contains("record ") || code.Contains("struct ") || code.Contains("interface "));
        var kind = hasTopLevel ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary;
        var compilation = CSharpCompilation.Create($"probe.{template}.{line}",
            trees, refs,
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

        // 挂 PalDDD 源生成器（真实项目编译形态——[GenerateMessage] 的 EventName/descriptor 由生成器提供）
        // IIncrementalGenerator → AsSourceGenerator 包装（Roslyn 公开扩展）
        // 挂 PalDDD 三源生成器（真实项目编译形态——GenerateId 生成 From/New/Value、
        // GenerateEnum 生成注册、GenerateMessage 生成 catalog）
        Microsoft.CodeAnalysis.IIncrementalGenerator[] palGens =
        [
            new MessageRegistryGenerator(),
            new IdentityGenerator(),
            new EnumGenerator(),
        ];
        var driver = CSharpGeneratorDriver.Create(
            palGens.Select(g => g.AsSourceGenerator()),
            parseOptions: (trees[0] as CSharpSyntaxTree)?.Options ?? new CSharpParseOptions());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var withGen, out var genDiags);
        if (Environment.GetEnvironmentVariable("PROBE_DUMP") is not null)
        {
            var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".probe-dump");
            Directory.CreateDirectory(dumpDir);
            foreach (var gt in withGen.SyntaxTrees.Where(t => t.FilePath.EndsWith(".g.cs", StringComparison.Ordinal)))
                File.WriteAllText(Path.Combine(dumpDir, "gen-" + Path.GetFileName(gt.FilePath)), gt.ToString());
        }

        // v57 P1-1：生成器诊断必须进判定——此前 genDiags 被丢弃（PALID/PALMSG/PALENUM 系列
        // 不进红绿），生成器"不生成坏代码"策略下编译层零错误 → 假绿（实证：桩双同名消息
        // 当前就该触发 PALMSG003 但探针绿——丢弃实锤）
        IEnumerable<Diagnostic> diagnostics = withGen.GetDiagnostics().Concat(genDiags);

        if (withAnalyzers)
        {
            var withA = withGen.WithAnalyzers(ImmutableArray.Create(analyzer), default(AnalyzerOptions));
            diagnostics = diagnostics.Concat(withA.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult());
        }

        // v57 P1-3：Warning 级 PDDD 升 Error——真实项目 TreatWarningsAsErrors=true 使
        // PDDD002/006/015 等强制，探针与生产同档（否则探针比生产松一档 → 假绿）
        var errors = diagnostics.Where(d =>
            d.Severity == DiagnosticSeverity.Error
            || (d.Id.StartsWith("PDDD", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Warning))
            .ToList();
        // CS0616@生成树豁免：生成物（[TypeConverter] attribute 绑定）在真实 SDK 引用面下编译通过
        //（独立 csproj 实证）；探针引用面（PalDDD 全家+EF+ASP.NET 共享框架组合）下的 attribute
        // 绑定差异为已知限制，豁免且仅限生成树路径
        errors.RemoveAll(e => e.Id == "CS0616"
            && (e.Location.GetLineSpan().Path?.EndsWith(".g.cs", StringComparison.Ordinal) ?? false));
        if (Environment.GetEnvironmentVariable("PROBE_DUMP") is not null && template.Contains("query-handler"))
        {
            var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".probe-dump");
            Directory.CreateDirectory(dumpDir);
            File.AppendAllText(Path.Combine(dumpDir, "qh-alldiag.txt"),
                string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Severity} {d.Id}: {d.GetMessage()}")));
        }
        if (errors.Count == 0) return (true, "");

        var sb = new StringBuilder();
        sb.AppendLine($"[{template}:{line}]");
        foreach (var e in errors.Take(12))
        {
            var pos = e.Location.GetLineSpan();
            var treeName = Path.GetFileName(pos.Path ?? "");
            sb.AppendLine($"  {e.Id} @{treeName}:{pos.StartLinePosition.Line + 1}: {e.GetMessage()}");
        }
        return (false, sb.ToString());
    }

    // 块包装：预置全框架 using + 挂 EF（模板 DI 骨架/读写库形态需要）
    public static string Wrap(string code) =>
        $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using System.ComponentModel;
        using PalDDD.Core;
        using PalDDD.CQRS;
        using PalDDD.Messaging;
        using PalDDD.Projections;
        using PalDDD.Serialization;
        using PalDDD.Idempotency;
        using PalDDD.Transactions;
        using global::Microsoft.Extensions.DependencyInjection;
        using global::Microsoft.EntityFrameworkCore;
        using global::Microsoft.AspNetCore.Builder;
        using global::Microsoft.AspNetCore.Routing;
        using global::System.Text.Json.Serialization;
        using PalDDD.Dapper;
        using PalDDD.Hosting.AspNetCore;
        using PalDDD.Serialization.Json;
        using PalDDD.DependencyInjection;

        {{code}}
        """;
}

internal static class FrameworkRefs
{
    public static ImmutableArray<MetadataReference> Collect()
    {
        // 框架程序集：PalDDD 全家从输出目录（ProjectReference 已复制），STJ/EF 从已加载程序集定位
        var baseDir = AppContext.BaseDirectory;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>();
        foreach (var dll in Directory.GetFiles(baseDir, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("PalDDD.", StringComparison.Ordinal) || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                refs.Add(MetadataReference.CreateFromFile(dll));
        }
        void AddLoaded(Type t)
        {
            var loc = t.Assembly.Location;
            if (loc.Length > 0 && !refs.OfType<PortableExecutableReference>().Any(r => string.Equals(Path.GetFileNameWithoutExtension(r.FilePath ?? ""), t.Assembly.GetName().Name, StringComparison.Ordinal)))
                refs.Add(MetadataReference.CreateFromFile(loc));
        }
        AddLoaded(typeof(System.Text.Json.JsonSerializer));
        AddLoaded(typeof(Microsoft.Extensions.Primitives.StringValues));
        // ASP.NET Core 共享框架（Hosting.AspNetCore 的 WebApplication/MapCommand 引用面——
        // 共享框架 dll 不复制到 bin，从 dotnet 根的 shared 目录定位）
        var aspDir = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "Microsoft.AspNetCore.App", Path.GetFileName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))));
        var aspCount = 0;
        if (Directory.Exists(aspDir))
            foreach (var dll in Directory.GetFiles(aspDir, "Microsoft.AspNetCore.*.dll"))
            { refs.Add(MetadataReference.CreateFromFile(dll)); aspCount++; }
        // Microsoft.AspNetCore.dll（11.0 的 WebApplication 所在主 dll）——显式追加，不赌通配语义
        var aspMain = Path.Combine(aspDir, "Microsoft.AspNetCore.dll");
        if (File.Exists(aspMain)) { refs.Add(MetadataReference.CreateFromFile(aspMain)); aspCount++; }
        // 核心运行时引用面——System.* 在运行时目录（非 bin 输出目录）
        foreach (var f in new[]
        {
            "System.Runtime", "System.Collections", "System.Linq", "System.Net.Primitives",
            "System.Runtime.Extensions", "System.Text.Json", "System.ComponentModel.Annotations",
            "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Options", "Microsoft.Extensions.Hosting.Abstractions",
            "System.Linq.Expressions", "System.Linq.Queryable", "System.Memory", "System.Text.RegularExpressions", "System.Threading",
            "System.ComponentModel.TypeConverter", "System.Collections.Concurrent", "System.Diagnostics.DiagnosticSource",
            "System.IO.Pipelines", "System.Threading.Channels", "System.Text.Encodings.Web", "System.Private.CoreLib",
            "System.Runtime.Intrinsics", "System.Numerics.Vectors", "System.Runtime.InteropServices",
        })
        {
            var path = Path.Combine(runtimeDir, f + ".dll");
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        // 共享框架（System.Text.Json 等）回退：从运行时目录的 refs/ 提示失败时补
        if (!refs.OfType<PortableExecutableReference>().Any(r => string.Equals(Path.GetFileNameWithoutExtension(r.FilePath ?? ""), "System.Text.Json", StringComparison.Ordinal)))
            throw new InvalidOperationException("System.Text.Json 引用缺失——探针引用收集不完整");
        return refs.ToImmutableArray();
    }
}

// 桩：块内未定义的领域类型（形态对齐 samples/输出格式段；同名剔除）
internal static class TemplateStubs
{
    public static string Render(string blockCode, string tag, string acc = "public")
    {
        // 示例段（samples 单文件 internal 语境）桩用 internal；输出段（public 生态）桩用 public
        var declared = new HashSet<string>();
        // 声明名抓取：keyword 后紧跟 PascalCase 类型名（[A-Z] 开头排除小写修饰符链）——
        // 覆盖 record Name / record struct Name / sealed class Name / readonly record struct Name 全形态
        foreach (Match m in Regex.Matches(blockCode, @"\b(?:record|class|struct|interface)\s+([A-Z]\w*)"))
            declared.Add(m.Groups[1].Value);

        var segments = new List<(string Type, string Text)>();
        // v57 P2-1 声明：桩=samples 真源 ∪ 模板教学扩展（Order.Submit/单参构造/
        // IOrderRepository.SaveChangesAsync 是 command-handler 教学链成员，samples 无——
        // aggregate-root 模板已同步补 Submit 闭合教学链；桩漂移审计见 v57 片2 报告
        var sb = new StringBuilder();
        // using 全部在 Wrap 头（单树合并后 using 必须位于类型声明前）

        void Emit(string typeName, string decl) => segments.Add((typeName, decl));

        Emit("Money", """
            public readonly record struct Money(decimal Amount, string Currency) : IValueObject
            {
                public static Money CNY(decimal a) => new(a, "CNY");
                public override string ToString() => $"{Amount:F2} {Currency}";
            }
            """);
        // v57 P2-3：改 [GenerateId] 真实生成形态（探针已挂 IdentityGenerator——桩手写 From
        // 与生成面漂移即假绿面；partial 声明由生成器合并出 From/New/Value 全成员面）
        // v60 P3-6 豁免声明：本桩必须保持 public——IdentityGenerator 是 Public-only 阈值
        //（v37），internal 转换会触发 PALID006 假红；internal 语境的 Replace 盲区
        //（"partial record struct" 不匹配三 Replace）恰好豁免它，属刻意依赖的现状
        Emit("OrderId", """
            [GenerateId(typeof(Guid))]
            public readonly partial record struct OrderId;
            """);
        Emit("OrderSubmitted", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-submitted.v1")]
            public sealed partial class OrderSubmitted : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                public string CustomerName { get; init; } = "";
                public decimal Amount { get; init; }
                static string IDomainEvent.EventName => "ordering.order-submitted.v1";
            }
            """);
        Emit("OrderConfirmed", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-confirmed.v1")]
            public sealed partial class OrderConfirmed : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                public string Customer { get; init; } = "";
                public Money Total { get; init; }
                static string IDomainEvent.EventName => "ordering.order-confirmed.v1";
            }
            """);
        Emit("ItemAdded", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.item-added.v1")]
            public sealed partial class ItemAdded : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                public string Name { get; init; } = "";
                public int Qty { get; init; }
                public Money Price { get; init; }
                static string IDomainEvent.EventName => "ordering.item-added.v1";
            }
            """);
        Emit("ItemAddedToOrder", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.item-added-to-order.v1")]
            public sealed partial class ItemAddedToOrder : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                public string ProductName { get; init; } = "";
                public Money Price { get; init; }
                public int Quantity { get; init; }
                static string IDomainEvent.EventName => "ordering.item-added-to-order.v1";
            }
            """);
        Emit("OrderItem", "public sealed class OrderItem { public string Name { get; set; } = \"\"; public int Qty { get; set; } public Money Price { get; set; } }");
        Emit("Order", """
            [BoundedContext("ordering")]
            public sealed class Order : AggregateRoot<OrderId>
            {
                private readonly List<OrderItem> _items = [];
                public IReadOnlyList<OrderItem> Items => _items;
                // Id 由基类 Entity<OrderId> 提供——桩自定义会 CS0108 遮蔽并失真模板引用面
                public string CustomerName { get; private set; } = "";
                public Money TotalAmount { get; private set; } = Money.CNY(0);
                public string Status { get; private set; } = "pending";
                public Order(OrderId id) : base(id) { }
                public Order(OrderId id, string customerName) : base(id) { CustomerName = customerName; }
                public void AddItem(string name, int qty, Money price) => _items.Add(new OrderItem { Name = name, Qty = qty, Price = price });
                public void Confirm() { Status = "confirmed"; }
                public void Submit(decimal amount) { TotalAmount = Money.CNY(amount); Status = "submitted"; }
            }
            """);
        Emit("IOrderRepository", """
            public interface IOrderRepository
            {
                void Add(Order order);
                Task SaveChangesAsync(CancellationToken ct);
            }
            """);
        Emit("OrderRepo", "public sealed class OrderRepo { public Order? Get(OrderId id) => null; }");
        Emit("SubmitOrder", "public sealed record SubmitOrder(string CustomerName, decimal Amount) : ICommand;");
        Emit("AddItemCmd", "public sealed record AddItemCmd(Guid OrderId, string Name, int Qty, Money Price) : ICommand;");
        Emit("ConfirmCmd", "public sealed record ConfirmCmd(OrderId OrderId) : ICommand;");
        Emit("GetOrderQry", "public sealed record GetOrderQry(OrderId OrderId) : IQuery<OrderDto?>;");
        Emit("OrderDto", "public sealed record OrderDto(string OrderId, string CustomerName, decimal TotalAmount, string Status);");
        Emit("SubmitOrderHandler", "public sealed class SubmitOrderHandler : ICommandHandler<SubmitOrder, Unit> { public ValueTask<Unit> HandleAsync(SubmitOrder c, CancellationToken ct) => ValueTask.FromResult(new Unit()); }");
        Emit("AddItemHandler", "public sealed class AddItemHandler(OrderRepo r) : ICommandHandler<AddItemCmd, Unit> { public ValueTask<Unit> HandleAsync(AddItemCmd c, CancellationToken ct) => ValueTask.FromResult(new Unit()); }");
        Emit("ConfirmHandler", "public sealed class ConfirmHandler(OrderRepo r) : ICommandHandler<ConfirmCmd, Unit> { public ValueTask<Unit> HandleAsync(ConfirmCmd c, CancellationToken ct) => ValueTask.FromResult(new Unit()); }");
        Emit("GetOrderHandler", "public sealed class GetOrderHandler(OrderRepo r) : IQueryHandler<GetOrderQry, OrderDto?> { public ValueTask<OrderDto?> HandleAsync(GetOrderQry q, CancellationToken ct) => ValueTask.FromResult<OrderDto?>(null); }");
        // 探针桩不挂 STJ 源生成——AppJsonContext 用轻量形态模拟 STJ 生成面
        //（Default.Xxx 属性在真实项目由 STJ 生成器产出，此处仅满足 DI/端点骨架的编译级引用）
        Emit("AppJsonContext", """
            public sealed class AppJsonContext
            {
                public static AppJsonContext Default { get; } = new();
                public System.Text.Json.JsonSerializerOptions Options => throw new NotSupportedException(); // 实例属性（对齐真实 JsonSerializerContext 生成面，v57 P2-2）
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<OrderSubmitted> OrderSubmitted => throw new NotSupportedException();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<OrderConfirmed> OrderConfirmed => throw new NotSupportedException();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<SubmitOrder> SubmitOrder => throw new NotSupportedException();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<AddItemCmd> AddItemCmd => throw new NotSupportedException();
                public System.Text.Json.Serialization.Metadata.JsonTypeInfo<OrderDto> OrderDto => throw new NotSupportedException();
            }
            """);
        Emit("OrderProjectionHandler", """
            [BoundedContext("ordering")]
            public sealed class OrderProjectionHandler : IProjectionHandler<OrderSubmitted>
            {
                public string ProjectionName => "ordering.order-projection";
                public ValueTask ProjectAsync(OrderSubmitted @event, ProjectionContext context, CancellationToken ct) => ValueTask.CompletedTask;
            }
            """);
        Emit("OrderSummary", "public sealed class OrderSummary { public Guid OrderId { get; set; } public string CustomerName { get; set; } = \"\"; public decimal Amount { get; set; } public string Status { get; set; } = \"\"; public DateTimeOffset OccurredAt { get; set; } }");
        Emit("OrderReadDbContext", "public sealed class OrderReadDbContext : DbContext { public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>(); public OrderReadDbContext() { } public OrderReadDbContext(DbContextOptions<OrderReadDbContext> o) : base(o) { } }");
        Emit("OrderDbContext", "public sealed class OrderDbContext : DbContext { public DbSet<Order> Orders => Set<Order>(); public OrderDbContext(DbContextOptions<OrderDbContext> o) : base(o) { } }");
        Emit("OrderRequested", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-requested.v1")]
            public sealed partial class OrderRequested : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                public string CustomerName { get; init; } = "";
                static string IDomainEvent.EventName => "ordering.order-requested.v1";
            }
            """);
        Emit("OrderCreated", """
            [BoundedContext("ordering")]
            [GenerateMessage(Name = "ordering.order-created.v1")]
            public sealed partial class OrderCreated : DomainEvent, IDomainEvent
            {
                public Guid OrderId { get; init; }
                static string IDomainEvent.EventName => "ordering.order-created.v1";
            }
            """);
        Emit("OrderSagaState", "public sealed class OrderSagaState : SagaState { public string? CustomerName { get; set; } public decimal TotalAmount { get; set; } }");
        Emit("OrderSaga", "public sealed class OrderSaga : Saga<OrderSagaState> { }");

        // 同名直接剔除（桩已全 internal——同程序集互相可见，无需传播剔除防 CS0053；
        // 块内定义 X 时剔除桩 X，其余桩对 X 的引用解析到块内同名 internal 类型）
        var excluded = new HashSet<string>(declared);

        foreach (var (ty, text) in segments)
            if (!excluded.Contains(ty))
                sb.AppendLine(acc == "internal" ? text.Replace("public sealed ", "internal sealed ").Replace("public readonly record struct", "internal readonly record struct").Replace("public interface ", "internal interface ") : text);

        if (Environment.GetEnvironmentVariable("PROBE_DUMP") is not null)
        {
            var dumpDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".probe-dump");
            Directory.CreateDirectory(dumpDir);
            var safe = tag.Replace(':', '_').Replace('.', '_');
            File.WriteAllText(Path.Combine(dumpDir, $"{safe}.excluded{excluded.Count}.cs"),
                sb.ToString() + "// excluded: " + string.Join(",", excluded.OrderBy(x => x)));
        }
        return sb.ToString();
    }
}
