using PalDDD.CQRS;
using PalDDD.DependencyInjection;
using PalDDD.EventLog;
using PalDDD.Idempotency;
using PalDDD.Messaging;
using PalDDD.Projections;
using PalDDD.Projections.EventLog;
using PalDDD.Serialization;
using PalDDD.Serialization.Evolution;
using PalDDD.Transactions;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace PalDDD.Core.Tests;

public sealed class PublicApiSnapshotTests
{
    // 范围决策（刻意）：核心 11 程序集——适配层（Dapper/PalORM/EFCore 族）公共面由其
    // 各自集成测试与编译消费锁定，不纳入本快照；快照范围扩大需评审 PublicApiSnapshot
    // 基线成本（基线文件体积与每次公共面变更的更新负担）。
    private static readonly Assembly[] Assemblies =
    [
        typeof(AggregateRoot<>).Assembly,       // PalDDD.Core
        typeof(Dispatcher).Assembly,            // PalDDD.CQRS
        typeof(ServiceRegistration).Assembly,   // PalDDD.DependencyInjection
        typeof(IEventLog).Assembly,             // PalDDD.EventLog
        typeof(IIdempotencyStore).Assembly,     // PalDDD.Idempotency
        typeof(IMessageBroker).Assembly,        // PalDDD.Messaging
        typeof(IProjectionCheckpointStore).Assembly, // PalDDD.Projections
        typeof(EventLogReplaySource<DomainEvent>).Assembly, // PalDDD.Projections.EventLog
        // 三十五轮 P3-2 修复：原第 9 项 typeof(IUnitOfWork).Assembly 与 AggregateRoot<>
        // 同为 PalDDD.Core——快照 PalDDD.Core 段被 dump 两遍（218 行重复），快照体积虚增 47%。
        typeof(IMessageSerializer).Assembly,    // PalDDD.Serialization
        typeof(MessageContractManifest).Assembly, // PalDDD.Serialization.Evolution
        typeof(Saga<>).Assembly                 // PalDDD.Transactions
    ];

    [Test]
    public async Task CorePackagePublicApi_MatchesSnapshot(CancellationToken cancellationToken)
    {
        var actual = BuildSnapshot();
        var snapshotPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "Snapshots",
            "core-packages-public-api.txt"));

        if (Environment.GetEnvironmentVariable("PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS") == "1")
        {
            // P3 修复：CI 守卫——该开关若误泄漏进流水线（环境变量继承/脚本污染），
            // 本测试会静默把金标改写成当前公共面，吞掉 API 破坏（门禁假绿）。
            // CI 下拒绝自更新并快失败，金标更新只允许本地显式执行。
            if (IsCiEnvironment())
            {
                throw new InvalidOperationException(
                    "PALDDD_UPDATE_PUBLIC_API_SNAPSHOTS=1 在 CI 环境被检测到——拒绝在 CI 上自更新公共 API 金标"
                    + "（会静默吞掉 API 破坏）。请在本地更新快照并提交。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual, cancellationToken);
        }

        var expected = await File.ReadAllTextAsync(snapshotPath, cancellationToken);

        await Assert.That(Normalize(expected)).IsEqualTo(Normalize(actual));
    }

    /// <summary>是否处于 CI 环境（CI / GITHUB_ACTIONS 任一非空即判定）。</summary>
    private static bool IsCiEnvironment()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
           || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    private static string BuildSnapshot()
    {
        var builder = new StringBuilder();
        foreach (var assembly in Assemblies.OrderBy(static a => a.GetName().Name, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"# {assembly.GetName().Name}");
            foreach (var type in assembly.GetExportedTypes().OrderBy(static t => t.FullName, StringComparer.Ordinal))
            {
                if (type.IsSpecialName)
                    continue;

                builder.AppendLine(GetTypeSignature(type));

                foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderBy(GetMemberSortKey, StringComparer.Ordinal))
                    builder.AppendLine("  " + GetConstructorSignature(constructor));

                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(static p => p.Name, StringComparer.Ordinal))
                    builder.AppendLine("  " + GetPropertySignature(property));

                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .OrderBy(GetMemberSortKey, StringComparer.Ordinal))
                {
                    builder.AppendLine("  " + GetFieldSignature(field));
                }

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(static method => !method.IsSpecialName)
                    .OrderBy(GetMemberSortKey, StringComparer.Ordinal))
                {
                    builder.AppendLine("  " + GetMethodSignature(method));
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string GetTypeSignature(Type type)
    {
        var kind = type switch
        {
            { IsInterface: true } => "interface",
            { IsEnum: true } => "enum",
            { IsValueType: true } when type.IsAssignableTo(typeof(Delegate)) => "delegate",
            { IsValueType: true } => "struct",
            { IsClass: true, IsAbstract: true, IsSealed: true } => "static class",
            { IsClass: true } => "class",
            _ => "type"
        };

        return $"{kind} {FormatType(type)}";
    }

    private static string GetConstructorSignature(ConstructorInfo constructor)
        => $"ctor({FormatParameters(constructor.GetParameters())})";

    private static string GetPropertySignature(PropertyInfo property)
        => $"property {FormatType(property.PropertyType)} {property.Name}";

    private static string GetFieldSignature(FieldInfo field)
    {
        // 字面量（const）带值（如 PalActivitySource.Name）；只读字段（如 PalMetrics 的
        // Counter<long>、Deleted.No）按 readonly 记类型与名；其余只记类型与名。
        if (field.IsLiteral)
            return $"field const {FormatType(field.FieldType)} {field.Name} = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)}";

        if (field.IsInitOnly)
            return $"field {(field.IsStatic ? "static " : "")}readonly {FormatType(field.FieldType)} {field.Name}";

        return $"field {FormatType(field.FieldType)} {field.Name}";
    }

    private static string GetMethodSignature(MethodInfo method)
        => $"method {FormatType(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";

    private static string FormatParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(static parameter => $"{FormatType(parameter.ParameterType)} {parameter.Name}"));

    private static string FormatType(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;

        if (type.IsArray)
            return FormatType(type.GetElementType()!) + "[]";

        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tickIndex = name.IndexOf('`', StringComparison.Ordinal);
        if (tickIndex >= 0)
            name = name[..tickIndex];

        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">";
    }

    private static string GetMemberSortKey(MethodBase member)
        => member.Name + "(" + FormatParameters(member.GetParameters()) + ")";

    private static string GetMemberSortKey(FieldInfo field)
        => field.Name;

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
