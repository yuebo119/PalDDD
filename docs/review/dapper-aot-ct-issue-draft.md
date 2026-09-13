# Dapper.AOT 上游 issue 草稿(ct 调用点入口)

> 用途:提交到 DapperLib/DapperAOT 的 issue 文本草稿(ITM-673)。
> 提交前删除本说明与中文段,正文译英文。

---

**Title**: Feature request: call-site syntax for passing CancellationToken to generated interceptor code

**Body**:

## Background

Dapper.AOT 1.1.0's generated `Command<T>` pipeline fully supports `CancellationToken`:

```csharp
// Dapper.AOT 1.1.0, CommandT.Query.cs / CommandT.Execute.cs
public Task<TRow> QueryFirstOrDefaultAsync<TRow>(TArgs args, ..., CancellationToken cancellationToken = default)
public Task ExecuteAsync(TArgs args, CancellationToken cancellationToken = default)
```

However, there is no call-site syntax to reach it. The two existing spellings both fail:

1. **`CommandDefinition`** — refused by DAP057 ("Dapper.AOT cannot read SQL at build time"), which is the *only* overload that accepts a ct in vanilla Dapper.
2. **Direct overloads** — vanilla Dapper's `QueryAsync<T>(sql, param, transaction, commandTimeout, commandType)` has **no ct parameter** (verified: CS1739).

## Consequence

AOT-enabled projects must choose between:
- ct-aware SQL execution but no AOT interception (CommandDefinition → vanilla Dapper → `PlatformNotSupportedException` under NativeAOT for anonymous-type params), or
- AOT interception but no ct at the SQL execution layer (direct overloads).

We hit this in a framework with 34 call-sites across 6 stores; we currently accept the ct contraction (connection timeout as backstop), but per-command cancellation is a real operational requirement for graceful shutdown in some deployments.

## Request

Please provide a call-site spelling that passes ct into the generated pipeline, e.g.:

- an extended direct overload: `QueryAsync<T>(sql, param, transaction, commandTimeout, commandType, CancellationToken)` — intercepted like the ct-less form, or
- an AOT-readable variant of `CommandDefinition` (e.g. a struct with known property layout the generator can deconstruct), or
- any documented pattern you consider idiomatic.

## Environment

- Dapper 2.1.79, Dapper.AOT 1.1.0, .NET 11 rc1
- Verified via scratch probe: direct overloads + const/property/switch-const SQL all intercept correctly; CommandDefinition with anonymous params throws `PlatformNotSupportedException` under NativeAOT (`SqlMapper.CreateParamInfoGenerator`).

Happy to provide the probe project if useful.
