using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PalDDD.DependencyInjection.Tests;

/// <summary>P3-SRC-601（R45）：构建产物排除 helper——全部 *.cs 目录扫描统一走此过滤，
/// 消除"仅因 obj 生成文件恰无关键字子串交集才不爆"的巧合式安全（GlobalUsings.g.cs 含 HttpClient）。</summary>
internal static class BuildArtifactFilter
{
    public static bool IsNotBuildArtifact(string file) =>
        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");
}

public sealed class ArchitectureBoundaryTests
{
    private static readonly string Root = FindRepositoryRoot();

    // ═══════════════════════════════════════════════════════════════
    // csproj 引用解析辅助（P1 修复：评审报告 P1-1）
    //
    // 旧实现用文本 Contains 匹配 forbiddenRefs 子串——`Confluent.Kafka`、
    // `RabbitMQ.Client`、`MySqlConnector`、`Pomelo.*`、`MySql.EntityFrameworkCore`
    // 均不含任何被禁子串，非 Infra 项目引用它们不会被拦截（守卫盲区）。
    // 现改为 XDocument 解析 PackageReference/ProjectReference 的 Include 属性，
    // 并用"精确名或 包名+'.' 前缀"匹配（覆盖 Npgsql → Npgsql.EntityFrameworkCore
    // 这类家族包，同时避免子串误报）。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>解析 csproj，提取全部包引用名与项目引用的目标项目名。
    /// 路径分隔符先归一化——仓库 ProjectReference 全用反斜杠，
    /// <see cref="Path.GetFileNameWithoutExtension"/> 在 Linux（CI 运行平台）上对反斜杠
    /// 路径返回整段路径，导致项目引用守卫在 CI 上永不命中（二轮评审 P2-NEW-1，
    /// 由负向自证测试 <see cref="ParseCsprojReferences_NormalizesWindowsPathSeparators"/> 锁定）。</summary>
    private static (IReadOnlyList<string> Packages, IReadOnlyList<string> ProjectNames) ParseCsprojReferences(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        var packages = doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
        var projectNames = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            // 归一化：csproj 内路径常为 Windows 分隔符（MSBuild 双向兼容），Unix 上
            // Path 处理反斜杠路径不拆分——先统一为 '/' 再取文件名，跨平台行为一致
            .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/')))
            .ToList();
        return (packages, projectNames);
    }

    /// <summary>包名匹配：精确相等，或以 包名+'.' 开头（家族前缀，如 Npgsql → Npgsql.Json.NET）。</summary>
    private static bool MatchesPackage(string packageName, string forbidden)
        => packageName.Equals(forbidden, StringComparison.Ordinal)
           || packageName.StartsWith(forbidden + ".", StringComparison.Ordinal);

    /// <summary>非 Infra 项目（Core/App/Serialization/CQRS/DI）禁止引用的基础设施包清单。
    /// 涵盖三栈（EFCore/Dapper/PalORM）全部 Provider、消息 Client、Web 宿主。</summary>
    private static readonly string[] s_infraPackages =
    [
        "Microsoft.EntityFrameworkCore", // 前缀覆盖 .Relational/.Sqlite/.Design 等全家
        "Dapper",
        "PalORM",                        // 前缀覆盖 PalORM.Core/PalORM.SourceGen
        "Npgsql",                        // 前缀覆盖 Npgsql.EntityFrameworkCore/Npgsql.Json.NET
        "MySqlConnector",
        "MySql.EntityFrameworkCore",
        "MySql.Data",
        "Pomelo",                        // 前缀覆盖 Pomelo.EntityFrameworkCore.MySql
        "Microsoft.Data.Sqlite",
        "Confluent.Kafka",
        "RabbitMQ.Client",
        "Microsoft.AspNetCore"           // 前缀覆盖 .Hosting/.Http 等（App 层禁止；Hosting.AspNetCore 适配器在 Infra 白名单）
    ];

    /// <summary>Core/App 层项目不得引用基础设施实现包。
    /// 动态扫描 src/ 下所有 Core/App 层项目 csproj，禁止引用 Infra 实现包。
    /// Core/App 层定义：非 Infra 适配器、非工具链的领域/应用层项目。
    /// 避免硬编码项目列表导致新增项目时守护失效。</summary>
    [Test]
    public async Task CoreAndBrokerProjects_DoNotReferenceInfrastructureImplementations()
    {
        // Infra 适配器层和工具链项目（允许引用 Infra 包）
        var infraProjects = new HashSet<string>(StringComparer.Ordinal)
        {
            "PalDDD.Core.SourceGen", "PalDDD.Analyzers", "PalDDD.Analyzers.CodeFixes",
            "PalDDD.Transactions.EFCore", "PalDDD.EventLog.EFCore", "PalDDD.Repository.EFCore",
            "PalDDD.Projections.EFCore", "PalDDD.Idempotency.EFCore",
            "PalDDD.Dapper", "PalDDD.Dapper.PostgreSql", "PalDDD.Dapper.MySql", "PalDDD.Dapper.Sqlite",
            "PalDDD.PalORM", "PalDDD.PalORM.PostgreSql", "PalDDD.PalORM.MySql", "PalDDD.PalORM.Sqlite",
            "PalDDD.Messaging.Kafka", "PalDDD.Messaging.RabbitMQ",
            "PalDDD.Hosting.AspNetCore", "PalDDD.Compression.Native",
            "PalDDD.Serialization.MemoryPack", "PalDDD.Serialization.Evolution",
            "PalDDD.Extension", "PalDDD.Base", "PalDDD.Prompts"
        };

        var srcCsprojs = Directory.EnumerateFiles(
            Path.Combine(Root, "src"),
            "*.csproj",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        var violations = new List<string>();
        foreach (var csprojPath in srcCsprojs)
        {
            var projectName = Path.GetFileNameWithoutExtension(csprojPath);
            if (infraProjects.Contains(projectName))
                continue;

            var (packages, projectNames) = ParseCsprojReferences(csprojPath);

            // 包引用：精确名/家族前缀匹配（覆盖 Confluent.Kafka、RabbitMQ.Client、
            // MySqlConnector、Pomelo.* 等旧文本子串匹配的盲区）
            foreach (var forbidden in s_infraPackages)
                if (packages.Any(p => MatchesPackage(p, forbidden)))
                    violations.Add($"{projectName} 引用了禁止的 Infra 包 '{forbidden}'");

            // 项目引用：目标项目名落在 Infra 白名单集合即违规
            foreach (var referenced in projectNames)
                if (infraProjects.Contains(referenced))
                    violations.Add($"{projectName} 引用了 Infra 项目 '{referenced}'");
        }

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task CqrsLayer_DoesNotContainImplicitTransactionPipeline()
    {
        var cqrsFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.CQRS"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in cqrsFiles)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("TransactionAttribute");
            await Assert.That(source).DoesNotContain("TransactionBehavior");
        }
    }

    [Test]
    public async Task MessagingLayer_DoesNotExposeUnusedEventFilterApi()
    {
        var eventFilterPath = Path.Combine(Root, "src", "PalDDD.Messaging", "EventFilter.cs");

        await Assert.That(File.Exists(eventFilterPath)).IsFalse();
    }

    [Test]
    public async Task RepositoryLayer_DoesNotExposeGenericRepositoryAbstraction()
    {
        await Assert.That(File.Exists(Path.Combine(Root, "src", "PalDDD.Repository.EFCore", "RepositoryBase.cs"))).IsFalse();

        // IUnitOfWork 已合并到 PalDDD.Core（原 PalDDD.Repository 项目已移除）
        var unitOfWork = ReadSource("src/PalDDD.Core/IUnitOfWork.cs");

        await Assert.That(unitOfWork).DoesNotContain("IRepository<");
        await Assert.That(unitOfWork).DoesNotContain("IQueryable<");
        await Assert.That(unitOfWork).DoesNotContain("Repository<");
        await Assert.That(unitOfWork).DoesNotContain("Query<");
    }

    [Test]
    public async Task EfCoreUnitOfWork_DoesNotUseServiceProviderBackedRepositoryCache()
    {
        var source = ReadSource("src/PalDDD.Repository.EFCore/UnitOfWork.cs");

        await Assert.That(source).DoesNotContain("IServiceProvider");
        await Assert.That(source).DoesNotContain("ConcurrentDictionary");
        await Assert.That(source).DoesNotContain("GetService");
        await Assert.That(source).DoesNotContain("_repos");
    }

    [Test]
    public async Task CoreLayer_DoesNotExposeIntegrationEventMarkerOrUpcasterPlaceholders()
    {
        await Assert.That(File.Exists(Path.Combine(Root, "src", "PalDDD.Core", "IIntegrationEvent.cs"))).IsFalse();

        var coreFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Core"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in coreFiles)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("IIntegrationEvent");
            await Assert.That(source).DoesNotContain("IUpcaster");
        }
    }

    [Test]
    public async Task SerializationEvolution_ProvidesExecutionPipelineNotPayloadMarkers()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Serialization.Evolution"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("IIntegrationEvent");
            await Assert.That(source).DoesNotContain("IUpcaster");
        }

        await Assert.That(
            ReadSource("src/PalDDD.Serialization.Evolution/MessageEvolutionPipeline.cs"))
            .Contains("MessageEvolutionPipeline");
    }

    [Test]
    public async Task MessageBroker_NonGenericPublishRequiresExplicitMessageId()
    {
        var source = ReadSource("src/PalDDD.Messaging/MessageBroker.cs");

        await Assert.That(source).Contains("PalUlid messageId");
        await Assert.That(source).DoesNotContain("PublishAsync(object message, MessageDescriptor descriptor, CancellationToken");
    }

    [Test]
    public async Task CoreAndHosting_DoNotExposeCustomAmbientContextCarrier()
    {
        await Assert.That(File.Exists(Path.Combine(Root, "src", "PalDDD.Core", "ContextCarrier.cs"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Root, "src", "PalDDD.Hosting.AspNetCore", "AspNetCore", "TracingMiddleware.cs"))).IsFalse();

        var coreFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Core"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);
        var hostingFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Hosting.AspNetCore"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in coreFiles.Concat(hostingFiles))
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("ContextCarrier");
            await Assert.That(source).DoesNotContain("AsyncLocal<Dictionary");
            await Assert.That(source).DoesNotContain("UsePalTracing");
        }
    }

    [Test]
    public async Task SerializationRuntimeCatalog_IsImmutable()
    {
        var source = ReadSource("src/PalDDD.Serialization/MessageCatalog.cs");

        await Assert.That(source).Contains("interface IMessageCatalog");
        await Assert.That(source).Contains("sealed class MessageCatalog");
        await Assert.That(source).Contains("sealed class MessageCatalogBuilder");
        await Assert.That(source).DoesNotContain("public void Register(");
    }

    [Test]
    [Arguments("src/PalDDD.Transactions/ServiceCollectionExtensions.cs", "TryAddSingleton<OutboxOptions>")]
    [Arguments("src/PalDDD.Transactions/ServiceCollectionExtensions.cs", "TryAddSingleton<InboxOptions>")]
    [Arguments("src/PalDDD.Transactions/ServiceCollectionExtensions.cs", "TryAddSingleton<SagaProcessorOptions>")]
    public async Task TransactionRegistrations_UseOptionsPattern(string path, string forbiddenText)
    {
        var source = ReadSource(path);

        await Assert.That(source).DoesNotContain(forbiddenText);
        await Assert.That(source).Contains("AddOptions");
        await Assert.That(source).Contains("ValidateOnStart");
    }

    [Test]
    public async Task InboxProcessor_DoesNotUseServiceLocator()
    {
        var source = ReadSource("src/PalDDD.Transactions/Inbox/InboxProcessor.cs");

        await Assert.That(source).DoesNotContain("IServiceScopeFactory");
        // 不带括号：同时覆盖 CreateScope( 与 CreateScopeAsync( 两形态（带括号会被 Async 形态绕过）
        await Assert.That(source).DoesNotContain("CreateScope");
        await Assert.That(source).Contains("IInboxStore");
    }

    [Test]
    public async Task OutboxMessage_UsesBinaryPayload()
    {
        var source = ReadSource("src/PalDDD.Transactions/Outbox/OutboxMessage.cs");

        await Assert.That(source).Contains("byte[] Payload");
        await Assert.That(source).DoesNotContain("public string Content ");
    }

    [Test]
    public async Task CoreProjects_EnableAotReferenceVerification()
    {
        var props = ReadSource("Directory.Build.props");

        await Assert.That(props).Contains("<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>");
    }

    [Test]
    public async Task CoreProjects_DisableSystemTextJsonReflectionDefaults()
    {
        var props = ReadSource("Directory.Build.props");

        await Assert.That(props).Contains("<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>");
    }

    [Test]
    public async Task SagaTimeoutStore_UsesBoundedActiveScan()
    {
        var storeSource = ReadSource("src/PalDDD.Transactions/Saga/ISagaStateStore.cs");
        var efSource = ReadSource("src/PalDDD.Transactions.EFCore/SagaStateDbContext.cs");

        await Assert.That(storeSource).Contains("GetActiveSagasAsync(int batchSize");
        await Assert.That(efSource).Contains(".Take(batchSize)");
        await Assert.That(storeSource).DoesNotContain("GetActiveSagasAsync(CancellationToken ct)");
    }

    /// <summary>
    /// 动态扫描所有 IsAotCompatible=false 的业务项目，断言三属性齐全。<br/>
    /// 排除 SourceGen/Analyzers/CodeFixes（Roslyn 工具链项目，false 是工具特性非业务豁免）。<br/>
    /// 元审计 R7 预防：避免硬编码 Theory 列表漏检新增项目。
    /// </summary>
    [Test]
    public async Task InfrastructureAdapters_AreExplicitlyNonAot()
    {
        var toolProjects = new HashSet<string>(StringComparer.Ordinal)
        {
            "PalDDD.Core.SourceGen", "PalDDD.Analyzers", "PalDDD.Analyzers.CodeFixes"
        };

        var csprojFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src"),
            "*.csproj",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        var checkedProjects = 0;
        foreach (var csprojPath in csprojFiles)
        {
            var project = File.ReadAllText(csprojPath);
            if (!project.Contains("<IsAotCompatible>false</IsAotCompatible>", StringComparison.Ordinal))
                continue;

            var projectName = Path.GetFileNameWithoutExtension(csprojPath);
            if (toolProjects.Contains(projectName))
                continue;

            checkedProjects++;
            await Assert.That(project).Contains("<IsAotCompatible>false</IsAotCompatible>");
            await Assert.That(project).Contains("<IsTrimmable>false</IsTrimmable>");
            await Assert.That(project).Contains("<VerifyReferenceAotCompatibility>false</VerifyReferenceAotCompatibility>");
        }

        // 断言至少检查了 8 个业务项目（当前基线），防止扫描逻辑空转
        await Assert.That(checkedProjects >= 8).IsTrue();
    }

    [Test]
    public async Task HostingAspNetCoreNamespace_MatchesProjectName()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Hosting.AspNetCore"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("namespace PalDDD.DependencyInjection.AspNetCore");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // P2-C: DI 生命周期守护
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// OutboxDomainEventInterceptor 持有实例字段 _pending，必须注册为 Scoped。<br/>
    /// 若误改为 Singleton，_pending 会被并发请求交叉写入，破坏事件收集语义。
    /// </summary>
    [Test]
    public async Task OutboxDomainEventInterceptor_IsRegisteredAsScoped()
    {
        var source = ReadSource("src/PalDDD.Repository.EFCore/ServiceCollectionExtensions.cs");

        // 断言使用 TryAddScoped（而非 TryAddSingleton / TryAddTransient）
        await Assert.That(source).Contains("TryAddScoped<OutboxDomainEventInterceptor>");
        await Assert.That(source).DoesNotContain("TryAddSingleton<OutboxDomainEventInterceptor>");
        await Assert.That(source).DoesNotContain("TryAddTransient<OutboxDomainEventInterceptor>");
    }

    // ═══════════════════════════════════════════════════════════════
    // P2-B: Core 反向依赖验证
    // ═══════════════════════════════════════════════════════════════

    /// <summary>PalDDD.Core 不引用任何 App 层或 Infra 层项目 — 领域核心零反向依赖</summary>
    [Test]
    public async Task CoreLayer_HasNoProjectReferences()
    {
        var csproj = ReadSource("src/PalDDD.Core/PalDDD.Core.csproj");

        await Assert.That(csproj).DoesNotContain("ProjectReference");
        // Core is allowed ByteAether.Ulid (Ulid type) — no other package references
        var packageRefs = Regex.Matches(csproj, "<PackageReference");
        await Assert.That(packageRefs).Count().IsEqualTo(1);
        await Assert.That(csproj).Contains("ByteAether.Ulid");
    }

    /// <summary>PalDDD.Serialization 不引用任何应用层或基础设施项目</summary>
    [Test]
    [Arguments("src/PalDDD.Serialization/PalDDD.Serialization.csproj")]
    public async Task AbstractionsLayer_HasNoAppOrInfraReferences(string csprojPath)
    {
        var csproj = ReadSource(csprojPath);

        // 抽象层零项目引用（纯接口/类型定义）
        await Assert.That(csproj).DoesNotContain("ProjectReference");
        await Assert.That(csproj).DoesNotContain("PalDDD.CQRS");
        await Assert.That(csproj).DoesNotContain("PalDDD.EventLog");
        await Assert.That(csproj).DoesNotContain("PalDDD.Transactions");
        await Assert.That(csproj).DoesNotContain("PalDDD.Messaging");
        await Assert.That(csproj).DoesNotContain("EFCore");
        await Assert.That(csproj).DoesNotContain("Dapper");
        await Assert.That(csproj).DoesNotContain("Microsoft.AspNetCore");
    }

    /// <summary>App 层（CQRS/Transactions/EventLog/Idempotency）不引用 Infra 实现。
    /// XML 解析引用（对齐 CoreAndBrokerProjects 守卫的 P1 修复），禁止清单含
    /// PalORM/MySqlConnector/Confluent 等旧文本匹配盲区。</summary>
    [Test]
    [Arguments("src/PalDDD.CQRS/PalDDD.CQRS.csproj")]
    [Arguments("src/PalDDD.EventLog/PalDDD.EventLog.csproj")]
    [Arguments("src/PalDDD.Idempotency/PalDDD.Idempotency.csproj")]
    [Arguments("src/PalDDD.Projections/PalDDD.Projections.csproj")]
    public async Task AppLayerProjects_DoNotReferenceInfrastructure(string csprojPath)
    {
        var (packages, projectNames) = ParseCsprojReferences(Path.Combine(Root, csprojPath));

        // App 层项目引用任何 Infra 项目即违规
        var infraProjectTokens = new[] { "EFCore", "Dapper", "PalORM", "Kafka", "RabbitMQ", "Sqlite" };
        foreach (var referenced in projectNames)
            foreach (var token in infraProjectTokens)
                if (referenced.Contains(token, StringComparison.Ordinal))
                    Assert.Fail($"App 层项目 {csprojPath} 引用了 Infra 项目 '{referenced}'。");

        foreach (var forbidden in s_infraPackages)
            if (packages.Any(p => MatchesPackage(p, forbidden)))
                Assert.Fail($"App 层项目 {csprojPath} 引用了禁止的 Infra 包 '{forbidden}'。");
    }

    // ═══════════════════════════════════════════════════════════════
    // P2-B: 内容级基础设施关键字禁令
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Domain 层 / App 层源码不包含基础设施关键字。
    /// 确保领域逻辑不泄漏 DbContext、SQL 连接、消息代理等基础设施关注点。
    /// 注释过滤用正则去除 // 行内注释和 /// XML 文档注释，避免尾随注释中的关键字导致误报。
    /// </summary>
    [Test]
    [Arguments("src/PalDDD.Core", "DbContext")]
    [Arguments("src/PalDDD.Core", "SqlConnection")]
    [Arguments("src/PalDDD.Core", "NpgsqlConnection")]
    [Arguments("src/PalDDD.Core", "DbCommand")]
    [Arguments("src/PalDDD.Core", ".Dapper.")]
    [Arguments("src/PalDDD.Serialization", "DbContext")]
    [Arguments("src/PalDDD.Serialization", "SqlConnection")]
    [Arguments("src/PalDDD.CQRS", "DbContext")]
    [Arguments("src/PalDDD.CQRS", "SqlConnection")]
    [Arguments("src/PalDDD.Messaging", "DbContext")]
    [Arguments("src/PalDDD.Messaging", "SqlConnection")]
    [Arguments("src/PalDDD.EventLog", "DbContext")]
    [Arguments("src/PalDDD.EventLog", "SqlConnection")]
    [Arguments("src/PalDDD.Transactions", "DbContext")]
    [Arguments("src/PalDDD.Transactions", "SqlConnection")]
    [Arguments("src/PalDDD.Idempotency", "DbContext")]
    [Arguments("src/PalDDD.Idempotency", "SqlConnection")]
    [Arguments("src/PalDDD.Projections", "DbContext")]
    [Arguments("src/PalDDD.Projections", "SqlConnection")]
    public async Task DomainAndAppLayers_DoNotContainInfrastructureKeywords(string directory, string keyword)
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, directory),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            // 用正则精确去除注释：整行注释（// 开头）、XML 文档注释（///）、块注释行（* 或 /* 开头）、行内尾随注释
            var codeOnly = Regex.Replace(source, @"//.*$", "", RegexOptions.Multiline); // 去除行内和整行 // 注释
            codeOnly = Regex.Replace(codeOnly, @"/\*.*?\*/", "", RegexOptions.Singleline); // 去除 /* */ 块注释

            if (codeOnly.Contains(keyword, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"文件 {Path.GetRelativePath(Root, file)} 包含禁止的基础设施关键字 '{keyword}'。" +
                    $"Domain/App 层不应泄漏基础设施关注点。");
            }
        }
    }

    /// <summary>App 层不包含 HTTP/REST 基础设施关键字</summary>
    [Test]
    [Arguments("src/PalDDD.CQRS", "HttpClient")]
    [Arguments("src/PalDDD.CQRS", "IHttpClientFactory")]
    [Arguments("src/PalDDD.CQRS", "HttpContext")]
    [Arguments("src/PalDDD.Transactions", "HttpClient")]
    [Arguments("src/PalDDD.Transactions", "HttpContext")]
    [Arguments("src/PalDDD.EventLog", "HttpContext")]
    [Arguments("src/PalDDD.Messaging", "HttpContext")]
    public async Task AppLayers_DoNotContainHttpInfrastructureKeywords(string directory, string keyword)
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, directory),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            // 注释剥离（对齐 DomainAndAppLayers_DoNotContainInfrastructureKeywords 姊妹正则）：
            // 文档注释中的 HTTP 关键字是说明性引用，不代表代码依赖 HTTP 基础设施
            var codeOnly = Regex.Replace(source, @"//.*$", "", RegexOptions.Multiline); // 去除行内和整行 // 注释
            codeOnly = Regex.Replace(codeOnly, @"/\*.*?\*/", "", RegexOptions.Singleline); // 去除 /* */ 块注释

            if (codeOnly.Contains(keyword, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"文件 {Path.GetRelativePath(Root, file)} 包含禁止的 HTTP 关键字 '{keyword}'。" +
                    $"App 层不应依赖 ASP.NET Core HTTP 基础设施。");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // P2-B: 命名空间一致性
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 核心项目和 App 层中的文件命名空间不包含基础设施层 token。
    /// 允许抽象层（PalDDD.Serialization）中定义的命名空间。
    /// </summary>
    [Test]
    [Arguments("src/PalDDD.CQRS")]
    [Arguments("src/PalDDD.EventLog")]
    [Arguments("src/PalDDD.Idempotency")]
    [Arguments("src/PalDDD.Messaging")]
    [Arguments("src/PalDDD.Projections")]
    [Arguments("src/PalDDD.Transactions")]
    [Arguments("src/PalDDD.Core")]
    [Arguments("src/PalDDD.Serialization")]
    public async Task DomainAndAppNamespaces_DoNotContainInfrastructureTokens(string directory)
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, directory),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        // 禁止的完整命名空间——仅匹配 PalDDD 基础设施实现层命名空间
        // 使用完整形式避免误报：
        // - PalDDD.Transactions.EFCore（而非通用的 ".EFCore"）
        // - PalDDD.Hosting.AspNetCore（而非通用的 ".Hosting"）
        var forbiddenTokens = new[] {
            "PalDDD.Transactions.EFCore", "PalDDD.Repository.EFCore",
            "PalDDD.EventLog.EFCore", "PalDDD.Projections.EFCore",
            "PalDDD.Idempotency.EFCore",
            "PalDDD.Dapper", "PalDDD.Dapper.PostgreSql", "PalDDD.Dapper.MySql", "PalDDD.Dapper.Sqlite",
            "PalDDD.Hosting.AspNetCore",
            "PalDDD.Messaging.Kafka", "PalDDD.Messaging.RabbitMQ",
            "PalDDD.Serialization.Evolution"
        };

        foreach (var file in files)
        {
            // 跳过 obj/ 和 bin/ 目录下的自动生成文件
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            // 跳过 AssemblyInfo.cs 和已知包含文档引用的文件
            var fileName = Path.GetFileName(file);
            if (fileName is "AssemblyInfo.cs" or "DapperDbType.cs")
                continue;

            var source = File.ReadAllText(file);
            // 排除注释行——文档引用不代表代码依赖
            var relevantLines = string.Join('\n', source.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                           && !trimmed.StartsWith('*')
                           && !trimmed.StartsWith("/*", StringComparison.Ordinal);
                }));

            foreach (var token in forbiddenTokens)
            {
                if (relevantLines.Contains(token, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"文件 {Path.GetRelativePath(Root, file)} 包含禁止的基础设施命名空间 '{token}'。");
                }
            }
        }
    }

    /// <summary>Domain 核心（PalDDD.Core）的 using 不引入应用或基础设施命名空间</summary>
    [Test]
    public async Task CoreLayer_Usings_DoNotImportAppOrInfrastructureNamespaces()
    {
        var files = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Core"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        // PalDDD.Repository 命名空间已随 IUnitOfWork 合并到 Core（原 PalDDD.Repository 项目已移除）
        var forbidden = new[] { "PalDDD.CQRS", "PalDDD.Messaging", "PalDDD.EventLog",
            "PalDDD.Transactions", "PalDDD.Serialization", "PalDDD.Projections",
            "PalDDD.Hosting", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore" };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            // 注释剥离（对齐 DomainAndAppLayers_DoNotContainInfrastructureKeywords 姊妹正则）：
            // 注释中的层名是说明性文字，不代表 using 引入
            var codeOnly = Regex.Replace(source, @"//.*$", "", RegexOptions.Multiline); // 去除行内和整行 // 注释
            codeOnly = Regex.Replace(codeOnly, @"/\*.*?\*/", "", RegexOptions.Singleline); // 去除 /* */ 块注释

            foreach (var keyword in forbidden)
            {
                if (codeOnly.Contains(keyword, StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"文件 {Path.GetRelativePath(Root, file)} 的 using 引用了禁止的命名空间 '{keyword}'。" +
                        $"PalDDD.Core 是领域核心层，不应依赖应用或基础设施层。");
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // P1-3: 测试分层守护 — 防止测试耦合基础设施实现
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Domain/App 层测试项目不得引用基础设施实现项目。
    /// 动态扫描 test/ 下所有测试项目 csproj，禁止引用 Infra 实现包。
    /// Domain/App 层测试定义：不含 Integration/PalORM/Messaging.Integration 的测试项目。
    /// 避免硬编码项目列表导致新增测试项目时守护失效。</summary>
    [Test]
    public async Task DomainTests_DoNotReferenceInfrastructureImplementations()
    {
        // Infra 测试项目（允许引用 Infra 实现）。
        // PalDDD.Hosting.AspNetCore.Tests：Hosting 适配器专属测试（XML 解析守卫的
        // 首个真实发现——旧文本匹配因清单缺 Hosting 而从未覆盖它）。
        var infraTestProjects = new HashSet<string>(StringComparer.Ordinal)
        {
            "PalDDD.Integration.Tests", "PalDDD.PalORM.Tests",
            "PalDDD.Messaging.Integration.Tests", "PalDDD.Repository.EFCore.Tests",
            "PalDDD.EventLog.Tests", "PalDDD.Projections.EventLog.Tests",
            "PalDDD.Hosting.AspNetCore.Tests"
        };

        var forbiddenProjectRefs = new HashSet<string>(StringComparer.Ordinal)
        {
            "PalDDD.Repository.EFCore", "PalDDD.Dapper", "PalDDD.Dapper.PostgreSql",
            "PalDDD.Dapper.MySql", "PalDDD.Dapper.Sqlite",
            "PalDDD.Messaging.Kafka", "PalDDD.Messaging.RabbitMQ",
            "PalDDD.Transactions.EFCore", "PalDDD.EventLog.EFCore",
            "PalDDD.Projections.EFCore", "PalDDD.Idempotency.EFCore",
            "PalDDD.Hosting.AspNetCore"
        };

        var testCsprojs = Directory.EnumerateFiles(
            Path.Combine(Root, "test"),
            "*.csproj",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        var violations = new List<string>();
        foreach (var csprojPath in testCsprojs)
        {
            var projectName = Path.GetFileNameWithoutExtension(csprojPath);
            if (infraTestProjects.Contains(projectName))
                continue;

            var (packages, projectNames) = ParseCsprojReferences(csprojPath);

            // 域测试禁止直接引用 Infra 实现包（消息 Client/ORM Provider/EFCore 全家）
            foreach (var forbidden in s_infraPackages)
                if (packages.Any(p => MatchesPackage(p, forbidden)))
                    violations.Add($"{projectName} 引用了禁止的 Infra 包 '{forbidden}'");

            foreach (var referenced in projectNames)
                if (forbiddenProjectRefs.Contains(referenced))
                    violations.Add($"{projectName} 引用了 Infra 实现 project '{referenced}'");
        }

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>Domain 层测试不得直接实例化基础设施 DbContext</summary>
    [Test]
    public async Task DomainTests_DoNotDirectlyInstantiateInfrastructureDbContext()
    {
        var domainTestFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "test", "PalDDD.Core.Tests"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in domainTestFiles)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("new OutboxDbContext");
            await Assert.That(source).DoesNotContain("new InboxDbContext");
            await Assert.That(source).DoesNotContain("new SagaStateDbContext");
        }
    }

    /// <summary>BackgroundService 子类必须有对应生命周期测试。
    /// 动态扫描 src/ 中 Domain/App 层继承 BackgroundService 的具体类，断言 test/ 中有对应测试文件。
    /// 排除：Infra 适配器层（Dapper/PalORM/EFCore/Kafka/RabbitMQ）、抽象基类。</summary>
    [Test]
    public async Task BackgroundServices_HaveLifecycleTests()
    {
        // Infra 适配器层目录（其中的 BackgroundService 不要求独立测试）
        var infraDirs = new[] { "PalDDD.Dapper", "PalDDD.PalORM", ".EFCore", "PalDDD.Messaging", "PalDDD.Compression" };

        // 抽象基类（不是具体服务，不要求独立测试文件）
        var abstractBaseClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "PeriodicBackgroundProcessor" // 抽象基类，子类 OutboxProcessor/SagaProcessor 才是被测对象
        };

        var srcFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !infraDirs.Any(d => f.Contains(d, StringComparison.Ordinal)));

        var testFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "test"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var missing = new List<string>();
        foreach (var file in srcFiles)
        {
            var source = File.ReadAllText(file);
            // 匹配 sealed class Xxx : BackgroundService 或 sealed class Xxx : PeriodicBackgroundProcessor
            // 只匹配 sealed（具体类），排除 abstract 基类；可选 <T> 泛型参数段——
            // 泛型服务类（如 sealed class Xxx<T> : BackgroundService）此前被漏检
            var classMatches = Regex.Matches(source, @"sealed\s+class\s+(\w+)(?:<[^>]*>)?\s*:\s*(?:BackgroundService|PeriodicBackgroundProcessor)");
            foreach (Match m in classMatches)
            {
                var className = m.Groups[1].Value;
                if (abstractBaseClasses.Contains(className))
                    continue;

                var hasTest = testFiles.Any(f =>
                    Path.GetFileName(f).Equals($"{className}Tests.cs", StringComparison.Ordinal) ||
                    Path.GetFileName(f).Contains($"{className}Test", StringComparison.Ordinal));
                if (!hasTest)
                    missing.Add(className);
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    /// 守卫负向自证（评审 P1-1）：用内联坏 csproj 样本证明 XML 解析 + 前缀匹配
    /// 不再对旧文本子串匹配的盲区包（Confluent.Kafka / RabbitMQ.Client /
    /// MySqlConnector / Pomelo.* / Npgsql 家族）放行——修复前这些包名不含任何
    /// 被禁子串，守卫对它们是无声 no-op。
    /// </summary>
    [Test]
    public async Task CsprojReferenceGuard_DetectsBlindSpotPackages()    {
        const string maliciousCsproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Confluent.Kafka" Version="2.0.0" />
                <PackageReference Include="RabbitMQ.Client" Version="7.0.0" />
                <PackageReference Include="MySqlConnector" Version="3.0.0" />
                <PackageReference Include="Pomelo.EntityFrameworkCore.MySql" Version="11.0.0" />
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="11.0.0" />
              </ItemGroup>
            </Project>
            """;
        var tempPath = Path.Combine(Path.GetTempPath(), $"palddd-guard-probe-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(tempPath, maliciousCsproj);
        try
        {
            var (packages, _) = ParseCsprojReferences(tempPath);

            // InternalsVisibleTo/普通文本中的包名不可见——解析只看 Include 属性
            await Assert.That(packages.Count).IsEqualTo(5);

            var caught = s_infraPackages
                .Where(forbidden => packages.Any(p => MatchesPackage(p, forbidden)))
                .ToList();
            // 五个盲区包必须全部命中（Pomelo 命中前缀、Npgsql.EntityFrameworkCore
            // 命中 Npgsql 家族前缀）
            foreach (var expected in new[] { "Confluent.Kafka", "RabbitMQ.Client", "MySqlConnector", "Pomelo", "Npgsql" })
                await Assert.That(caught).Contains(expected);

            // 反向：合法包（Microsoft.Extensions.DependencyInjection）不被误报
            await Assert.That(s_infraPackages.Any(f => MatchesPackage("Microsoft.Extensions.DependencyInjection", f))).IsFalse();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// 守卫跨平台自证（二轮评审 P2-NEW-1）：仓库 ProjectReference 全用 Windows 反斜杠路径，
    /// 修复前 <c>Path.GetFileNameWithoutExtension</c> 在 Linux（CI 运行平台 ubuntu-latest）上
    /// 对反斜杠路径返回<b>整段路径</b>——项目引用守卫在 CI 上永不命中（守卫平台性 no-op）。
    /// 本测试用反斜杠路径样本锁定归一化行为，在任何平台上都必须通过。
    /// </summary>
    [Test]
    public async Task ParseCsprojReferences_NormalizesWindowsPathSeparators()
    {
        const string backslashCsproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\PalDDD.Core\PalDDD.Core.csproj" />
                <ProjectReference Include="..\PalDDD.Dapper\PalDDD.Dapper.csproj" />
              </ItemGroup>
            </Project>
            """;
        var tempPath = Path.Combine(Path.GetTempPath(), $"palddd-pathnorm-probe-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(tempPath, backslashCsproj);
        try
        {
            var (_, projectNames) = ParseCsprojReferences(tempPath);

            // 反斜杠路径必须解析出裸项目名（而非整段路径）——否则 infraProjects/
            // forbiddenProjectRefs 的 Contains 守卫在 Linux CI 上永不命中
            await Assert.That(projectNames.Count).IsEqualTo(2);
            await Assert.That(projectNames).Contains("PalDDD.Core");
            await Assert.That(projectNames).Contains("PalDDD.Dapper");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(Root, relativePath));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PalDDD.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate PalDDD.slnx.");
    }

    // ═══════════════════════════════════════════════════════════════
    // 命名守护（conventions.md §3 自动化执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// DI 扩展方法命名扫描正则（ITM-643 修复）：捕获返回 <c>IServiceCollection</c> 的公共静态方法名，
    /// 供 <see cref="DependencyInjectionMethods_MustStartWithAddPalPrefix"/> 执行 AddPal* 前缀守护。
    /// <para>
    /// 修复前正则 <c>public static .* IServiceCollection ([A-Za-z]+)\(</c> 对合法 C# 恒不匹配——
    /// 贪婪的 <c>.*</c> 吃掉返回类型后，<c>([A-Za-z]+)\(</c> 的捕获被推到方法参数 <c>services)</c> 上，
    /// <c>\(</c> 随即失配（实测源码 grep 命中 0），守卫体从不执行、整个命名守护是无声 no-op。
    /// 现正则锚定 <c>IServiceCollection</c> 之后紧跟方法名（可选泛型参数表），并以 \s*\( 收尾。
    /// </para>
    /// </summary>
    private const string DiMethodPattern =
        @"public static\s+IServiceCollection\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>(]*>)?\s*\(";

    /// <summary>
    /// DI 扩展方法必须以 AddPal* 开头。<br/>
    /// 对应 conventions.md §3.5。
    /// </summary>
    [Test]
    public async Task DependencyInjectionMethods_MustStartWithAddPalPrefix()
    {
        var diFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.DependencyInjection"),
            "*.cs",
            SearchOption.AllDirectories).Where(BuildArtifactFilter.IsNotBuildArtifact);

        foreach (var file in diFiles)
        {
            var source = File.ReadAllText(file);
            // 检查所有 public static 返回 IServiceCollection 的方法
            // 若不以 AddPal/AddOptions/Configure 开头 → 违规
            foreach (Match m in Regex.Matches(source, DiMethodPattern))
            {
                var methodName = m.Groups[1].Value;
                await Assert.That(
                    methodName.StartsWith("AddPal", StringComparison.Ordinal)
                    || methodName.StartsWith("AddOptions", StringComparison.Ordinal)
                    || methodName.StartsWith("Configure", StringComparison.Ordinal)).IsTrue();
            }
        }
    }

    /// <summary>
    /// 扫描器负向自证（ITM-643）：用一个违规 <c>RegisterPalCore(...)</c> 样本证明
    /// <see cref="DiMethodPattern"/> 能捕获方法名，并用合法 <c>AddPalDDD</c> 证明不误报——
    /// 修复前正则对任何合法 C# 都零捕获（守卫恒空转，返回 0 命中），此样本会使旧正则失败。
    /// </summary>
    [Test]
    public async Task DiMethodPattern_DetectsNonAddPalReturningIServiceCollection()
    {
        const string violating = """
            public static IServiceCollection RegisterPalCore(this IServiceCollection services)
            {
                return services;
            }
            """;

        const string compliant = """
            public static IServiceCollection AddPalDDD(this IServiceCollection services)
            {
                return services;
            }
            """;

        var offendingNames = Regex.Matches(violating, DiMethodPattern)
            .Select(m => m.Groups[1].Value)
            .ToList();
        await Assert.That(offendingNames).Contains("RegisterPalCore");
        await Assert.That(offendingNames[0].StartsWith("AddPal", StringComparison.Ordinal)).IsFalse();

        var compliantNames = Regex.Matches(compliant, DiMethodPattern)
            .Select(m => m.Groups[1].Value)
            .ToList();
        await Assert.That(compliantNames).Contains("AddPalDDD");
        await Assert.That(compliantNames[0].StartsWith("AddPal", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// 公共接口必须以 I 或 IPal 开头。<br/>
    /// 对应 conventions.md §3.3。
    /// </summary>
    [Test]
    public async Task PublicInterfaces_MustStartWithI()
    {
        var srcFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var file in srcFiles)
        {
            var lines = File.ReadAllLines(file);
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("public interface ", StringComparison.Ordinal)
                    || trimmed.StartsWith("public partial interface ", StringComparison.Ordinal))
                {
                    // 提取接口名
                    var parts = trimmed.Replace("public interface ", "")
                        .Replace("public partial interface ", "")
                        .Split('<', ' ', ':')[0];
                    await Assert.That(parts.StartsWith('I')).IsTrue();
                }
            }
        }
    }

    /// <summary>
    /// 测试方法签名扫描正则（F19 修复：消除参数化方法假阴性），供
    /// <see cref="TestMethods_MustFollowUnderscorePattern"/> 执行 Method_Scenario
    /// 下划线格式守护（≥1 个下划线，对应 conventions.md §3.6；排除辅助方法/private/protected、
    /// IDisposable、构造函数、初始化方法、分析器测试）：<br/>
    /// ① 特性块允许多行——[Test] 后可跟 [Arguments]/[MethodDataSource] 等参数化特性行，
    ///    且单特性内容可跨行（后续特性用 [^\]]* 匹配，字符类否定天然含换行）；
    ///    原正则要求特性紧邻 public 行，参数化方法全部漏检，多行 Arguments 参数形态亦漏检；<br/>
    /// ② 返回类型支持泛型——[^\s(]* 吃掉 Task&lt;T&gt;/ValueTask&lt;T&gt; 尾巴
    ///    （原正则 (?:void|Task|ValueTask)\s+ 漏检泛型返回）；<br/>
    /// ③ 刻意不用 RegexOptions.Singleline：public 与返回类型约定同行（[^\n]*?），
    ///    Singleline 下特性块的 . 会跨行贪婪吞掉后续多个方法（[^\]]* 遇 ] 即停，无此问题）。
    /// </summary>
    private const string TestMethodPattern =
        @"\[(?:Test|Fact|Theory)[^\]]*\](?:\s*\n\s*\[[^\]]*\])*\s*\n\s*public[^\n]*?\b(?:ValueTask|Task|void)[^\s(]*\s+(\w+)\s*\(";

    /// <summary>
    /// 扫描器负向自证（falsification，F19）：用故意坏命名的参数化/泛型返回样本
    /// 证明 <see cref="TestMethodPattern"/> 不再对参数化方法盲——修复前该形态全漏检，
    /// 扫描器对它是无声 no-op（静默放行 = 覆盖率为零的门禁）。
    /// </summary>
    [Test]
    public async Task TestMethodPattern_DetectsParameterizedAndGenericReturnMethods()
    {
        // 坏样本 1：[Test] + [Arguments] 参数化特性 + 坏命名（无下划线）
        const string parameterized = """
            [Test]
            [Arguments(1, 2)]
            public async Task BadParameterizedName(int a, int b)
            {
                await Task.CompletedTask;
            }

            """;

        // 坏样本 2：泛型返回 Task<T> + 坏命名（原正则漏检泛型返回形态）
        const string genericReturn = """
            [Test]
            public async Task<bool> BadGenericReturnName(int a)
            {
                return true;
            }
            """;

        // 坏样本 3：[Arguments] 参数跨多行 + 坏命名——特性内容含换行时
        // 特性块匹配若用不跨行的模式会漏检（[^\]]* 可跨行，此样本证伪"行级假设"）
        const string multiLineArguments = """
            [Test]
            [Arguments(
                1,
                2)]
            public async Task BadMultiLineArgumentsName(int a, int b)
            {
                await Task.CompletedTask;
            }
            """;

        var detected = new List<string>();
        foreach (Match m in Regex.Matches(parameterized + genericReturn + multiLineArguments, TestMethodPattern))
            detected.Add(m.Groups[1].Value);

        await Assert.That(detected).Contains("BadParameterizedName");
        await Assert.That(detected).Contains("BadGenericReturnName");
        await Assert.That(detected).Contains("BadMultiLineArgumentsName");
    }

    // 诚实命名（ITM-645）：实断言为 ≥1 个下划线（项目约定 Method_Scenario 允许单下划线分段），
    // 原名 TripleUnderscorePattern 与断言不符——改为 UnderscorePattern。
    [Test]
    public async Task TestMethods_MustFollowUnderscorePattern()
    {
        var testFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "test"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains("ArchitectureBoundaryTests.cs")
                && !f.Contains("PalDDD.Analyzers.Tests")
                && !f.Contains("PalDDD.Core.Abstractions.Tests")
                && !f.Contains("PalDDD.Testing")
                && Path.GetFileName(f).EndsWith("Tests.cs", StringComparison.Ordinal));

        var exemptPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Dispose", "DisposeAsync", "get_", "set_", "add_", "remove_",
            "Initialize", "InitializeAsync", "Setup", "Cleanup"
        };

        var violations = new List<string>();

        foreach (var file in testFiles)
        {
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source, TestMethodPattern))
            {
                var methodName = m.Groups[1].Value;
                if (exemptPrefixes.Any(p => methodName.StartsWith(p, StringComparison.Ordinal)))
                    continue;

                var underscoreCount = methodName.Count(c => c == '_');
                if (underscoreCount < 1)
                    violations.Add($"测试方法 '{methodName}' ({file})");
            }
        }

        if (violations.Count > 0)
            Assert.Fail($"发现 {violations.Count} 个测试方法不遵循 Method_Scenario 下划线格式（至少1个下划线）:\n{string.Join("\n", violations)}");
    }

    // ═══════════════════════════════════════════════════════════════
    // 性能契约守护（conventions.md §12）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task PerformanceContract_FrozenDictionaryAndPipelineStateMachineAndRefStruct()
    {
        await Assert.That(File.Exists(Path.Combine(Root, "src/PalDDD.CQRS/PipelineStateMachine.cs"))).IsTrue();

        // ITM-645：Test 名含 FrozenDictionary，补对应断言——Dispatcher 注册表冻结后
        // 必须走 FrozenDictionary（普通 Dictionary 回归会使 O(1) 只读查找契约失效）。
        var dispatcher = ReadSource("src/PalDDD.CQRS/Dispatcher.cs");
        await Assert.That(dispatcher).Contains("FrozenDictionary");

        var domainEvents = ReadSource("src/PalDDD.Core/DomainEventEnumerable.cs");
        await Assert.That(domainEvents).Contains("ref struct DomainEventEnumerable");
        await Assert.That(domainEvents).Contains("ref struct DomainEventEnumerator");

        var recordedEvent = ReadSource("src/PalDDD.EventLog/RecordedEvent.cs");
        await Assert.That(recordedEvent).Contains("internal static RecordedEvent RehydrateFromBytes(");
    }
}
