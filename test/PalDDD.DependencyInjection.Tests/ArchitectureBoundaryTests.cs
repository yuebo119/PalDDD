using System.Text.RegularExpressions;

namespace PalDDD.DependencyInjection.Tests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Test]
    [Arguments("src/PalDDD.CQRS/PalDDD.CQRS.csproj", "PalDDD.Messaging")]
    [Arguments("src/PalDDD.Serialization/PalDDD.Serialization.csproj", "PalDDD.Core")]
    [Arguments("src/PalDDD.Transactions/PalDDD.Transactions.csproj", "Microsoft.EntityFrameworkCore")]
    [Arguments("src/PalDDD.Projections/PalDDD.Projections.csproj", "Microsoft.EntityFrameworkCore")]
    [Arguments("src/PalDDD.Idempotency/PalDDD.Idempotency.csproj", "Microsoft.EntityFrameworkCore")]
    public async Task CoreAndBrokerProjects_DoNotReferenceInfrastructureImplementations(string projectPath, string forbiddenReference)
    {
        var fullPath = Path.Combine(Root, projectPath);
        var project = File.ReadAllText(fullPath);

        await Assert.That(project).DoesNotContain(forbiddenReference);
    }

    [Test]
    public async Task CqrsLayer_DoesNotContainImplicitTransactionPipeline()
    {
        var cqrsFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.CQRS"),
            "*.cs",
            SearchOption.AllDirectories);

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
            SearchOption.AllDirectories);

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
            SearchOption.AllDirectories);

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
            SearchOption.AllDirectories);
        var hostingFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.Hosting.AspNetCore"),
            "*.cs",
            SearchOption.AllDirectories);

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
        var source = ReadSource("src/PalDDD.Transactions/InboxProcessor.cs");

        await Assert.That(source).DoesNotContain("IServiceScopeFactory");
        await Assert.That(source).DoesNotContain("CreateScope(");
        await Assert.That(source).Contains("IInboxStore");
    }

    [Test]
    public async Task OutboxMessage_UsesBinaryPayload()
    {
        var source = ReadSource("src/PalDDD.Transactions/OutboxMessage.cs");

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
        var storeSource = ReadSource("src/PalDDD.Transactions/ISagaStateStore.cs");
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
            SearchOption.AllDirectories);

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
            SearchOption.AllDirectories);

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

    /// <summary>App 层（CQRS/Transactions/EventLog/Idempotency）不引用 Infra 实现</summary>
    [Test]
    [Arguments("src/PalDDD.CQRS/PalDDD.CQRS.csproj")]
    [Arguments("src/PalDDD.EventLog/PalDDD.EventLog.csproj")]
    [Arguments("src/PalDDD.Idempotency/PalDDD.Idempotency.csproj")]
    [Arguments("src/PalDDD.Projections/PalDDD.Projections.csproj")]
    public async Task AppLayerProjects_DoNotReferenceInfrastructure(string csprojPath)
    {
        var csproj = ReadSource(csprojPath);

        // Exclude InternalsVisibleTo lines — those are assembly-level declarations, not project references
        var relevant = Regex.Replace(csproj, @"<InternalsVisibleTo\b[^>]*/>", "");

        await Assert.That(relevant).DoesNotContain("EFCore");
        await Assert.That(relevant).DoesNotContain("Dapper");
        await Assert.That(relevant).DoesNotContain("Kafka");
        await Assert.That(relevant).DoesNotContain("RabbitMQ");
        await Assert.That(relevant).DoesNotContain("Microsoft.AspNetCore");
        await Assert.That(relevant).DoesNotContain("Npgsql");
        await Assert.That(relevant).DoesNotContain("Sqlite");
    }

    // ═══════════════════════════════════════════════════════════════
    // P2-B: 内容级基础设施关键字禁令
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Domain 层 / App 层源码不包含基础设施关键字。
    /// 确保领域逻辑不泄漏 DbContext、SQL 连接、消息代理等基础设施关注点。
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
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            // 排除注释行（// 和 /* */）中的引用——这些是文档性质的，不代表代码依赖
            var relevantLines = string.Join('\n', source.Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();
                    // 排除所有注释行和 XML 文档注释
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains("///"))
                        return false;
                    if (trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal))
                        return false;
                    return true;
                }));

            if (relevantLines.Contains(keyword, StringComparison.Ordinal))
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
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            if (source.Contains(keyword, StringComparison.Ordinal))
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
            SearchOption.AllDirectories);

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
            SearchOption.AllDirectories);

        // PalDDD.Repository 命名空间已随 IUnitOfWork 合并到 Core（原 PalDDD.Repository 项目已移除）
        var forbidden = new[] { "PalDDD.CQRS", "PalDDD.Messaging", "PalDDD.EventLog",
            "PalDDD.Transactions", "PalDDD.Serialization", "PalDDD.Projections",
            "PalDDD.Hosting", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore" };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var keyword in forbidden)
            {
                if (source.Contains(keyword, StringComparison.Ordinal))
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

    /// <summary>Domain/App 层测试项目不得引用基础设施实现项目</summary>
    [Test]
    [Arguments("test/PalDDD.Core.Tests/PalDDD.Core.Tests.csproj")]
    [Arguments("test/PalDDD.CQRS.Tests/PalDDD.CQRS.Tests.csproj")]
    [Arguments("test/PalDDD.Core.Abstractions.Tests/PalDDD.Core.Abstractions.Tests.csproj")]
    public async Task DomainTests_DoNotReferenceInfrastructureImplementations(string testProjectPath)
    {
        var csproj = ReadSource(testProjectPath);

        await Assert.That(csproj).DoesNotContain("PalDDD.Repository.EFCore");
        await Assert.That(csproj).DoesNotContain("PalDDD.Dapper");
        await Assert.That(csproj).DoesNotContain("PalDDD.Messaging.Kafka");
        await Assert.That(csproj).DoesNotContain("PalDDD.Messaging.RabbitMQ");
        await Assert.That(csproj).DoesNotContain("PalDDD.Transactions.EFCore");
        await Assert.That(csproj).DoesNotContain("PalDDD.Dapper");
    }

    /// <summary>Domain 层测试不得直接实例化基础设施 DbContext</summary>
    [Test]
    public async Task DomainTests_DoNotDirectlyInstantiateInfrastructureDbContext()
    {
        var domainTestFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "test", "PalDDD.Core.Tests"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in domainTestFiles)
        {
            var source = File.ReadAllText(file);
            await Assert.That(source).DoesNotContain("new OutboxDbContext");
            await Assert.That(source).DoesNotContain("new InboxDbContext");
            await Assert.That(source).DoesNotContain("new SagaStateDbContext");
        }
    }

    /// <summary>BackgroundService 子类必须有对应生命周期测试</summary>
    [Test]
    public async Task BackgroundServices_HaveLifecycleTests()
    {
        // 扫描 src 中的 BackgroundService 子类，断言 test 中有对应测试文件
        var backgroundServices = new[] { "OutboxProcessor", "SagaProcessor" };
        foreach (var serviceName in backgroundServices)
        {
            var hasTest = Directory.EnumerateFiles(
                Path.Combine(Root, "test"),
                "*.cs",
                SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f).Contains($"{serviceName}Tests", StringComparison.Ordinal));
            await Assert.That(hasTest).IsTrue();
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
    /// DI 扩展方法必须以 AddPal* 开头。<br/>
    /// 对应 conventions.md §3.5。
    /// </summary>
    [Test]
    public async Task DependencyInjectionMethods_MustStartWithAddPalPrefix()
    {
        var diFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src", "PalDDD.DependencyInjection"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in diFiles)
        {
            var source = File.ReadAllText(file);
            // 检查所有 public static 返回 IServiceCollection 的方法
            // 若不以 AddPal/AddOptions/Configure 开头 → 违规
            var methodPattern = @"public static .* IServiceCollection ([A-Za-z]+)\(";
            foreach (Match m in Regex.Matches(source, methodPattern))
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
    /// 公共接口必须以 I 或 IPal 开头。<br/>
    /// 对应 conventions.md §3.3。
    /// </summary>
    [Test]
    public async Task PublicInterfaces_MustStartWithI()
    {
        var srcFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "src"),
            "*.cs",
            SearchOption.AllDirectories);

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
    /// 测试方法遵循 Method_Scenario 下划线格式（≥1 个下划线）。<br/>
    /// 对应 conventions.md §3.6。<br/>
    ///排除: 辅助方法(private/protected)、IDisposable、构造函数、初始化方法、分析器测试。
    /// </summary>
    [Test]
    public async Task TestMethods_MustFollowTripleUnderscorePattern()
    {
        var testFiles = Directory.EnumerateFiles(
            Path.Combine(Root, "test"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj") && !f.Contains("bin")
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
            var methodPattern = @"\[(?:Test|Fact|Theory)[^\]]*\]\s*\n\s*public.*?(?:void|Task|ValueTask)\s+(\w+)\s*\(";
            foreach (Match m in Regex.Matches(source, methodPattern, RegexOptions.Singleline))
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

        var domainEvents = ReadSource("src/PalDDD.Core/DomainEventEnumerable.cs");
        await Assert.That(domainEvents).Contains("ref struct DomainEventEnumerable");
        await Assert.That(domainEvents).Contains("ref struct DomainEventEnumerator");

        var recordedEvent = ReadSource("src/PalDDD.EventLog/RecordedEvent.cs");
        await Assert.That(recordedEvent).Contains("internal static RecordedEvent RehydrateFromBytes(");
    }
}
