# Pal.DDD 持续优化提示词（v1，2026-10-22）

> 直接使用：把全文粘贴给具备仓库读写能力的 AI，先在 §0 填写参数，再从 §4 开始执行。只粘贴参数不粘贴全文，等于没有约束。
> 分工：`docs/design/contract-audit-prompt.md` 查文档承诺是否兑现；本提示词查系统是否更安全、更快、更省、更稳、更易演进。两者可交替使用，不要在同一轮混用判据。
> 适用：Pal.DDD 这类框架库。换成业务应用时替换 §13 的命令与猎场，保留 §1 至 §12 的判据。

## 0. 启动参数

```text
MODE      = SCAN | FIX | LOOP | REGRESS
ROUND     = <整数，从 1 开始>
FOCUS     = ALL | SECURITY | CORRECTNESS | PERF | MEMORY | LATENCY | ARCH | ROBUSTNESS
BUDGET    = <分钟，默认 120>
FIX_LIMIT = <默认 5>
AUDIT_DIR = /tmp/palddd-opt/round-<ROUND>
```

模式边界：

| 模式 | 可写位置 | 允许的动作 | 禁止的动作 |
|---|---|---|---|
| SCAN | `AUDIT_DIR` | 只读仓库、跑构建与测试、写探针、写报告 | 改仓库文件、git 写操作、安装新包 |
| FIX | `AUDIT_DIR`、选中文件、必要文档、台账 | 写锁定测试、修实现、跑门禁、本地提交 | 推送、打 tag、发布、顺手重构 |
| LOOP | 同 FIX | 最多 3 次 SCAN 加 FIX 循环 | 无限循环、跳过台账、一次改多个家族 |
| REGRESS | `AUDIT_DIR` | 重跑历史锁定测试与门禁、对照基线 | 新增发现、修改代码 |

上下文有限时不要一次吞全文。把本文当参考手册：按 §6 选一个分片，只加载 §2、§3、§7 对应轴、§8、§9 与该分片；上下文充足或 LOOP 模式按 §6 的覆盖矩阵执行。一次读完再总结，通常会退化成通用建议。

五条主线：先跑门禁，再读台账，用探针证明，修家族而非实例，把复发家族做成门禁。

执行纪律：

1. 不要复述本提示词，不要先给计划等确认。本提示词就是授权，从 §4 的第一条命令开始。
2. 需要用户裁决的事项写入报告「需裁决」节，不要中断执行。只有 P0 #5 破坏性操作例外。
3. 预算到点立即收敛，报告未完成项，不补写想象结论。
4. 同一轮只允许一个主攻方向。发现跨轴问题时，选影响最大的轴登记，其余写入候选池。

## 1. 成功判据

一轮合格的产出必须同时满足：

1. 先跑门禁与基线，再读源码。基线未记录就下的结论不计。
2. 至少 1 条带运行时复现的新发现；如果确实没有，提交探针台账与覆盖矩阵，证明查过而不是没查。
3. 每条 P0/P1 有可重跑命令、退出码与首行输出，能回答「哪个消费者、什么配置、做什么、看到什么」。
4. 每条性能、内存、时延候选有单位成本、保留的保证、测量路径、判定门槛、复活条件。
5. 每个修复有先红后绿的锁定测试，且做过反向验证（移除修复因子后重新变红）。
6. 台账已更新，重复项为零，被否决项引用了原文。
7. 报告披露跳过数、排除面板、未验证区域、blocked 探针。

以下不算成功：报告很长；P3 很多；建议很通用；测试全绿但没有变异或探针；只读代码就宣称没有问题。

## 2. 六轴与问题门槛

| 轴 | 可进主清单的问题 | 不进主清单的内容 |
|---|---|---|
| 安全 | 不可信输入到达 SQL、文件、进程、反序列化、日志等汇聚点且缺少校验；凭据或 PII 泄漏；解压炸弹；无界输入导致资源耗尽；依赖漏洞或许可违规；不安全的默认值 | 库本身不承担的认证授权；没有可达路径的假设攻击；纯理论风险 |
| 正确性 | 指定配置下结果错、状态错、数据丢、重复、乱序、静默回落、错误分类错、升级后旧数据错 | 风格与命名；文档已声明的取舍；消费者误用；个人偏好 |
| 性能 | 有调用路径与单位成本，且超过预算、明显多往返、多分配、多扫描、多锁 | 没有量具的墙钟数字；低于门槛的微优化；只凭直觉的缓存建议 |
| 内存 | 每操作分配超过契约；驻留无界增长；池化误用；LOH 或 Gen2 压力；闭包与装箱在热路径 | 一次性启动分配；测试宿主或基准替身自身的分配 |
| 时延 | 同步阻塞；队头阻塞；共享资源串行化；多余跳数；刷新过频；批处理延迟违反已声明 SLO | 只提升吞吐却说不清延迟代价；网络地板以下的理论优化 |
| 架构与健壮性 | 边界违例；跨栈契约分叉；组件注册不独立；能力回落无信号；AOT 或零反射红线被突破；生命周期泄漏；故障不隔离；可观测性缺口 | 「可以更抽象」；大规模重写；按行数判断质量；没有消费者的扩展点 |

发现要过四道门：

1. 可达性：真实消费者与配置能触发。
2. 新颖性：现有门禁、测试、ADR、台账没有覆盖。被覆盖的，除非证明防线本身假绿，否则不报。
3. 可证伪性：一条命令能看到成立或不成立。
4. 影响：数据、安全、文档承诺、预算、SLO、架构侵蚀至少命中一项。

四门缺一，降为候选池条目，不进缺陷清单。

## 3. 红线

1. SCAN 模式只写 `AUDIT_DIR`。不改仓库文件，不做 git 写操作。
2. 不改门禁、测试、快照、断言来让问题消失。修因，不修绿。
3. 不加 `NoWarn`、`SuppressMessage`、`pragma` 过关。不加无到期日的 Skip。本仓 `new-skip-guard` 会拦，绕过等于审计造假。
4. 不虚构 API、参数顺序、包版本、文件路径、行号、数字。查不到就写「未找到」或 `[未验证]`。
5. 不以牺牲正确性换性能。fencing、顺序、at-least-once、幂等、背压、AOT、零反射，破坏任一即禁止提出。
6. 不重复上报已被 ADR、`perf-ledger`、代码声明注释接受的项。要重开必须引用原文并给出新证据。
7. 不删除「P定案 / 勘正 / 取舍 / 登记延后 / 暂不优化」注释。命中先写决策文档回应，再改代码。
8. 不做 P0 #5 破坏性操作。需要时把完整命令交给用户确认。
9. 不凭 `IsAotCompatible=true` 或零警告声称 AOT 兼容。必须 `PublishAot` 后运行二进制。
10. 不凭「测试全绿」声称覆盖。必须披露 Skip 数、排除面板、未跑项目。
11. 不把 P3 凑进主清单。主清单最多 12 条，同族合并，其余进候选池。
12. 不一次改多个变量。修复轮一次只验证一个因子。

## 4. 阶段 0：事实底座（15 分钟，不可跳）

先跑，后读。所有命令以仓库根为工作目录，记录退出码与首行输出。禁止用 `| tail` 或 `| head` 作为最后一段吞掉退出码。

```bash
cd <repo>
git rev-parse --short HEAD
git status --porcelain
git describe --tags --abbrev=0 --match 'v*' || true
git tag --sort=-v:refname | head -5

dotnet build PalDDD.slnx -c Release -v q
```

测试逐项目跑，不依赖 slnx 批量（MTP 批量握手可能 exit 5）：

```bash
for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' | sort); do
  echo "== $p =="
  dotnet test "$p" --no-build -c Release --verbosity minimal || echo "FAILED $p"
done
```

单个项目超过 10 分钟未结束，终止并记 `blocked`，改用该项目的测试 exe 直跑。不要用 `dotnet test <单个项目>` 反复撞挂起。

快速门禁：

```bash
dotnet run scripts/gate-audit.cs -- --inventory
dotnet run scripts/count-audit.cs
dotnet run scripts/doc-consistency.cs
dotnet run scripts/secret-scan.cs
dotnet run scripts/vuln-scan.cs
dotnet run scripts/verify-conventions.cs -- --quick
```

按 FOCUS 追加：

```bash
# 性能与内存
dotnet run --configuration Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke
dotnet run scripts/eps-run.cs -- --quick

# 覆盖率
dotnet run scripts/ci-coverage.cs -- --local

# AOT（本机无工具链就记 blocked，不得声称通过）
dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj -c Release -r win-x64 -p:PublishAot=true

# 安全深挖（本机有 Semgrep 则按 .github/workflows/ci.yml 的 semgrep job 复跑；没有就记 blocked）

# 真库方言（容器未起先看 docs/development.md 的 wslc 段）
PALDDD_TEST_PG="Host=127.0.0.1;Port=5432;Database=palddd_probe;Username=postgres;Password=postgres" \
PALDDD_TEST_MYSQL="Server=127.0.0.1;Port=3306;Database=palddd_probe;User ID=root;Password=root" \
dotnet test test/PalDDD.Integration.Tests -- --treenode-filter "/*/*/DialectProbeTests/*"
```

写入 `facts.md`：

- HEAD、工作树是否干净、所选基线 tag 及理由。
- 构建警告数与错误数。
- 测试总数、通过、失败、跳过，逐项目失败清单，未跑面板及原因。
- 每个门禁的退出码与首行输出；`gate-audit` 的五态矩阵计数。
- 与上一轮台账相比偏差超过 10% 的项，标明是口径差异还是真变化。
- 环境：OS、SDK 版本、是否具备 Docker、wslc、AOT 工具链。

基线红则停。不要带着红基线继续审计，否则后续失败无法归因。基线红本身作为第一条 P0 候选记录。

## 5. 阶段 1：历史对账（10 分钟）

按顺序读，读完再碰源码：

1. `.ai/perf-ledger.md` 四张表，重点是表 2「明确不做」与表 4 候选状态。
2. `perf-budgets.md` 的分配预算与阈值。
3. `docs/decisions/` 中与 FOCUS 相关的 ADR，至少读索引与最近三份。
4. `docs/review/` 最近两轮报告与 `action-items-*`。
5. `CHANGELOG.md` 最近两个版本。
6. 代码中的声明注释：

```bash
grep -rn -a -E "P定案|勘正|取舍|登记延后|暂不优化|accepted-gap|REVIEWED" src test --include='*.cs' | head -200 || true
```

输出 `reaffirmed.md`：

| 项 | 来源（文件:行） | 原文摘录 | 本轮是否重开 | 新证据 |
|---|---|---|---|---|

规则：

- 已接受项默认 re-affirmed，不计缺陷，不占清单名额。
- 重开必须说明「自决策以来发生了什么变化」：新版本、新消费者、新量测、新代码路径、新故障。
- 找不到新证据就停止。不要用「但是我认为」重开。

## 6. 阶段 2：分片与覆盖矩阵（15 分钟）

按能力面分片，不按项目目录分片。跨项目的问题在项目分片里结构性看不见。

| 分片 | 范围 | 本轮必答 |
|---|---|---|
| S1 基础与领域 | `PalDDD.Base`、`PalDDD.Core`、`PalDDD.Shared`、`PalDDD.Extension` | 实体、值对象、SmartEnum、验证、领域事件、规格的边界与分配 |
| S2 CQRS 与序列化 | `PalDDD.CQRS`、`PalDDD.Serialization*` | 分发链、管道行为、序列化目录、演进与 MemoryPack 分支 |
| S3 DI 与 Hosting | `PalDDD.DependencyInjection`、`PalDDD.Hosting.AspNetCore*` | 每个 Add* 独立装配、选项校验、启动停止、健康检查 |
| S4 Dapper 栈 | `PalDDD.Dapper*` | SQL 模板、参数绑定、方言分支、批量路径、AOT 拦截器 |
| S5 PalORM 栈 | `PalDDD.PalORM*` | 生成 SQL、会话、方言分支、批量与 RETURNING |
| S6 EF Core 栈 | `PalDDD.Repository.EFCore`、`PalDDD.*.EFCore` | DbContext、拦截器、模型配置、查询翻译、迁移 |
| S7 消息与一致性 | `PalDDD.Messaging`、`PalDDD.Idempotency`、`PalDDD.EventLog`、`PalDDD.Projections*`、`PalDDD.Transactions*` | Outbox、Inbox、Saga、投影、检查点、租约、保留清理 |
| S8 Broker | `PalDDD.Messaging.RabbitMQ`、`PalDDD.Messaging.Kafka` | 头、确认、重投、幂等、消费并发、关闭排空 |
| S9 压缩 | `PalDDD.Compression*` | 池化、解压上限、算法分支、原生绑定 |
| S10 流式与 HTTP | `PalDDD.Hosting.AspNetCore` 的流式面 | SSE、流式查询、断线续跑、心跳、背压、取消 |
| S11 分析器与源生成 | `PalDDD.Analyzers*`、`PalDDD.Core.SourceGen` | 诊断覆盖、代码修复、分析器自身分配、AOT 安全 |
| S12 构建与门禁 | `scripts/`、`bench/`、`.githooks/`、`.github/`、`samples/` | 门禁假绿、CI 接线、基准有效性、脚本退出码 |
| ARCH 仓库级 | 全部 | 依赖方向、公共 API、AOT、跨栈对称、默认值、可观测性 |

覆盖矩阵必须逐格填写，不允许空白：

| 分片 | 安全 | 正确性 | 性能 | 内存 | 时延 | 架构与健壮性 |
|---|---|---|---|---|---|---|
| S1 | 已查(证据ID) / 未查(原因) / N/A(理由) | 同左 | 同左 | 同左 | 同左 | 同左 |

要求：

- FOCUS 指定的轴，所有在范围分片至少覆盖 3 个轴。
- `ARCH` 块每轮必跑，即使 FOCUS 很窄。
- 未查必须写原因，例如「需要 Docker，本机不具备」「本轮预算不足」「无公开入口」。
- 假设清单 10 条：至少 6 条必须运行时才能判，至少 2 条针对最近改动，至少 1 条针对门禁假绿。
- 最近改动：

```bash
BASE=$(git describe --tags --abbrev=0 --match 'v*')
git diff "$BASE"..HEAD --stat -- src test
```

基线 tag 在 dev 上可能解析到旧版本。解析结果与 `CHANGELOG.md` 最新版本不一致时，改用 main 上最近的可达 tag，并在 `facts.md` 写明理由。

输出 `matrix.md`，末尾写「阶段 2 完成/未完成」。

## 7. 阶段 3：检测方法库

### 7.1 安全

先画信任边界，再做数据流追踪。不要用通用 OWASP 清单代替追踪。

资产：事件载荷、连接串、租户数据、幂等键、Saga 状态、日志与遥测。
不可信来源：公开 API 入参、配置、数据库行、broker 消息、HTTP 请求与头、反序列化载荷。
汇聚点：SQL 文本、标识符拼接、文件路径、进程启动、反序列化、日志、HTTP 响应。

| 编号 | 检查 | 成立条件 |
|---|---|---|
| S1 | 从公开入口追踪不可信值到汇聚点 | 无校验、无参数化、无白名单即可到达 |
| S2 | 标识符注入：表名、列名、排序方向、LIMIT 来自配置或输入 | 引号或白名单缺失，构造探针能改变 SQL 结构 |
| S3 | 反序列化：类型解析、未知类型、嵌套深度、载荷上限 | 类型白名单缺失，或超大载荷可耗尽内存 |
| S4 | 解压炸弹：解压后大小与压缩比上限 | 只有压缩前校验，解压无上限 |
| S5 | 凭据与 PII：连接串、密码、token、原始头进入日志或异常 | 明文出现在日志、异常消息、遥测标签 |
| S6 | 资源耗尽：无界 Channel、缓冲、字典、批量、SQL 参数数量 | 输入可驱动无界增长，且无背压或上限 |
| S7 | 依赖与供应链：漏洞、许可、Actions 钉 SHA、传递依赖 | `vuln-scan` 或许可门禁红，或动作使用可变标签 |
| S8 | 不安全默认：TLS 校验、证书、认证、CORS、详细错误、raw SQL | 默认关闭安全能力且文档未声明 |

每条安全发现必须写出「输入从哪来，经过哪些方法，到达哪个汇聚点」。只有 grep 命中、没有可达路径的，进候选池。

### 7.2 正确性

| 编号 | 检查 | 成立条件 |
|---|---|---|
| C1 | 状态机枚举：每个组件的状态与迁移 | 某迁移在失败、取消、重试下进入错误状态 |
| C2 | 部分失败：批量 N 条，第 k 条失败 | 已成功的回滚、未成功的被标记成功、检查点错位 |
| C3 | 并发：双实例、租约过期、旧 token、重复投递、乱序 | 旧 token 生效、重复终态、丢失更新 |
| C4 | 时间：DateTime 与 DateTimeOffset、UTC、DST、哨兵、精度 | 时区错、哨兵溢出、MySQL 微秒截断、比较错 |
| C5 | 边界：0、1、max、max-1、负数、重复、空串、超长 | 越界、溢出、重复处理、空集误判 |
| C6 | 错误分类：瞬时、永久、毒消息、死锁、超时、取消 | 永久错误被重试、瞬时错误被丢弃、取消被吞 |
| C7 | 静默回落：能力接口未实现、批量不支持、方言不支持 | 回落无日志、无指标、无文档，行为与承诺不符 |
| C8 | 幂等：重复 ID、同载荷不同 ID、部分提交后重试 | 重复副作用、CAS 语义错、唯一约束误判 |
| C9 | 顺序：按键顺序、批内重排、检查点提交顺序 | 后写覆盖先写、检查点先于数据处理 |
| C10 | 升级兼容：旧行 NULL、旧 schema、旧枚举、旧载荷、混合版本 | 首轮扫描报错、静默跳过旧数据 |
| C11 | 跨栈差分：同场景跑 Dapper、PalORM、EF | 三栈结果、异常、计数、时间语义不一致 |
| C12 | 文档承诺：照文档最小路径执行 | 文档命令失败、默认值不符、示例缺步骤 |

### 7.3 并发、生命周期与健壮性

| 编号 | 检查 | 成立条件 |
|---|---|---|
| R1 | 每个公开 Add* 独立装配 | 文档路径启动或首用失败，错误不点名缺失类型 |
| R2 | 启动与停止：非法配置、启动中取消、停止时在途工作、双 Dispose | 挂起、丢消息、连接泄漏、状态错 |
| R3 | 取消：每个 await 路径尊重 token | 取消后继续写、部分状态、`OperationCanceledException` 被吞 |
| R4 | 故障注入：第 N 次调用抛、broker 不可用、DB 超时、序列化失败 | 无重试、无死信、检查点错、整批中止 |
| R5 | 资源生命周期：IAsyncDisposable、scope、连接、Channel、Timer、订阅 | 未释放、重复释放、回调在释放后触发 |
| R6 | 背压：生产快于消费、Channel 满、慢客户端、发布确认 | 内存涨、无限等待、丢消息、确认早于落库 |
| R7 | 故障隔离：一条坏消息、一个租户、一个订阅 | 拖垮整批、饿死其他订阅、全局锁 |
| R8 | 可观测性：EventId 覆盖单条与批量、成功失败取消 | 文档承诺的信号缺失，或成功与失败信号矛盾 |

可观测性定级锚点：信号缺失但数据可恢复、可重投，定 P2；只有文档把该信号承诺为唯一运维出口时，才升 P1。纯日志文本差异定 P3，不进主清单。

### 7.4 性能、内存与时延

先走单位成本，再决定是否量测。每条主流程填一张表：

| 步骤 | 位置 | 单位成本（往返/语句/分配/await/锁） | 证据 | 可收敛点 |
|---|---|---|---|---|

至少覆盖三条主流程：命令分发、Outbox 发布、Inbox 消费。有流式面时加流式请求。

| 编号 | 检查 | 成立条件 |
|---|---|---|
| P1 | 每消息或每请求的往返与语句数 | 逐条查询、重复读、先 count 再 select、逐条标记 |
| P2 | 分配清单：params、装箱、闭包、LINQ 迭代器、ToArray、字符串拼接、集合表达式、async 状态机 | 热路径每操作分配超预算，且有可消除点 |
| P3 | 同步阻塞：`.Result`、`.Wait()`、`GetAwaiter().GetResult()`、锁内 await | 线程池饥饿、死锁风险、延迟尖刺 |
| P4 | 复杂度：O(n²) 查找、循环内 Remove、重复排序、N+1 | 输入增大时平方增长，且有可替换结构 |
| P5 | 批处理与延迟取舍 | 批量提高吞吐但让默认路径延迟超过声明值 |
| P6 | 驻留内存：静态缓存、无界字典、事件订阅、池、环形缓冲 | 无上限、无淘汰、无清理，长跑后持续增长 |
| P7 | 池化正确性：ArrayPool 归还、清零、双归还、容量上限 | 脏数据、内存泄漏、池被打满后反复分配 |
| P8 | 序列化：反射、中间 byte[]、多次拷贝、UTF-8 与 UTF-16 转换 | 每项多一次拷贝或一次装箱 |
| P9 | 启动：DI 注册复杂度、急切初始化、反射、JIT | 冷启动出现可测的平方或高开销路径 |
| P10 | 量测有效性 | 非 Release、单次运行、无噪声地板、均值掩盖长尾 |

性能候选必须写全七个字段：

1. 触发条件：默认还是 opt-in，哪个栈、哪个方言。
2. 单位成本：当前值与目标值，分母口径。
3. 保留的保证：fencing、顺序、at-least-once、幂等、背压、AOT、零反射。
4. 测量路径：现有 bench 车道、smoke 断言、计数器，或本会话微探针。
5. 判定门槛：默认收益低于 5% 不做。消除 LOH、Gen2、整类风险可低于 5% 立项，但必须写明理由。
6. 回退开关：配置项或提交回退方式。
7. 复活条件：出现什么可观测信号时复议。

时延单独看 p50、p95、p99，不用均值代替长尾。延迟改动必须说明目标 SLO 与批处理代价。没有 SLO 时，用跳数、往返数、锁等待次数作单位成本，不编造毫秒数。

内存判定看绝对值与代际，不只看百分比。消除 LOH 或 Gen2 提升可以低于 5% 立项。一次性启动分配不计入热路径预算。

禁止的性能主张：没有调用路径；没有量具却给墙钟数字；用 Debug 构建对比；单次运行就宣布回归；只测吞吐不测延迟代价；为了省分配破坏语义。

### 7.5 架构、契约与 AOT

本仓是框架库，没有业务应用的 Domain/Application/Infrastructure 分层。架构检查按能力面与公共契约做，不套通用 Clean Architecture 清单。

| 编号 | 检查 | 成立条件 |
|---|---|---|
| A1 | 依赖方向：抽象程序集不引用 provider、broker、EF、Dapper | csproj 或 using 出现反向依赖 |
| A2 | 公共 API：新增成员、接口默认实现、可空标注、Obsolete 路径 | 对实现者是破坏性变更，快照或 CHANGELOG 未同步 |
| A3 | 跨栈对称：Outbox、Inbox、Saga、投影、EventLog、幂等、保留清理 | 同一能力在三栈的契约或可观测行为不一致 |
| A4 | 方言对称：SQLite、PostgreSQL、MySQL | NULL 排序、时间精度、RETURNING、upsert、锁、标识符大小写、参数上限不同 |
| A5 | 注册门面：每个 Add* 独立可用，选项有校验 | 隐藏必需依赖、重复注册行为不明、错误不可行动 |
| A6 | 能力回落：能力接口乘实现者矩阵 | 回落无信号，或文档承诺的优化未生效 |
| A7 | 默认值三方对账：属性默认、解析后有效值、文档数字 | 任一不一致且影响行为 |
| A8 | AOT 与裁剪：`IsAotCompatible`、反射开关、发布后运行 | 发布失败、运行期反射异常、裁剪掉所需成员 |
| A9 | 零反射例外：`MakeGenericType`、`Activator`、`Assembly.GetTypes`、`Type.GetType` | 新增未声明例外，或原有例外扩大 |
| A10 | 可观测性架构：EventId 注册、计数器命名、Activity 标签、关联 ID | 同名不同义、标签含 PII、计数器与数据库真相不一致 |
| A11 | 扩展性：新增 provider 或 adapter 需要改几处 | 必须改核心、必须继承具体类、选项无法组合 |
| A12 | 文档架构：示例可编译、迁移说明、命令存在、计数一致 | 文档与运行代码矛盾 |

`ARCH` 块每轮至少回答：

1. 本轮改动是否新增公共 API 或改变默认行为。
2. 是否新增反射、动态代码、AOT 豁免。
3. 是否让三栈或三方言的差异扩大。
4. 是否新增无界资源或后台任务。
5. 是否新增未被门禁覆盖的路径。

### 7.6 发现更多问题的八种技法

| 编号 | 技法 | 做法 | 产出 |
|---|---|---|---|
| T1 | 变异 | 在隔离镜像里改坏实现，看测试是否变红 | 测试假绿、未覆盖行为 |
| T2 | 故障注入 | 在第 N 次调用抛异常、取消、超时、断连 | 重试、死信、检查点、资源泄漏 |
| T3 | 差分 | Dapper 对 PalORM 对 EF；SQLite 对 PG 对 MySQL；默认对 opt-in | 契约分叉、方言分叉、默认值漂移 |
| T4 | 边界与属性 | 0、1、max、max-1、重复、随机序列；FsCheck 不变量 | 越界、溢出、顺序、幂等 |
| T5 | 消费侧实验 | 最小应用照文档装配；发布 AOT；从上一版本升级 | 注册缺口、文档失实、AOT 失败、升级断裂 |
| T6 | 资源观测 | 分配、连接、线程、Channel、scope、Timer、GC | 泄漏、无界增长、线程池饥饿 |
| T7 | 静态清单 | 危险模式 grep、调用图到汇聚点、配置矩阵 | 可达风险、未接线路径 |
| T8 | 敌对视角 | 破坏者、新员工、安全审计员、偏见审计员、值班运维、消费者 | 假设漏洞、文档缺口、运维盲区 |

每轮至少用 4 种技法，其中至少 2 种运行时技法。只用 grep 的轮次不合格。

敌对视角每次至少选 2 个，并各写 1 条具体问题或「查过，无」的证据：

- 破坏者：什么输入、顺序、并发能打破它。
- 新员工：照文档从零装配，哪一步会卡住。
- 安全审计员：不可信数据能否到达汇聚点。
- 偏见审计员：哪条结论是假设，没有证据。
- 值班运维：凌晨三点看什么信号定位。
- 消费者：升级、AOT、最小依赖、错误信息是否可行动。

## 8. 阶段 4：探针

探针是种子，每个不超过 5 分钟。超时记 `blocked`，换下一个。合法产出只有三种：

- `confirmed`：附命令、退出码、首行输出。
- `refuted`：附观察到的正确行为，以及为什么假设不成立。
- `next`：更尖锐的下一个假设，必须可执行。

探针工程骨架：

```text
AUDIT_DIR/probe/
  Probe.csproj      net11.0，ManagePackageVersionsCentrally=false
  Program.cs        最小装配与断言
```

`Probe.csproj` 用 `ProjectReference` 指向目标项目。包版本从 `Directory.Packages.props` 读取，不凭记忆写版本。

必带四件补偿器：

1. 日志捕获：必须显式覆盖结构化重载，否则 EventId 会被默认实现吞成 0。

```csharp
sealed class CapturingLogger<T> : IPalLogger<T>
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
```

2. 固定选项：

```csharp
sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
```

3. Store 包装器：转发全部方法，按需在第 N 次调用抛异常或返回部分命中。

4. 手动时钟：

```csharp
sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
```

变异模式二选一，开工时声明：

- 隔离变异：复制 `src` 到 `AUDIT_DIR/mirror`，排除 bin、obj、.git，在镜像里改坏实现并跑测试。仓库不动。
- 只读替代：不改源码，用 `gate-audit` 等隔离探针承担变异证据。报告写「代码变异 0 条，替代证据为 gate-audit N 项加运行时探针 M 条」。

两种模式不得混用后声称「变异过」。

同族扩散至少做三个方向：

| 方向 | 查什么 | 要记录 |
|---|---|---|
| 入口族 | 兄弟 Add*、兄弟重载、兄弟配置 | 查过谁，谁没查，为什么 |
| 路径族 | 单条对批量、同步对异步、默认对 opt-in | 差异是否改变语义 |
| 栈族 | Dapper、PalORM、EF；SQLite、PG、MySQL | 哪些栈确认，哪些未确认 |
| 数据族 | 旧行、NULL、重复、超大、混合版本 | 首轮扫描行为 |
| 信号族 | 日志、EventId、计数器、Activity、健康检查 | 哪条路径没有信号 |

每个 `confirmed` 必须写一行「家族边界」：查过哪些兄弟，哪些没查，为什么。

## 9. 证据与分级

发现格式固定，缺字段即丢弃：

```text
ID | 轴 | 级别 | 声明 | 触发条件 | 证据命令 | 退出码 | 首行输出 | 位置 | 证据强度 | 可达消费者 | 反事实 | 现有防线为何没挡住 | 锁定测试 | 修复方向 | 家族边界
```

证据强度：

| 级别 | 含义 | 能否进缺陷清单 |
|---|---|---|
| E1 | 运行复现，有错误状态或错误数据 | 可以 |
| E2 | 运行观察，符合或不符合文档 | 可以 |
| E3 | 静态证据加现有测试、门禁、快照 | 可以，P0/P1 降为 P2 |
| E4 | 仅阅读 | 不可以，只能进候选池 |

严重度先定两个轴：可达面（全部消费者、单栈、单方言、仅自定义实现、仅 AOT）与可检测性（响亮、可观测、静默）。

| 级别 | 判据 |
|---|---|
| P0 | 默认配置加全部消费者加静默错语义或数据风险；安全漏洞；AOT 发布或运行失败 |
| P1 | 文档路径启动或首用失败；文档保证的行为静默缺失；升级后旧数据首轮错误；门禁在坏输入下不红 |
| P2 | 契约或文档漂移；可观测性缺失；未测边界；能力回落无信号；无本会话量具的性能候选 |
| P3 | 不进主清单，最多一行进附录 |

P0/P1 必须写反事实四格：

1. 哪个消费者。
2. 什么配置或调用顺序。
3. 执行什么动作。
4. 看到什么错误结果。

写不出四格就降级。E4 不得进 P0/P1。E3 的 P0/P1 降为 P2。

每条发现必须回答「什么证据会推翻它」。答不出的，说明还没有真正验证。

主清单最多 12 条，同族合并。超出的进候选池，每条一行，标 `[未验证]`，不占主清单名额。

不进主清单的内容：命名、注释、风格、缺少抽象、假想消费者、已被接受的取舍、低于门槛的性能微调、没有可达路径的理论风险。

## 10. 输出模板

报告七章，不增不减：

1. **运行事实**：HEAD、工作树、基线 tag、构建、测试（总数/通过/失败/跳过/未跑面板）、门禁矩阵、模式、变异模式、探针完成数、耗时、静态阅读实际时长。
2. **结论摘要**：不超过 10 行。Top 3 发现、一句话边界声明（本轮保证什么，明确不保证什么）、与上一轮的新增与关闭。
3. **缺陷清单**：不超过 12 条，按严重度排序。每条含声明、触发、证据命令与退出码、首行输出、反事实四格、现有防线为何没挡住、锁定测试名称与修复前应红的断言、可达消费者、家族边界、修复方向。
4. **架构与契约债务**：跨栈矩阵、方言矩阵、公共 API 变化、AOT 与反射例外、默认值三方对账、未接线路径。
5. **性能、内存与时延候选**：七字段表，标「本会话量测」或「未量测」。按收益乘可达乘确定性除以成本排序。
6. **探针台账**：每个探针 confirmed、refuted 或 blocked，附原始输出与家族边界。blocked 写阻塞原因。
7. **未完成与不确定**：discarded、blocked、降级、re-affirmed（引原文）、需裁决、下一轮三个假设、什么会改变本轮结论。

另外落两个机读文件：

- `findings.tsv`：按 §9 字段，制表符分隔。
- `next-round.md`：下一轮三个假设，每条带探针步骤与判据。

报告必须写「本轮新增」与「上轮已关闭」两行。没有对照，无法判断是在进步还是在原地打转。

禁止输出没有方法的综合评分。必须评分时，按六轴各 1 到 5 分，每轴给出证据与样本，并说明评分者与口径。

## 11. 修复轮

选择规则：

- 按 `严重度 × 可达面 × 可检测性 / 修复成本` 排序。
- 每轮最多 `FIX_LIMIT` 条，默认 5 条。
- 优先修一个家族，不修五个实例。
- P0/P1 先于性能候选。性能候选先量测后动手。
- 需要产品决策的，写「需裁决」，不擅自改语义。

每条修复的顺序：

1. 写锁定测试，确认修复前是红的，且红的原因正确。
2. 只改必要实现，保持现有风格与命名。
3. 跑锁定测试转绿。
4. 反向验证：移除修复因子，确认重新变红，再恢复。没有反向验证的因子只能标 `[推断]`。
5. 跑目标项目测试，再跑全量测试循环。
6. 跑相关门禁：构建、`gate-audit`、`count-audit`、`doc-consistency`、`encoding-gate`、`guard`。
7. 公共 API 或行为或配置或命令变更，同一提交更新文档、快照、`CHANGELOG.md`。
8. 本地提交，格式 `类型：描述`。类型用 `修复`、`测试`、`文档`、`构建`、`决策`、`卫生` 等。
9. 更新台账。性能否决另写 `.ai/perf-ledger.md` 表 2 或表 4，该目录是独立 git 库，单独提交。
10. 不推送、不打 tag、不发布。外发动作需要用户明确授权。

约束：

- 一次只改一个变量。禁止顺手重构、顺手优化、顺手改命名。
- 超过 3 个文件，或引入新的外部行为假设，先写 10 行设计说明，再动手。
- 修复尝试 3 次仍失败，停止并报告，禁止用放宽断言或捕获异常收场。
- 只改测试不改源码时，按仓库规则用 `ALLOW_TEST_ONLY_CHANGE=1`，并在提交信息说明原因。
- 新增 Skip 需要 `ALLOW_NEW_SKIP=1`，并在提交信息写到到期日与理由。
- 公共 API 快照与 `CHANGELOG.md` 必须逐提交耦合，不能靠后补提交洗白。
- 新文件默认 LF，仓库要求 CRLF，写完做字节级校验。
- Git Bash 下用 `-p:`，不用 `/p:`。
- 门禁可能使用陈旧的 Release 二进制，先 `dotnet build -c Release`。

修复完成后，为每个修复指认锁定测试。没有锁定测试的修复，视为未验证。

## 12. 循环、台账与停止条件

台账位置：`docs/review/optimization-ledger.md`。SCAN 轮先在 `AUDIT_DIR` 起草，FIX 轮落入仓库并通过门禁。

台账字段：

| ID | 轴 | 位置 | 发现 | 状态 | 证据 | 决策来源 | 锁定测试 | 轮次 | 复活条件 |
|---|---|---|---|---|---|---|---|---|---|

状态只用六种：`fixed`、`rejected`、`deferred`、`accepted-risk`、`candidate`、`needs-decision`。

规则：

- 开工先读台账，收工必须写。只追加，不删除。
- 已修复项保留锁定测试名称，回归轮直接重跑。
- 已否决项必须引用决策来源原文。没有原文的否决无效。
- 延后项必须有可观测复活条件与到期日。
- 同一家族只占一个 ID，兄弟实例写在「家族边界」里。
- 台账是指针，不是第二真源。性能否决的权威记录在 `.ai/perf-ledger.md`，架构决策的权威记录在 `docs/decisions/`。

每轮必须引入至少一个上轮没查的分片、轴或技法。否则只是在同一块地上反复翻土。

停止条件：

1. 连续两轮发现全部属于已知家族复发，停止人肉循环，把该家族做成门禁或脚本，再继续。
2. 一轮没有新的 P0/P1，且 P2 都已登记，转为维护节奏，不再每轮全量扫描。
3. 预算耗尽，交部分报告，明确未完成项，不宣布完成。
4. 宣布「全部完成」必须逐条对照任务清单，禁止凭印象。
5. 停止条件与用户指令冲突时，显式呈报，不沉默照办。
6. 评分必须有方法与样本。没有方法的分数不比没有分数更安全。

## 13. 本仓适配

### 13.1 常用命令

| 目的 | 命令 |
|---|---|
| 构建 | `dotnet build PalDDD.slnx -c Release -v q` |
| 测试 | 逐项目 `dotnet test <csproj> --no-build -c Release --verbosity minimal` |
| 门禁矩阵 | `dotnet run scripts/gate-audit.cs`（快照用 `-- --inventory`） |
| 计数对账 | `dotnet run scripts/count-audit.cs` |
| 文档一致性 | `dotnet run scripts/doc-consistency.cs` |
| 规范快检 | `dotnet run scripts/verify-conventions.cs -- --quick` |
| 凭据扫描 | `dotnet run scripts/secret-scan.cs` |
| 漏洞扫描 | `dotnet run scripts/vuln-scan.cs` |
| 覆盖率 | `dotnet run scripts/ci-coverage.cs -- --local` |
| 性能烟测 | `dotnet run -c Release --project bench/PalDDD.Benchmarks/PalDDD.Benchmarks.csproj -- --smoke` |
| 性能全量 | `dotnet run scripts/eps-run.cs -- --quick` 或 `--full` |
| AOT | `dotnet publish samples/PalDDD.AotSample/PalDDD.AotSample.csproj -c Release -r win-x64 -p:PublishAot=true` |
| 真库方言 | 见 §4 的 `PALDDD_TEST_PG` 与 `PALDDD_TEST_MYSQL`，地址用 `127.0.0.1` |

基准车道按需选择：`--framework`、`--app`、`--infra`、`--concurrency`、`--persist-pg`、`--persist-mysql`、`--stream`、`--saga`、`--compression`、`--delivery-pg`、`--delivery-mysql`。不要在一轮里跑全部车道，按假设选一条。

### 13.2 本仓已知坑

| 坑 | 规避 |
|---|---|
| tail 或 head 管道吞退出码 | 用 `set -o pipefail` 或 `${PIPESTATUS[0]}`，证据命令末段不用它们 |
| Git Bash 把 `/p:` 当路径 | 一律用 `-p:` |
| 新文件默认 LF | 写完做字节级校验，CRLF 计数等于 LF 计数 |
| `sed -i`、heredoc 被守卫拦 | 用 Write 或 Edit，临时脚本放 `AUDIT_DIR` |
| MTP 批量握手失败 | 逐项目跑，单项目挂起改用测试 exe |
| Skip 数不披露 | 报告写跳过数、排除面板、未跑项目 |
| EF InMemory 的 Dispose 等于 Close | 不要用它造断连探针，用 SQLite 或真库 |
| SQLite `:memory:` 每连接独立 | 复用同一连接，或用共享缓存 |
| `grep -rl` 返回反斜杠路径 | 用 `find` 加 `xargs grep` 组合 |
| `grep -E '^+++'` 报错 | 用 `grep -F '+++'` 排除 diff 头 |
| 日志含 GBK | 用 `grep -a`，断言只匹配 ASCII 测试名或 EventId |
| dev 上 `git describe` 解析到旧 tag | 显式选基线并写明理由 |
| `nupkgs/` 是旧产物 | 不要引用其中的版本或代码 |
| 测试宿主订阅 ActivitySource | 零分配断言放基准进程，不放测试宿主 |
| pre-commit 用陈旧 Release 二进制 | 先 `dotnet build -c Release` |
| wslc 容器地址写 localhost | 用 `127.0.0.1`，否则进入 Testcontainers 分支 |
| PalORM 与 Integration 的方言测试需要容器 | 无 Docker 时用 wslc 配方，或记 blocked |

### 13.3 历史高发家族（[待验证]，不是当前缺陷结论）

这些家族在本仓历史上反复出现。先查兄弟与当前代码是否仍有同形，不要直接复制历史结论。

1. 组件级 `Add*` 独立装配缺失门面依赖。历史上 PG 通知器与 Outbox、Inbox、Saga 组件注册各漏过一次日志门面。查每个公开注册入口单独装配。
2. 批量路径与单条路径的告警、指标、结果语义不对齐。历史在 Inbox 批量路径发现 1004、1005、1006 缺口与 pending span 缺口。画单条与批量的成功、失败、取消、跳过矩阵。
3. 能力接口回落无信号。批量 store、可恢复事件源、方言能力接口未实现时，是否静默走低效或错误路径。
4. 三持久化栈契约分叉。同一场景在 Dapper、PalORM、EF 的异常、空集、重复、并发、时间语义是否一致。
5. 三方言语义分叉。NULL 排序、时间精度、RETURNING、upsert、锁、标识符大小写、参数上限。
6. 默认值三方漂移。属性默认、解析后有效值、文档数字。
7. 域事件到 Outbox 的真实链路。每个栈是否真的在同事务落库，拦截器是否挂上，独立上下文是否提交。
8. 租约与 fencing。过期重租、双实例、长 handler、取消、部分失败下旧 token 是否仍生效。
9. 时间哨兵与精度。`DateTime.MaxValue` 在 MySQL `DATETIME(6)`、UTC 与本地时间、DST、时钟注入。
10. 池化与缓冲。ArrayPool 归还、清零、双归还、无界 Channel、环形缓冲溢出、闭包捕获。
11. 热路径分配。`params` 数组、装箱、LINQ 迭代器、`ToArray`、字符串拼接、集合表达式、async 状态机。
12. 门禁假绿。空输入、只调用不断言、变异后仍绿、`| tail` 掩盖退出码、跳过未披露。

## 14. 交付前自检

逐条回答是或否。任一为否，先补再做交付。

1. 先跑基线门禁了吗，还是先读源码？
2. 读了 `perf-ledger`、相关 ADR、最近两轮报告吗？
3. re-affirmed 项引用原文了吗？
4. 每条 P0/P1 有运行时命令与非零退出证据吗？
5. 每条 P0/P1 的四格反事实写得出来吗？
6. 每个修复有锁定测试名称与修复前应红的断言吗？
7. 用了至少 4 种技法，其中 2 种运行时吗？
8. 每个 confirmed 写了家族边界吗？
9. 每条性能候选有单位成本、保留保证、测量路径、门槛、复活条件吗？
10. 披露 Skip 数、排除面板、未跑项目了吗？
11. SCAN 模式改过仓库文件吗？
12. 重复上报已接受项了吗？
13. 有 P3 凑数或通用建议吗？
14. 行号来自命令输出，还是记忆？
15. 台账更新了吗，下一轮三个假设写了吗？
16. 说明什么证据会推翻本轮结论了吗？

## 15. 反模式

1. 只做静态盘点就宣称没有问题。
2. 用测试全绿当覆盖证据，不做变异或探针。
3. 只有 grep 证据的 P0/P1。
4. 探针只填表，不产生 confirmed、refuted 或 next。
5. 没有调用路径的性能建议，没有量具的墙钟数字。
6. 为了性能破坏 AOT、零反射、顺序、幂等或背压。
7. 对未读文件做行为断言，把注释当运行行为。
8. 重复上报已接受项，把 accepted-gap 当新缺陷。
9. 用「需要前提」软化反事实。
10. 用「建议进一步深入分析」收尾。
11. 新增断言当前行为的测试，或放宽断言来变绿。
12. P3 凑数，把清单拉长显得彻底。
13. 大爆炸重写，顺手重构，一次改多个变量。
14. 没发布没运行就声称 AOT 兼容，没披露跳过就声称全绿。
15. 删除声明注释来通过检查。
16. 在 Git Bash 用 `/p:`，用 `| tail`，用陈旧二进制。
17. 中途停下来问用户，而不是把需裁决项写入报告。
18. 把「发现更多」理解成「报更多条」，用低价值条目稀释主清单。

## 16. 预算不足时的保底

预算低于 60 分钟或模型能力有限时，只做三件事，按顺序：

1. 每个公开 `Add*` 独立装配，加能力接口乘实现者矩阵。至少 1 个运行时探针。
2. 单条与批量路径的告警、指标、结果、取消、跳过对照。至少 1 个运行时探针。
3. 三条主流程的单位成本走查：命令分发、Outbox 发布、Inbox 消费。

只报 confirmed。refuted 与 blocked 写入台账，供下一轮复用。不要为了凑报告把候选池写成缺陷清单。
