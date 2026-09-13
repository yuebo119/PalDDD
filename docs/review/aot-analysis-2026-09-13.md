# Dapper / EF Core / PalORM 三栈 AOT 全链路支持深度分析

> 分析编号：AOT-ANALYSIS-2026-09-13
> 数据来源：源码逐行审读 + Web 搜索（Microsoft 官方文档 / GitHub Issues / 社区实证）+ PalOrmSample CI NativeAOT publish 实测

---

## 核心结论（先说答案）

| 持久化栈 | AOT 全链路支持？ | 根因 |
|---------|:---:|------|
| **PalORM** | ✅ **真支持** | PalORM.SourceGen 编译期生成 Row DTO 映射，零运行时反射；PalOrmSample CI NativeAOT publish 实测通过 |
| **Dapper** | ⚠️ **假支持** | `IsAotCompatible=true` 但 `[module: DapperAot]` **被注释掉**——Dapper 2.x 运行时 IL 发射未被消除，AOT 下退化为反射 |
| **EF Core** | ❌ **不支持** | `IsAotCompatible=false` 显式声明；EF Core 11 RC1 NativeAOT 仍为**高度实验性，微软自述"不适合生产"** |

**这意味着**：Pal.DDD 的三持久化栈中，只有 PalORM 栈真正实现了 AOT 全链路。Dapper 栈的 `IsAotCompatible=true` 是编译期无警告口径，不是运行时 AOT 兼容。EF Core 栈诚实声明不支持。

---

## 逐栈深度分析

### 1. Dapper 栈：`IsAotCompatible=true` 但 AOT 拦截未启用

**编译期声明**：
```xml
<IsAotCompatible>true</IsAotCompatible>
<NoWarn>$(NoWarn);IL3058;DAP005;CA2255</NoWarn>
<!-- Dapper.AOT：诊断就绪，[module:DapperAot] 暂未全局启用 -->
<!-- <InterceptorsPreviewNamespaces>...Dapper.AOT</InterceptorsPreviewNamespaces> -->
<!-- <PackageReference Include="Dapper.AOT" /> -->
```

**运行时实际行为**（源码逐行审读实证）：
```
DapperOutboxStore.cs:25 → "运行时经典 Dapper 路径（QueryAsync<T> 物化经 IL 发射，AOT 下退化为反射）"
DapperOutboxStore.cs:110-112 → QueryAsync<OutboxMessage> 走运行时经典 Dapper 路径
DapperOutboxStore.cs:111 → "AOT 拦截未启用，与 csproj IsAotCompatible=true 的差异见 DapperBulkCopy IL2062 注释"
```

**Dapper.AOT 未启用的精确原因**（DapperAotInitializer.cs 逐行审读）：

```csharp
// 🔧 待三个 handler 迁移至 Dapper.TypeHandler<T>（AOT 侧抽象，Parse(DbParameter) 非装箱签名）
//    + 全量回归 + NativeAOT 发布实测后启用（二十五轮 API 扫描 A5 勘正，见上）
// [module: DapperAot]    ← 被注释掉
```

| 障碍 | 经典 Dapper（当前） | Dapper.AOT（目标） | 迁移工作量 |
|---|---|---|---|
| TypeHandler 基类 | `SqlMapper.TypeHandler<T>` — `Parse(object)` 装箱 | `Dapper.TypeHandler<T>` — `Parse(DbParameter)` 非装箱 | 3 个 handler 重写基类 |
| 泛型物化 | `QueryAsync<T>` 运行时 IL 发射 | 编译时拦截器生成映射代码 | `[module: DapperAot]` 启用 |
| dynamic 限制 | 支持 `dynamic` | 不允许 dynamic 参数/返回值 | 无影响（项目已用具体类型） |
| 全量回归 | — | NativeAOT publish + 运行验证 | CI 新增 AOT job |

**结论**：Dapper.AOT 技术上**可以实现 AOT**（构建时源生成替代运行时反射），但 Pal.DDD 的 Dapper 栈**尚未启用**——`IsAotCompatible=true` 只表示"AOT 编译不报错"（IL 警告被 NoWarn 抑制），不表示"AOT 运行时正确"。

### 2. EF Core 栈：诚实声明不支持

**编译期声明**：
```xml
<IsAotCompatible>false</IsAotCompatible>
```

**EF Core 11 RC1 NativeAOT 现状**（2025 年搜索实证）：
- [Microsoft 官方文档](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)明确声明：**"NativeAOT 和查询预编译是高度实验性功能，尚不适合在生产环境中使用"**
- [C# Corner 分析](https://www.c-sharpcorner.com/article/ef-core-10-nativeaot-whats-actually-broken-and-why2/)：EF Core 10 的 NativeAOT 支持仍标记为实验性——编译模型体积庞大导致 AOT 二进制过大
- [开发者实测](https://blog.cubed.run/i-turned-on-native-aot-in-net-10-for-our-api-then-ef-core-stopped-working-7fcb64131a79)：.NET 10 + Native AOT + EF Core → "EF Core stopped working"
- [GitHub Issue #34446](https://github.com/dotnet/efcore/issues/34446)：EF Core 团队承认运行时表达式树编译是 AOT 的核心障碍

**EF Core 预编译查询（实验性路径）**：
```
dotnet ef dbcontext optimize --precompile-queries
```
→ 使用源生成器和拦截器在编译时分析表达式树，生成 AOT 兼容的查询代码——但 Microsoft 自述"实验性，可能变更"。

**结论**：EF Core **截至 11 RC1 仍未实现 AOT 全链路支持**——微软官方立场是实验性。Pal.DDD 的 `IsAotCompatible=false` 是诚实的。

### 3. PalORM 栈：真 AOT 全链路

**编译期声明**：
```xml
<IsAotCompatible>true</IsAotCompatible>
<Description>Pal.DDD PalORM 持久化适配核心层（真 AOT + 源生成 + 编译期方言特化）</Description>
```

**AOT 实现机制**：
- PalORM.SourceGen NuGet 包：编译期从 `[Column]`/`[Key]`/`[ConcurrencyCheck]` 属性生成 Row DTO 映射代码
- 零运行时反射：所有 SQL/映射/并发谓词在编译期生成
- PalOrmSample：CI 每次 NativeAOT publish + 运行断言（Outbox CRUD + 事务提交回滚）

**与 Dapper/EF Core 的根本差异**：

| 特性 | Dapper 经典 | Dapper.AOT | EF Core | PalORM |
|---|---|---|---|---|
| 类型物化 | 运行时 IL 发射 | 编译时拦截器 | 运行时表达式树编译 | 编译时源生成 |
| NativeAOT 兼容 | ❌ | ✅（有限制） | ⚠️ 实验性 | ✅ |
| dynamic 支持 | ✅ | ❌ | ❌（AOT 下）| ❌ |
| 生产就绪 | ✅ | ⚠️ 需验证 | ❌ 微软自述不适合 | ✅ CI 验证 |
| 编译期 SQL | ❌ | ❌（运行时 SQL）| ⚠️ 预编译实验 | ✅ 源生成 |

---

## 对 Pal.DDD 三持久化栈策略的影响

### 当前策略（ADR-020）

```
PalORM（AOT 主线）+ EFCore（生态兼容线）；Dapper 栈退役
v3.0: Dapper [Obsolete] → v4.0: 移除五包
```

### 本分析的验证结论

**ADR-020 的方向是正确的**——且本分析进一步强化了其合理性：

| 发现 | 对 ADR-020 的影响 |
|---|---|
| Dapper 栈 `IsAotCompatible=true` 实际是"编译不报错"而非"运行时 AOT 正确" | **强化退役合理性**——消费者可能误以为 Dapper 栈是 AOT 安全的 |
| Dapper.AOT 可以启用但有前置条件（TypeHandler 迁移 + NativeAOT publish 验证） | **两个选项**：①投入迁移工作使 Dapper 栈真正 AOT（延迟退役）②按 ADR-020 原计划退役（推荐——PalORM 已是真 AOT） |
| EF Core NativeAOT 仍实验性 | **不影响**——EF Core 栈的定位是生态兼容而非 AOT，`IsAotCompatible=false` 诚实 |

### 建议行动

| 优先级 | 行动 | 理由 |
|:--:|------|------|
| **高** | Dapper 栈 csproj Description 补充"IsAotCompatible=true 是编译期口径，运行时经典 Dapper 路径在 AOT 下退化为反射" | 防消费者误判 |
| **高** | DapperOutboxStore.cs:111 的"AOT 拦截未启用"注释升级为 XML doc `<remarks>` | 当前是行内注释，消费者通过 NuGet XML doc 看不到 |
| **中** | 评估 Dapper.AOT TypeHandler 迁移的 ROI——3 个 handler 重写 + NativeAOT publish 验证 vs ADR-020 退役路线 | 如果 v3.0 退役时间确定，迁移投入浪费 |
| **低** | 跟踪 EF Core 11 GA 的 NativeAOT 进展 | 如果 GA 时生产就绪，EF Core 栈可评估启用 |

---

## sources

- [Microsoft Learn: NativeAOT Support and Precompiled Queries (Experimental)](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries) — 微软官方："高度实验性，尚不适合生产"
- [DapperLib/DapperAOT — GitHub](https://github.com/DapperLib/DapperAOT) — Dapper.AOT 构建时源生成工具
- [Dapper.AOT Getting Started](https://aot.dapperlib.dev/gettingstarted.html) — Dapper.AOT 使用文档
- [C# Corner: EF Core 10 NativeAOT — What's Actually Broken](https://www.c-sharpcorner.com/article/ef-core-10-nativeaot-whats-actually-broken-and-why2/) — EF Core 10 AOT 限制分析
- [Blog: I Turned On Native AOT in .NET 10 For Our API](https://blog.cubed.run/i-turned-on-native-aot-in-net-10-for-our-api-then-ef-core-stopped-working-7fcb64131a79) — 开发者实测 EF Core AOT 失败
- [GitHub Issue #34446: Future NativeAOT work](https://github.com/dotnet/efcore/issues/34446) — EF Core 团队 AOT 讨论
- [Reddit: Microsoft's own EF Core docs recommend Dapper.AOT](https://www.reddit.com/r/dotnet/comments/1sg48zk/) — 社区讨论
- [Medium: Native AOT and databases](https://codevision.medium.com/native-aot-and-databases-87b26f2fcfc8) — AOT 数据访问模式分析
- [Thinktecture: Data Access in .NET Native AOT](https://www.thinktecture.com/en/net/data-access-in-net-native-aot-with-sessions/) — AOT 数据访问方案对比
- [Nanorm — GitHub](https://github.com/DamianEdwards/Nanorm) — AOT 兼容微型 ORM（Damian Edwards）
