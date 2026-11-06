# ADR 038：超时放弃的 handler 持已释放 scope —— 分期处置与阶段 2 立项

> 状态：**已采纳（2026-11-07）**，阶段 1 为「登记 + 披露 + 独立可观测项」，**不含根因修复**；阶段 2 需独立 ADR 与性能裁决
> 日期：2026-11-07
> 关联：2026-11-06 全量功能审计（`docs/review/audit-2026-11-06-full.md` P1 B4）、修复方案 v4（`docs/review/fix-plan-2026-11-07-v4.md` §2.2）、ADR-026（Dispatcher 复用 RequestServices 性能定案）、`TimeoutBehavior.cs` 文件头语义定案

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-11-07 |
| 评审状态 | 已采纳（方案 v4 经独立红队 8 方向复核，阶段 1 定级与范围已收敛）；阶段 2 待立项 |
| 兼容性 | 阶段 1 零公共 API 变更、零行为变更（仅日志/Activity 事件与文档）。已发布版本行为不变 |
| 触发原因 | 审计 P1：`TimeoutBehavior` 超时后 abandoned handler 持已释放的请求 scope 继续运行，触碰 scoped 依赖抛 ODE 且被静默吞掉；叠加「CQRS 请求面无幂等键」，客户端 504 重试可能双写 |

## 背景

三条事实（源码实证，非推测）：

1. **超时只放弃等待**：`src/PalDDD.CQRS/TimeoutBehavior.cs` 的 `WaitWithTimeoutAsync` 在超时抛 `TimeoutException`，`running` 任务在后台继续跑（文件头定案「进程内无法安全中止任意异步工作」）。
2. **HTTP 路径复用请求 scope**：`src/PalDDD.CQRS/Dispatcher.cs:310-314` 在 `requestServices is not null` 时直接 `entry.Executor(requestServices, ...)`，不自建 scope——这是 ADR-026 的性能定案（消除每请求两个 DbContext）。
3. **scope 随响应释放**：`src/PalDDD.Hosting.AspNetCore/AspNetCore/ExceptionMiddleware.cs:106-128` 把 `TimeoutException` 映射 504 并写出响应；ASP.NET 在端点返回后释放请求 scope，此后 abandoned handler 解析 scoped 依赖即抛 `ObjectDisposedException`。

## 决策

### 阶段 1（本 ADR 范围，已实施）

保持 P1 定级，**不做根因修复**，只做三件不改变运行时行为的事：

| # | 动作 | 载体 |
|---|------|------|
| 1 | 后果披露：504 后 RequestServices 已释放，abandoned handler 触碰 scoped 依赖会抛 ODE | `docs/usage.md` 超时小节 |
| 2 | 未决项登记（含阶段 2 立项条目） | `.ai/state.md`「未决」段 |
| 3 | abandoned handler 失败记 Activity 事件（**独立 P3 可观测项**，不充当 B4 修法） | `TimeoutBehavior.ObserveFault` |

同时把「CQRS 请求面无幂等键 → 504 重试可能双写」作为**独立条目单列**，不并入本项一笔带过。

### 阶段 2（不在本 ADR 实施，需独立 ADR + 性能裁决）

根因修复只能走「Dispatcher 侧 scope 归属」形态，两条候选路径：

- **A**：`PipelineStateMachine` 的 `next` 接受可替换的 `IServiceProvider`，由 Dispatcher 提供请求级工作上下文承载 scope 生命周期；
- **B**：引入请求级工作上下文（scope 持有者）在 `SendAsync` 返回前等 abandoned 任务收尾再释放。

两条都必然回退 ADR-026 的性能定案（每请求两个 DbContext 回归），因此**必须作为性能裁决单列**，不能静默实施。

## 证据锚点表

| 主张 | 锚点 |
|------|------|
| 超时即抛、后台继续 | `src/PalDDD.CQRS/TimeoutBehavior.cs:70-84`（`WaitWithTimeoutAsync` 的 `catch (TimeoutException)` 分支） |
| HTTP 复用请求 scope | `src/PalDDD.CQRS/Dispatcher.cs:310-314`（三个分支中的 `requestServices is not null`） |
| 504 映射与响应写出 | `src/PalDDD.Hosting.AspNetCore/AspNetCore/ExceptionMiddleware.cs:106-128` |
| 端点传 RequestServices | `src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs:57`、`:97`、`:184` |
| 原静默吞异常 | `src/PalDDD.CQRS/TimeoutBehavior.cs` 的 `ObserveFault`（原 `_ = t.Exception`） |
| 阶段 1 披露文本 | `docs/usage.md`「请求级超时（可选）」小节 |
| 未决登记 | `.ai/state.md`「未决」段的 B4-P1 条目 |

## 已否决的形态（均有实证，防复活）

| 形态 | 否决理由 |
|------|---------|
| Dispatcher 等 abandoned 收尾再返回（v3 形态） | 504 被推迟到 handler 跑完（时序探针 118ms → 505ms，4.3×）；`Task.WhenAll(bag)` 会重抛 abandoned 异常使 504 变 500 |
| 只在 `requestServices is not null` 分支修 | Dispatcher 有三分支，后台/消息消费者路径的 `_bag.Value` 为 null → NRE 或静默丢弃 |
| linked CTS 中断 handler | 违反 `TimeoutBehavior.cs` 文件头「框架不强制中断」定案 |
| 把 scope 释放挂到 `Executor(...)` 返回 Task 的续体 | 被代码证伪：`WaitWithTimeoutAsync` 超时即抛，`Executor` 返回的是立刻 faulted 的短任务，不含仍在后台跑的 `running`，续体会提前释放 scope |
| 全面改回自建 scope | 违反 ADR-026 性能定案，须走阶段 2 的性能裁决 |

## 开放决策点

1. **阶段 2 立项**：走 A（可替换 `IServiceProvider`）还是 B（请求级工作上下文）？两者都需 ADR-026 性能定案回退裁决。
2. **CQRS 请求面幂等键**：是否在框架层提供请求级幂等键（而非要求每个 handler 自建）？属独立设计项，未立项。
3. **复现判据与复评触发条件**：
   - 复现判据：504 端点 + handler 内 `await Task.Delay(_timeout * 2)` 后解析 scoped 依赖 → 观测到 `ObjectDisposedException`（当前无自动化用例，属已知缺口）。
   - 复评触发：① 出现生产 ODE 报告；② 504 端点接入幂等键设计；③ 有请求级 scope 归属的性能裁决结论。
