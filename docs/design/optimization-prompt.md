# Pal.DDD 全量缺陷挖掘提示词（v2，2026-10-22）

> 复制全文给 AI。先填 §0 参数。SCAN 轮不改代码。
> 核心原则：候选可以大胆，结论必须克制。先求覆盖，再求证实。
> 目标不是写一份好看的报告，而是在预算内产出尽可能多的可证伪候选，并把其中能证实的变成带证据的缺陷。

## 0. 启动参数

```text
MODE      = SCAN | FIX | LOOP | REGRESS
ROUND     = <整数，从 1 开始>
FOCUS     = ALL | SECURITY | CORRECTNESS | PERF | MEMORY | LATENCY | ARCH | ROBUSTNESS
BUDGET    = <分钟，默认 180>
FIX_LIMIT = <默认 5>
AUDIT_DIR = /tmp/palddd-opt/round-<ROUND>
```

模式：

| 模式 | 可写位置 | 允许 | 禁止 |
|---|---|---|---|
| SCAN | `AUDIT_DIR` | 只读仓库、跑构建测试、写探针、写报告 | 改仓库文件、git 写操作、装新包 |
| FIX | `AUDIT_DIR`、选中文件、必要文档、台账 | 锁定测试、修实现、跑门禁、本地提交 | 推送、打 tag、发布、顺手重构 |
| LOOP | 同 FIX | 最多 3 次 SCAN 加 FIX | 无限循环、跳过台账、一次改多个家族 |
| REGRESS | `AUDIT_DIR` | 重跑历史锁定测试与门禁 | 新增发现、修改代码 |

执行纪律：

1. 不要复述提示词，不要先给计划等确认。从 §3 的第一步开始执行。
2. 需要用户裁决的事项写入报告，不中断。只有破坏性操作例外。
3. 预算到点立即收敛，写清未完成项。
4. 不要因为「这条可能不重要」杀死候选。重要性用可达面与影响判断，不用直觉。
5. 不要因为「已有测试覆盖」杀死候选。测试可能只调用不断言，先变异再判断。
6. 不要因为「文档这么写」杀死候选。文档可能失实，先跑命令。

FOCUS 决定深挖顺序，不减少覆盖。非 FOCUS 轴至少做一轮轻量扫描，否则下一轮容易在同一块地反复翻土。

上下文有限时按 §3 顺序执行，不要先通读全文再总结。只做一个分片时，只加载 §5 对应透镜与 §12.3 对应种子。

## 1. 产出与判据

一轮必须产出两个清单：

| 清单 | 内容 | 数量 |
|---|---|---|
| 已证实缺陷 | 有运行时证据或可复现静态证据的问题 | 不设上限，按严重度排序 |
| 未证实候选 | 具体、可证伪、但本轮未跑完的假设 | 按 §6 配额，不隐藏 |

合格判据：

1. 先跑基线门禁，再读源码。
2. 覆盖矩阵逐格填写，空白格要写原因。
3. 每个分片至少达到 §6 的候选配额，每个候选有位置、触发、预期、探针。
4. 至少 20 个运行时探针，或按预算比例。
5. 每条 P0/P1 有可重跑命令、退出码、首行输出、反事实四格。
6. 每条性能、内存、时延候选有单位成本、保留保证、测量路径、门槛、复活条件。
7. 被否决项引用原文，重复项为零。
8. 披露 Skip 数、排除面板、未跑项目、blocked 探针。

不合格：只报 5 条就收工；只查一个栈；只读代码不跑探针；把候选池藏起来；用 P3 凑数；用「建议进一步分析」收尾。

## 2. 铁律

1. SCAN 只写 `AUDIT_DIR`，不改仓库跟踪文件，不做 git 写操作。构建与测试产生的 bin/obj 不算改仓库文件。
2. 不改门禁、测试、快照、断言来让问题消失。
3. 不加 `NoWarn`、`SuppressMessage`、`pragma` 过关。不加无到期日的 Skip。
4. 不虚构 API、参数顺序、包版本、路径、行号、数字。查不到写「未找到」或 `[假设]`。
5. P0/P1 必须有运行时证据。只有静态证据的最高 P2，安全类硬证据除外（明文凭据、可直证的注入拼接），但仍需给出可利用路径与修复建议。E4 只能进候选池。
6. 不以牺牲正确性换性能。fencing、顺序、at-least-once、幂等、背压、AOT、零反射，破坏任一即禁止提出。
7. 不重复上报已被 ADR、`perf-ledger`、声明注释接受的项。重开必须引用原文并给出新证据。
8. 不删除「P定案 / 勘正 / 取舍 / 登记延后 / 暂不优化」注释。
9. 故障注入只在临时库、临时目录、临时容器里做，不碰真实数据。
10. 不凭 `IsAotCompatible=true` 或零警告声称 AOT 兼容。必须发布后运行。
11. 不凭「测试全绿」声称覆盖。必须披露 Skip 数、排除面板、未跑项目。
12. 配额是覆盖目标，不是编造目标。达不到就写实际数量与原因，禁止用低质量条目凑数。

## 3. 六步流程

### 第 0 步：基线（15 分钟）

```bash
cd <repo>
git rev-parse --short HEAD
git status --porcelain
git describe --tags --abbrev=0 --match 'v*' || true

dotnet build PalDDD.slnx -c Release -v q

for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' | sort); do
  echo "== $p =="
  dotnet test "$p" --no-build -c Release --verbosity minimal || echo "FAILED $p"
done

dotnet run scripts/gate-audit.cs -- --inventory
dotnet run scripts/count-audit.cs
dotnet run scripts/doc-consistency.cs
dotnet run scripts/secret-scan.cs
dotnet run scripts/vuln-scan.cs
dotnet run scripts/verify-conventions.cs -- --quick
```

按 FOCUS 追加：

```bash
dotnet run -c Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke
dotnet run scripts/ci-coverage.cs -- --local
dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj -c Release -r win-x64 -p:PublishAot=true
PALDDD_TEST_PG="Host=127.0.0.1;Port=5432;Database=palddd_probe;Username=postgres;Password=postgres" \
PALDDD_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=palddd_probe;User ID=root;Password=root" \
dotnet test test/PalDDD.Integration.Tests -- --treenode-filter "/*/*/DialectProbeTests/*"
```

写 `facts.md`：HEAD、工作树、基线 tag 及理由、构建警告与错误、测试总数/通过/失败/跳过、未跑面板、每个门禁退出码与首行、`gate-audit` 五态计数、环境（Docker、wslc、AOT 工具链）。基线红则停，先报告红基线，不带红基线继续审计。

### 第 1 步：攻击面清单（15 分钟）

不读实现细节，先填十张表。每张表至少 5 行，填不满写原因。

A. 公开入口

| 入口 | 类型 | 最小装配 | 默认路径 | 失败模式 |
|---|---|---|---|---|

覆盖每个 `Add*`、公开方法、HTTP 端点、hosted service、store、broker 消费者。

B. 状态机

| 组件 | 状态 | 迁移 | 触发 | 失败/取消/重试后的状态 |
|---|---|---|---|---|

至少覆盖 Outbox、Inbox、Saga、投影、检查点、租约、断线续跑。

C. 选项与默认值

| 选项 | 属性默认 | 解析后有效值 | 文档值 | 非法值行为 |
|---|---|---|---|---|

D. 栈与操作

| 操作 | Dapper | PalORM | EF | 差异 |
|---|---|---|---|---|

E. 方言与 SQL 特性

| 特性 | SQLite | PostgreSQL | MySQL | 差异 |
|---|---|---|---|---|

至少覆盖 NULL 排序、时间精度、RETURNING、upsert、锁、标识符大小写、参数上限、批量语法。

F. 路径矩阵

| 路径 | 成功 | 失败 | 取消 | 超时 | 重复 | 跳过 |
|---|---|---|---|---|---|---|
| 单条 | | | | | | |
| 批量 | | | | | | |

G. 错误路径

| 位置 | catch/retry/fallback | 分类 | 是否有上限 | 是否记录 | 是否可行动 |
|---|---|---|---|---|---|

H. 资源

| 资源 | 创建 | 释放 | 上限 | 失败时是否释放 | 是否可重入 |
|---|---|---|---|---|---|

I. 信号

| 路径 | EventId | 计数器 | Activity | 成功/失败/取消/跳过 |
|---|---|---|---|---|

J. 文档承诺

| 承诺原文 | 出处 | 最小验证命令 | 现有 oracle |
|---|---|---|---|

### 第 2 步：候选生成（45 分钟）

用 §5 的十四个透镜逐项扫描。每个候选写成固定句式：

```text
在 <入口/配置/栈/方言> 下，执行 <动作或序列>，预期 <承诺/不变量>，
实际可能 <错误状态/数据后果>，因为 <代码路径/缺失守卫/默认值>，
探针 <命令或步骤>。
```

负空间候选写成：

```text
<位置> 缺少 <守卫/上限/取消/指标/测试/迁移>，当 <条件> 时会 <后果>，探针 <步骤>。
```

候选门槛：有具体位置、有触发条件、有可观察后果、有探针。四条缺一，不计数。不要用「可能有问题」这种候选。

### 第 3 步：覆盖矩阵（10 分钟）

矩阵行是分片，列是透镜。每格填候选数量或 `N/A(理由)`。空白格视为未查。

| 分片 | L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 | L9 | L10 | L11 | L12 | L13 | L14 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| S1 基础与领域 | | | | | | | | | | | | | | |
| S2 CQRS 与序列化 | | | | | | | | | | | | | | |
| S3 DI 与 Hosting | | | | | | | | | | | | | | |
| S4 Dapper | | | | | | | | | | | | | | |
| S5 PalORM | | | | | | | | | | | | | | |
| S6 EF Core | | | | | | | | | | | | | | |
| S7 消息与一致性 | | | | | | | | | | | | | | |
| S8 Broker | | | | | | | | | | | | | | |
| S9 压缩 | | | | | | | | | | | | | | |
| S10 流式与 HTTP | | | | | | | | | | | | | | |
| S11 分析器与源生成 | | | | | | | | | | | | | | |
| S12 构建与门禁 | | | | | | | | | | | | | | |
| ARCH 仓库级 | | | | | | | | | | | | | | |

要求：

- 每个分片至少覆盖 8 个透镜，至少达到 §6 的每分片候选配额。
- ARCH 每轮必查，即使 FOCUS 很窄。
- 未查格写原因：预算不足、需要容器、无公开入口、已由门禁覆盖。
- 同一格多个候选写 ID 列表，不写「有」。

### 第 4 步：探针验证（50 分钟）

每个候选最多 5 分钟，1 到 2 分钟一个也算合格。超时记 `blocked`，写清阻塞原因与下一步。合法结果只有：

- `confirmed`：有命令、退出码、首行输出，能重复。
- `refuted`：观察到正确行为，写出为什么假设不成立。
- `blocked`：缺环境、缺权限、缺数据，写出解锁条件。
- `next`：更尖锐的下一个假设，必须可执行。

优先级：

1. P0/P1 候选先跑。
2. 同一探针能覆盖多个候选的，合并跑。
3. 跨栈、跨方言、批量路径优先，因为最容易暴露分叉。
4. 故障注入与并发优先于纯静态阅读。
5. 需要真实 broker 或真库而本机不具备时，写方向判断，不猜数值。

### 第 5 步：家族扩散（15 分钟）

每确认一条，至少扩散三个方向：

| 方向 | 查什么 | 记录 |
|---|---|---|
| 入口族 | 兄弟 Add*、重载、配置 | 查过谁，没查谁，为什么 |
| 路径族 | 单条/批量、同步/异步、默认/opt-in | 差异是否改变语义 |
| 栈族 | Dapper/PalORM/EF、SQLite/PG/MySQL | 哪些确认，哪些未确认 |
| 数据族 | 旧行、NULL、重复、超大、混合版本 | 首轮扫描行为 |
| 信号族 | 日志、EventId、计数器、Activity、健康检查 | 哪条路径没信号 |

每个 confirmed 写一行「家族边界」：查过哪些兄弟，哪些没查，为什么。没有家族边界的 confirmed 不算完成。

### 第 6 步：报告与台账（15 分钟）

按 §9 写报告，按 §11 更新台账，写 `next-round.md` 三个假设。

预算不足时按比例压缩，静态阅读不超过总预算的 20%，优先保候选生成与探针。分片明显不适用某透镜时写 `N/A(理由)`，不要硬凑候选。

## 4. 分片

| 分片 | 范围 |
|---|---|
| S1 基础与领域 | `PalDDD.Base`、`PalDDD.Core`、`PalDDD.Shared`、`PalDDD.Extension` |
| S2 CQRS 与序列化 | `PalDDD.CQRS`、`PalDDD.Serialization*` |
| S3 DI 与 Hosting | `PalDDD.DependencyInjection`、`PalDDD.Hosting.AspNetCore*` |
| S4 Dapper | `PalDDD.Dapper*` |
| S5 PalORM | `PalDDD.PalORM*` |
| S6 EF Core | `PalDDD.Repository.EFCore`、`PalDDD.*.EFCore` |
| S7 消息与一致性 | `PalDDD.Messaging`、`PalDDD.Idempotency`、`PalDDD.EventLog`、`PalDDD.Projections*`、`PalDDD.Transactions*` |
| S8 Broker | `PalDDD.Messaging.RabbitMQ`、`PalDDD.Messaging.Kafka` |
| S9 压缩 | `PalDDD.Compression*` |
| S10 流式与 HTTP | `PalDDD.Hosting.AspNetCore` 的流式面 |
| S11 分析器与源生成 | `PalDDD.Analyzers*`、`PalDDD.Core.SourceGen` |
| S12 构建与门禁 | `scripts/`、`bench/`、`.githooks/`、`.github/`、`samples/` |
| ARCH 仓库级 | 全部 |

## 5. 十四个发现透镜

### L1 契约

每个公开类型的每个公开成员逐个问：前置条件是什么；空值、空集合、负数、超长输入怎么处理；单位和范围是什么；默认值是什么；失败抛什么异常；是否线程安全；是否可重入；是否支持取消；资源由谁释放；返回值是否可能为 null；文档与实现是否一致。不允许抽样。

### L2 状态机

列出每个组件的状态与迁移。对每个迁移问：成功路径；第 N 步失败；取消；超时；重试；重复投递；旧 token；进程崩溃；重启恢复；部分提交；并发双写。每个组合至少一个候选。

### L3 并发

双实例同时抢租；租约过期后旧 token 继续写；长 handler 超过租约；消费者并发；生产者快于消费者；乱序；重复；共享静态状态；锁内 await；连接池耗尽；线程池饥饿；取消与关闭竞速。

### L4 部分失败与幂等

批量 N 条第 k 条失败；已成功项是否回滚；检查点是否前移；重复消息 ID；同载荷不同 ID；重试后副作用；死信；毒消息；取消时已处理项；事务边界；先写数据后写检查点还是反过来。

### L5 时间

`DateTime` 与 `DateTimeOffset`；UTC 与本地；DST；`MinValue` 与 `MaxValue`；MySQL `DATETIME(6)`；SQLite 文本时间；PostgreSQL timestamp；时钟回拨；长耗时；超时精度；到期扫描边界；租约续期与时钟漂移。

### L6 边界

0、1、max、max-1、负数、重复、空串、空白、超长、超大、Unicode、特殊字符、null、默认结构体、单元素集合、空集合。

### L7 错误路径

每个 catch 是否分类；每个重试是否有上限与退避；每个 fallback 是否记日志与指标；每个 `return null` 或 `return false` 是否有调用方处理；异常消息是否点名键与下一步；取消是否被吞；毒消息是否隔离；错误是否可重试。

### L8 跨栈与方言

同一场景在 Dapper、PalORM、EF；SQLite、PostgreSQL、MySQL；单条与批量；同步与异步；默认与 opt-in。比较结果、异常、计数、时间、顺序、空集、重复、并发行为。

### L9 资源与内存

每操作分配；静态缓存；无界字典、Channel、List、环形缓冲；`ArrayPool` 归还、清零、双归还、池上限；闭包捕获；装箱；LINQ 迭代器；LOH；订阅未退订；Timer 未释放；Scope 未释放；大对象驻留。

### L10 性能与时延

每消息往返、语句、await、锁、序列化、拷贝；N+1；循环内查询；逐条标记；批处理延迟；flush 频率；p50/p95/p99；同步阻塞；队头阻塞；共享单读者；连接池；启动成本。

### L11 安全

不可信输入到 SQL、标识符、文件、进程、反序列化、日志、HTTP；类型白名单；解压炸弹；无界输入；凭据与 PII；依赖漏洞；不安全默认；错误信息泄露；日志注入。

### L12 测试、门禁与文档

每个测试是否只调用不断言；断言是否恒真；是否只测 happy path；Skip 原因与到期日；门禁在空输入、坏输入、变异下是否红；文档命令是否可执行；示例是否编译；计数是否一致；测试是否依赖顺序或共享状态。

### L13 架构与演进

依赖方向；抽象是否引用 provider；公共 API 是否破坏实现者；DIM 新增；AOT 与裁剪；反射例外；注册门面；能力回落；三栈对称；升级与回滚；旧数据；混合版本。

### L14 负空间

什么应该存在但不存在：公开方法缺取消；循环缺上限；外部调用缺超时；catch 缺分类；选项缺校验；状态迁移缺测试；路径缺指标；能力缺回落；Store 缺批量；方言缺用例；公共 API 缺快照；新诊断缺文档；新配置缺迁移说明；新路径缺 AOT 测试。

## 6. 配额

配额是覆盖目标，不是编造目标。达不到写实际数量与原因。

| 预算 | 候选总数 | 运行时探针 | 每分片候选 | 说明 |
|---|---|---|---|---|
| 60 分钟 | ≥25 | ≥8 | ≥3 | 保底 |
| 120 分钟 | ≥40 | ≥12 | ≥4 | 标准 |
| 180 分钟 | ≥60 | ≥20 | ≥5 | 推荐 |
| 240 分钟 | ≥100 | ≥30 | ≥8 | 深挖 |

配额是下限，不是上限。一个探针能覆盖多个候选时合并跑，候选总数按覆盖后的条目计。

额外要求：

- 至少 6 条负空间候选。
- 至少 4 条测试或门禁假绿候选。
- 至少 3 条消费侧实验：最小装配、AOT 发布、升级。
- 至少 2 条跨栈或跨方言差分探针。
- 每个租约或队列组件至少 1 条并发调度探针。
- 每个状态机至少 1 条故障注入探针。

## 7. 候选与证据格式

候选台账：

```text
CID | 分片 | 透镜 | 位置 | 触发 | 预期 | 怀疑后果 | 证据状态 | 探针 | 影响 | 备注
```

已证实缺陷：

```text
FID | 轴 | 级别 | 声明 | 触发 | 命令 | 退出码 | 首行输出 | 位置 | E级 | 可达消费者 | 反事实 | 防线缺口 | 锁定测试 | 家族边界 | 修复方向
```

证据强度：

| 级别 | 含义 | 用途 |
|---|---|---|
| E1 | 运行复现，错误状态或错误数据 | 可进 P0/P1 |
| E2 | 运行观察，符合或不符合文档 | 可进 P0/P1 |
| E3 | 静态证据加现有测试、门禁、快照 | 最高 P2 |
| E4 | 仅阅读 | 只能进候选池 |
| E5 | 猜测 | 只能进候选池，不得进已证实清单 |

严重度先定两个轴：可达面（全部消费者、单栈、单方言、仅自定义实现、仅 AOT）与可检测性（响亮、可观测、静默）。

| 级别 | 判据 |
|---|---|
| P0 | 默认配置加全部消费者加静默错语义或数据风险；安全漏洞；AOT 发布或运行失败 |
| P1 | 文档路径启动或首用失败；文档保证的行为静默缺失；升级后旧数据首轮错误；门禁在坏输入下不红 |
| P2 | 契约或文档漂移；可观测性缺失；未测边界；能力回落无信号；无本会话量具的性能候选 |
| P3 | 单列附录，不混进 P0 到 P2 |

P0/P1 必须写反事实四格：

1. 哪个消费者。
2. 什么配置或调用顺序。
3. 执行什么动作。
4. 看到什么错误结果。

再写三行：

- 什么证据会推翻它。
- 现有防线为什么没挡住。
- 修复后用什么测试锁定。

## 8. 探针

规则：每个不超过 5 分钟；输出 confirmed、refuted、blocked 或 next；命令以仓库根开头；记录退出码与首行；不用 tail 或 head 吞退出码。

最小探针工程：`net11.0` 控制台，`ManagePackageVersionsCentrally=false`，`ProjectReference` 指向目标项目，包版本从 `Directory.Packages.props` 读。

```csharp
sealed class Cap<T> : IPalLogger<T>
{
    public List<(int EventId, string Message)> Rows { get; } = [];
    public void Debug(string m) => Rows.Add((0, m));
    public void Information(string m) => Rows.Add((0, m));
    public void Warning(string m) => Rows.Add((0, m));
    public void Error(Exception e, string m) => Rows.Add((0, m));
    public bool IsEnabled(LogLevel level) => true;
    public void Debug(EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));
    public void Information(EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));
    public void Warning(EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));
    public void Error(Exception e, EventId id, string m, params object?[] a) => Rows.Add((id.Id, m));
}

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
```

再补一个固定 `IOptionsMonitor<T>` 和一个转发全部方法的 store 包装器。store 包装器按需在第 N 次调用抛异常或返回部分命中。

变异模式二选一，开工时声明：

- 隔离变异：复制 `src` 到 `AUDIT_DIR/mirror`，排除 bin、obj、.git，在镜像里改坏实现并跑测试。
- 只读替代：不改源码，用 `gate-audit` 等隔离探针承担变异证据。报告写「代码变异 0 条，替代证据 N 项」。

不得混用后声称「变异过」。

## 9. 报告

章节：

1. **运行事实**：HEAD、工作树、基线 tag、构建、测试（总数/通过/失败/跳过/未跑面板）、门禁矩阵、模式、变异模式、探针完成数、耗时、静态阅读时长。
2. **结论摘要**：不超过 15 行。Top 5 发现、一句话边界声明、与上一轮的新增与关闭。
3. **已证实缺陷**：不设条数上限，按严重度排序。每条含 §7 字段。
4. **未证实候选**：完整候选台账，按分片与透镜排序。每条写清下一步探针。禁止隐藏。
5. **覆盖与配额**：分片乘透镜矩阵、实际候选数、探针数、未达标项与原因。
6. **探针台账**：confirmed、refuted、blocked、next，附原始输出与家族边界。
7. **架构与契约债务**：跨栈矩阵、方言矩阵、公共 API、AOT、反射例外、默认值对账。
8. **性能、内存与时延候选**：七字段表，标「本会话量测」或「未量测」。
9. **未完成与不确定**：discarded、blocked、re-affirmed（引原文）、需裁决、下一轮三个假设、什么会改变本轮结论。

另外落 `findings.tsv`、`candidates.tsv`、`next-round.md`。

报告必须写「本轮新增」与「上轮已关闭」两行。没有对照，无法判断是在进步还是在原地打转。

## 10. 修复轮

选择规则：

- 按 `严重度 × 可达面 × 可检测性 / 修复成本` 排序。
- 每轮最多 `FIX_LIMIT` 条，默认 5 条。
- 优先修一个家族，不修五个实例。
- P0/P1 先于性能候选。性能候选先量测后动手。
- 需要产品决策的写「需裁决」，不擅自改语义。

每条修复：

1. 写锁定测试，确认修复前是红的，且红的原因正确。
2. 只改必要实现，保持现有风格。
3. 跑锁定测试转绿。
4. 反向验证：移除修复因子，确认重新变红，再恢复。
5. 跑目标项目测试，再跑全量测试循环。
6. 跑构建、`gate-audit`、`count-audit`、`doc-consistency`、`encoding-gate`、`guard`。
7. 公共 API、行为、配置、命令变更，同一提交更新文档、快照、`CHANGELOG.md`。
8. 本地提交，格式 `类型：描述`。类型用 `修复`、`测试`、`文档`、`构建`、`决策`、`卫生`。
9. 更新台账。性能否决另写 `.ai/perf-ledger.md`，该目录是独立 git 库，单独提交。
10. 不推送、不打 tag、不发布。

约束：

- 一次只改一个变量。禁止顺手重构、顺手优化、顺手改命名。
- 超过 3 个文件，或引入新的外部行为假设，先写 10 行设计说明。
- 修复尝试 3 次仍失败，停止并报告，禁止用放宽断言或捕获异常收场。
- 只改测试不改源码时用 `ALLOW_TEST_ONLY_CHANGE=1`，并说明原因。
- 新增 Skip 需要 `ALLOW_NEW_SKIP=1`，并写到到期日与理由。
- 公共 API 快照与 `CHANGELOG.md` 必须逐提交耦合。
- 新文件默认 LF，仓库要求 CRLF，写完做字节级校验。
- Git Bash 用 `-p:`，不用 `/p:`。
- 门禁可能用陈旧的 Release 二进制，先 `dotnet build -c Release`。

## 11. 台账与停止

台账：`docs/review/optimization-ledger.md`。SCAN 轮先在 `AUDIT_DIR` 起草，FIX 轮落入仓库。

字段：

```text
ID | 轴 | 位置 | 发现 | 状态 | 证据 | 决策来源 | 锁定测试 | 轮次 | 复活条件
```

状态：`fixed`、`rejected`、`deferred`、`accepted-risk`、`candidate`、`needs-decision`。

规则：

- 开工先读，收工必写。只追加，不删除。
- 已修复项保留锁定测试名称，回归轮重跑。
- 已否决项引用原文。没有原文的否决无效。
- 延后项有可观测复活条件与到期日。
- 同一家族只占一个 ID，兄弟实例写在家族边界里。
- 台账是指针，不是第二真源。性能否决的权威记录在 `.ai/perf-ledger.md`，架构决策在 `docs/decisions/`。

停止条件：

1. 连续两轮发现全部属于已知家族复发，停止人肉循环，把该家族做成门禁或脚本。
2. 一轮没有新的 P0/P1，且 P2 都已登记，转维护节奏。
3. 预算耗尽，交部分报告，明确未完成项，不宣布完成。
4. 宣布「全部完成」必须逐条对照任务清单。
5. 停止条件与用户指令冲突时，显式呈报。

## 12. 本仓适配

### 12.1 命令

| 目的 | 命令 |
|---|---|
| 构建 | `dotnet build PalDDD.slnx -c Release -v q` |
| 测试 | 逐项目 `dotnet test <csproj> --no-build -c Release --verbosity minimal` |
| 门禁矩阵 | `dotnet run scripts/gate-audit.cs` 或 `-- --inventory` |
| 计数 | `dotnet run scripts/count-audit.cs` |
| 文档 | `dotnet run scripts/doc-consistency.cs` |
| 规范 | `dotnet run scripts/verify-conventions.cs -- --quick` |
| 凭据 | `dotnet run scripts/secret-scan.cs` |
| 漏洞 | `dotnet run scripts/vuln-scan.cs` |
| 覆盖率 | `dotnet run scripts/ci-coverage.cs -- --local` |
| 性能烟测 | `dotnet run -c Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke` |
| 性能编排 | `dotnet run scripts/eps-run.cs -- --quick` |
| AOT | `dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj -c Release -r win-x64 -p:PublishAot=true` |

基准车道按假设选：`--framework`、`--app`、`--infra`、`--concurrency`、`--persist-pg`、`--persist-mysql`、`--stream`、`--saga`、`--compression`、`--delivery-pg`、`--delivery-mysql`。不要一轮跑全部。

### 12.2 陷阱

| 坑 | 规避 |
|---|---|
| tail 或 head 吞退出码 | 用 `set -o pipefail` 或 `${PIPESTATUS[0]}` |
| Git Bash 把 `/p:` 当路径 | 用 `-p:` |
| 新文件默认 LF | 写完校验 CRLF |
| `sed -i`、heredoc 被守卫拦 | 用 Write 或 Edit |
| MTP 批量握手失败 | 逐项目跑，单项目挂起改用测试 exe |
| Skip 数不披露 | 报告写跳过数、排除面板、未跑项目 |
| EF InMemory 的 Dispose 等于 Close | 不要用它造断连探针 |
| SQLite `:memory:` 每连接独立 | 复用连接或共享缓存 |
| `grep -rl` 返回反斜杠路径 | 用 `find` 加 `xargs grep` |
| `grep -E '^+++'` 报错 | 用 `grep -F '+++'` |
| 日志含 GBK | 用 `grep -a`，断言匹配 ASCII 名称 |
| dev 上 `git describe` 解析旧 tag | 显式选基线并写理由 |
| `nupkgs/` 是旧产物 | 不要引用 |
| 测试宿主订阅 ActivitySource | 零分配断言放基准进程 |
| pre-commit 用陈旧 Release 二进制 | 先 `dotnet build -c Release` |
| wslc 地址写 localhost | 用 `127.0.0.1` |

### 12.3 高价值猎场

先查这些家族。它们是历史高发点，不是当前缺陷结论。每个都要重新验证，不要复制历史结论。

| # | 种子 | 具体动作 | 常见发现 |
|---|---|---|---|
| 1 | 注册门面 | 每个 `Add*` 只配 `AddLogging()` 独立装配，解析全部 hosted 与 scoped | 缺门面依赖、错误不点名类型、重复注册 |
| 2 | 批量对单条 | 同场景跑单条与批量，比较成功、失败、取消、跳过 | 告警缺失、计数错、检查点错位、结果语义不同 |
| 3 | 能力回落 | 枚举能力接口乘实现者，移除能力后观察日志与指标 | 静默走低效路径、无信号、文档承诺未生效 |
| 4 | 三栈契约 | 同场景跑 Dapper、PalORM、EF | 异常类型、空集、重复、并发、时间语义不一致 |
| 5 | 三方言 | 同 SQL 跑 SQLite、PG、MySQL | NULL 排序、时间精度、RETURNING、upsert、标识符大小写 |
| 6 | 默认值 | 属性默认、解析值、文档值三方对账 | 默认值漂移、非法值静默接受 |
| 7 | 域事件到 Outbox | 每个栈查事务、拦截器、独立上下文 | 事件丢失、事务不共享、桥接只在部分栈 |
| 8 | 租约与 fencing | 过期、旧 token、双 worker、长 handler、取消 | 旧 token 生效、重复处理、丢消息 |
| 9 | 时间 | Min/Max、UTC/本地、DST、MySQL 精度、时钟回拨 | 哨兵溢出、时区错、边界扫描漏数据 |
| 10 | 序列化与 AOT | 未知类型、缺字段、多字段、超大载荷、解压炸弹 | 类型白名单缺失、内存耗尽、AOT 运行期失败 |
| 11 | 池与缓冲 | 归还、清零、双归还、池满、溢出、释放 | 脏数据、泄漏、反复分配 |
| 12 | 可观测性 | 单条/批量乘成功/失败/取消/跳过 | EventId 缺失、计数与数据库真相不一致 |
| 13 | 测试与门禁 | 空输入、坏输入、变异、只调用不断言、Skip | 门禁假绿、断言恒真、覆盖缺口 |
| 14 | 文档 | 逐条执行命令、编译示例、核对计数 | 命令失效、示例缺步骤、计数漂移 |
| 15 | 升级与回滚 | 旧数据、旧 schema、混合版本、部分迁移、开关关闭 | 首轮扫描报错、旧数据被跳过、回滚失败 |
| 16 | 消费侧 | 最小应用、AOT 发布、包升级、错误配置 | 文档路径不可用、错误信息不可行动 |
| 17 | DI 与启动 | 非法配置、缺依赖、重复注册、释放 | 启动失败不点名、资源泄漏、顺序依赖 |
| 18 | 关闭与取消 | 启动中取消、停止时在途、双 Dispose | 挂起、丢消息、连接泄漏 |
| 19 | SQL 标识符 | 表名、列名、排序方向、LIMIT 来自配置或输入 | 注入面、引号缺失、方言不兼容 |
| 20 | 日志与 PII | 连接串、密码、token、原始头、异常消息 | 明文泄漏、日志注入、遥测含 PII |
| 21 | 保留与回填 | 清理、归档、回填、删除活跃数据 | 误删、回填覆盖新数据、检查点错位 |
| 22 | 顺序 | 批内重排、检查点提交、并发写 | 后写覆盖先写、检查点先于数据 |
| 23 | 错误分类 | 瞬时、永久、毒消息、死锁、超时、取消 | 永久错误重试、瞬时错误丢弃、取消被吞 |
| 24 | 公共 API | DIM、Obsolete、可空、快照、CHANGELOG | 破坏实现者、快照未更新、迁移说明缺失 |

### 12.4 历史高发家族

已修复的实例不要重复上报，但必须查兄弟：

- 组件级 `Add*` 漏日志门面，历史上 PG 通知器与 Outbox、Inbox、Saga 组件注册各出现一次。
- Inbox 批量路径告警与单条不一致，历史出现 1004、1005、1006 与 pending span 缺口。
- 能力接口回落无信号，批量 store 与可恢复事件源是重点。
- 三栈契约分叉，Dapper、PalORM、EF 的异常、空集、重复、并发、时间语义。
- 默认值三方漂移。
- 域事件到 Outbox 的链路只在部分栈成立。
- 租约 fencing 与时间哨兵。
- SQL 字符串内嵌 `--` 导致谓词自噬。
- 池化归还与双归还。
- 同步阻塞与线程池饥饿。
- 门禁假绿，空输入、只调用不断言、tail 吞退出码。

## 13. 反模式

1. 只报 5 条就收工。
2. 只查一个栈或一个方言。
3. 只读代码，不跑探针。
4. 把未证实候选藏起来，只交已证实清单。
5. 用 P3 凑数，把清单拉长。
6. 用「可能有问题」当候选，没有触发与探针。
7. 用「已有测试」当覆盖证据。
8. 用「文档这么写」当行为证据。
9. 为了凑配额编造条目。
10. 只测 happy path。
11. 只查默认配置，不查 opt-in、批量、取消、失败。
12. 把 accepted-gap 当新缺陷。
13. 用「需要前提」软化反事实。
14. 用「建议进一步分析」收尾。
15. 为了变绿放宽断言、加 Skip、加 NoWarn。
16. 一次改多个变量，顺手重构。
17. 在 Git Bash 用 `/p:`，用 tail 吞退出码，用陈旧二进制。
18. 没发布没运行就声称 AOT 兼容，没披露跳过就声称全绿。

## 14. 预算不足时

预算低于 60 分钟时，只做三件事：

1. 每个公开 `Add*` 独立装配，加能力接口乘实现者矩阵。
2. 单条与批量路径的成功、失败、取消、跳过对照。
3. 三条主流程的单位成本走查：命令分发、Outbox 发布、Inbox 消费。

只报 confirmed，候选全部写入台账，写清下一轮探针。不要为了报告好看把候选写成缺陷。
