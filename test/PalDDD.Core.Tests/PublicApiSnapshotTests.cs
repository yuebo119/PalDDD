using PalDDD.Core.Repository;
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
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual, cancellationToken);
        }

        var expected = await File.ReadAllTextAsync(snapshotPath, cancellationToken);

        await Assert.That(Normalize(expected)).IsEqualTo(Normalize(actual));
    }

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

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
