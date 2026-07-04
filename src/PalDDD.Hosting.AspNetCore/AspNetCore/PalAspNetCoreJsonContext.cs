using System.Text.Json.Serialization;

namespace PalDDD.Hosting.AspNetCore;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PalHealthResponse))]
[JsonSerializable(typeof(ValidationProblemResponse))]
[JsonSerializable(typeof(HandlerNotFoundProblemResponse))]
[JsonSerializable(typeof(InternalServerErrorProblemResponse))]
internal sealed partial class PalAspNetCoreJsonContext : JsonSerializerContext;

internal sealed record PalHealthResponse(
    string Status,
    DateTimeOffset Timestamp,
    double TotalDurationMs,
    PalHealthComponent[] Components);

internal sealed record PalHealthComponent(
    string Name,
    string Status,
    string? Description,
    double DurationMs,
    string[] Tags);

internal sealed record ValidationProblemResponse(
    string Type,
    string Title,
    int Status,
    ValidationProblemError[] Errors);

internal sealed record ValidationProblemError(string Property, string Message);

internal sealed record HandlerNotFoundProblemResponse(
    string Type,
    string Title,
    int Status,
    string Detail);

internal sealed record InternalServerErrorProblemResponse(
    string Type,
    string Title,
    int Status);
