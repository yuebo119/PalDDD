# Pal.DDD AI 质量系统全量运行报告（2026-09-14）

> 基线 commit：`e51d48b`（dev 分支，工作树运行前/运行后均干净）
> 运行方式：本机（Windows 10 x64）逐脚本真实执行，零缓存复用；16 个门禁脚本 + 14 个测试项目 + 2 个环境受限项目全量实跑
> 机器环境：无 Docker（`docker: command not found`）；`appsettings.test.local.json` 指向内网服务器 192.168.200.120（已 gitignore，未入库）

---

## 一、运行结论

**17 项全绿 · 2 项红灯（均为环境性，0 代码级缺陷） · 6 项发现（4 文档/1 代码改进/1 门禁裁决）**

机械防线与构建面全部通过：32 个门禁脚本矩阵中 16 个接线门禁全 OK（含自证），6 项变异探针全部证明"能拒绝坏输入"；`check-all` 三段全零（IDE 0 项 / CA 0 错误 / 编译 0 error CS）；14 个测试项目 1226 用例全绿。

两项红灯的根因都不在代码：

- **Messaging.Integration.Tests**（5 失败 / 3 跳过）：内网 RabbitMQ 服务器处于"TCP 可达、AMQP 协议无响应"状态（探针实测 TCP 12ms 连上，但 `connection.start` 帧永不到达，18.9s 超时）。Kafka 同机不可达（协议级预检识别为环境问题，3 个测试正确跳过）。
- **PalORM.Tests**（46 失败）：本机无 Docker，多方言 Fixture 按设计 fail-closed 拒绝连接外部数据库。该状态在 `open-items-2026-09-14.md` D1 已记录。

---

## 二、运行矩阵

| # | 门禁 / 组件 | 结果 | 关键数据 |
|---|------------|------|---------|
| 1 | secret-scan | ✅ PASS | 537 文件已扫描，零硬编码凭据 |
| 2 | encoding-gate | ✅ 5/5 | E1 CRLF / E2 BOM / E3 mojibake / E4 .verified / E5 行尾一致 |
| 3 | xml-guard | ✅ PASS | 全仓 XML 良构 |
| 4 | dapper-param-guard | ✅ PASS | 枚举直传零违规（CI #94 防线在位） |
| 5 | verify-ai | ✅ 23/23 | V1-V23 全过（含 V19 传感器定标、V21 P3 老化） |
| 6 | verify-conventions --quick | ✅ | V5 TODO 0 处 · V8 模板 9 个 · V9 脚本引用在位 · V10 78 文档互链可解析 |
| 7 | verify-conventions full | ❌ 2 项目红 | 见 §三（环境性；静态四项与 build 零错误零警告全过） |
| 8 | doc-consistency | ✅ D7 | .ai/README.md 文件地图一致 |
| 9 | tech-debt | ✅ 9/2/1/0 | 9 通过 · 2 允许（超长行）· 1 提示（[Obsolete] 6 处在案）· 0 失败 |
| 10 | gate.cs | ✅ | G23 快照同步 PASS · G24 裸路径 PASS（G22 因 --allow-dirty 按设计跳过） |
| 11 | gate-lite | ✅ 3/0 | 异常 sealed / 文件头 / 文件命名 |
| 12 | test-gate | ✅ 0 失败 | OSC 无翻转（观测 1246 测试） |
| 13 | guard.cs | ✅ 8/8 GREEN | 24.7s（SourceGen/Architecture/DocConsistency/AssertionStrength/TechDebt/TestGate/DiagnosticCoverage/Compression） |
| 14 | check-all | ✅ 全零 | IDE 0 项 · CA 分析 0 错误 · 编译 0 error CS |
| 15 | gate-audit | ✅ | 32 脚本：接线 16 全 OK · 手工工具 16 · UNVERIFIED 0 · 缺口 0 · 未归类 0；**6 探针全 PASS** |
| 16 | vuln-scan | ✅ 0 漏洞 | 传递依赖含在内 |
| 17 | 测试 14 项目 | ✅ 1226 通过 | Analyzers 47 · CQRS 29 · Compression 31 · Core.Abstractions 8 · Core 314 · DI 167 · EventLog 34 · Hosting 25 · Integration 297(283+14 跳过) · Messaging 28 · Projections 9 · Repository.EFCore 12 · Serialization 51 · Transactions 174 |
| 18 | Messaging.Integration.Tests | ❌ 5 失败 / 3 跳过 | 全 RabbitMQ 失败 + 全 Kafka 跳过；根因见 §三.1 |
| 19 | PalORM.Tests | ❌ 46 失败 | 无 Docker，多方言 Fixture fail-closed；见 §三.2 |

门禁探针明细（gate-audit 隔离仓库注入坏输入，断言必须非零退出）：
`secret-scan` 拒绝密钥格式 + 放行干净输入（负向对照）· `encoding-gate` 拒绝 BOM + 覆盖 scripts/ + 拒绝裸 LF · `dapper-param-guard` 拒绝枚举直传。全部 PASS，即"见过仪器故意产生错误答案"。

---

## 三、红灯根因分析

### 3.1 Messaging.Integration.Tests：RabbitMQ 服务器 AMQP 层故障

**实测证据链**（多方法交叉）：

1. 隔离探针（`%TEMP%` 独立脚本）实测 `192.168.200.120:5672`：TCP 连接 **12ms 成功**（`connected=True`）。
2. 测试失败堆栈显示 AMQP 协议握手超时：`connection.start was never received, likely due to a network timeout` → `BrokerUnreachableException`，每次失败固定约 18.9s。
3. Kafka 轴对比：`GetMetadata` 协议级探测失败 → 3 个 Kafka 测试**跳过**而非失败（信息："Kafka broker 预检失败——环境问题，非代码失败"）。

**结论**：服务器 5672 端口 TCP 层开放，但 AMQP 协议层无响应。该故障模式与 `open-items-2026-09-14.md` D2 记录的"PG/MySQL/Kafka/RabbitMQ 同时 TCP 开、协议无响应"同型，属 D2 描述现象的再现。**需在服务器侧处理**（重启/检查 RabbitMQ 服务）。

### 3.2 PalORM.Tests：Testcontainers 必需 + 本机无 Docker

46 项失败信息一致：`多方言 Fixture 禁止连接或清理外部数据库；必须启用 Testcontainers`。这是刻意的 fail-closed 安全守卫（防止测试误连外部库并执行清理），与 `PalDDD.Integration.Tests` 对同环境的"跳过"处理（14 项，信息："当前配置指向外部连接串，跳过"）形成设计上的不对称——前者安全优先选失败，后者可用性优先选跳过。两者均为在案设计（D1 记录），本机无法执行这 46 项，CI 容器环境可全跑。

---

## 四、系统性问题发现

### F2（P2·代码）RabbitMQ 预检深度与 Kafka 不对称，设计意图在"半坏"故障下失效

`BrokerIntegrationTests.cs:135-153` 的 Rabbit 预检只做 **TCP 探活**（`TcpClient.ConnectAsync`，5s），而 `:112-133` 的 Kafka 预检是 **协议级**（AdminClient `GetMetadata`，8s）。预检注释（`:136`）声称"broker 不可达时显式 Skip 而非 19s×3 假失败（本地实测 41s 假失败签名）"，但本次实测在"TCP 通、AMQP 死"模式下预检判 true → 5 个 Rabbit 测试各 ~19s 假失败（合计数分钟），声称与实测不符。

修复方向：Rabbit 预检升级为 AMQP 协议级探测（`ConnectionFactory.CreateConnectionAsync` + 5s 超时），与 Kafka 轴对称。该故障模式在 D2 已出现至少两度，属会重复造访的环境状态。

### F3（P2·文档）open-items D2"当前就绪"声明与本次实测矛盾

`open-items-2026-09-14.md:41`（D2）称 192.168.200.120"当前就绪，全部实测通过"。本次实测：RabbitMQ AMQP 无响应、Kafka GetMetadata 失败——**该声明已不再成立**。按"跨会话核实"纪律，环境状态类声明的有效期短，建议改为带时间戳的观测记录（"截至 X 时实测通过"）或直接删除结论句。

### F4（P3·文档）AGENTS.md 的 `path-gate` 表述与 CI 实际结构不符

`AGENTS.md:44` 将 `path-gate` 与 `coverage`、`dialect-probe` 并列为 CI job。实际 `ci.yml` 仅 4 个 job（build-and-test / aot-verify / coverage / dialect-probe），"Path gate"是 **dialect-probe job 内的一个路径过滤步骤**（`ci.yml:246-260`，`id: paths`）。`git log --all -S "path-gate"` 全历史仅命中 `14cc980`（写入 AGENTS.md 的那次提交），ci.yml 中从未存在此 job。

### F5（P3·文档）3 个文档共 4 处引用已删除的 .sh 脚本路径

主仓 `scripts/` 已 0 个 .sh（MIG-012 全 C# 化），但以下**活引用**未同步：

| 位置 | 现状 | 应改为 |
|------|------|--------|
| `docs/testing.md:475` | `scripts/verify-conventions.sh`（文件索引表） | `.cs` |
| `docs/pitfalls.md:139`（SE2） | `scripts/secret-scan.sh` | `.cs` |
| `docs/release.md:642` | `scripts/changelog-facts.sh` + `scripts/changelog-check.sh` | `.cs` ×2 |

（`docs/design/` 与 `docs/review/` 中的 .sh 提及为历史叙述，合法；`branch-flow.md`/`release.md` 的 `publish-main.sh 已删除` 标注合法。）

### F6（P3·门禁裁决）V9 守护盲区：非命令形态的路径引用零守护

`verify-conventions.cs:161-181`（V9）的设计范围是"命令形态引用"（`bash X.sh` / `dotnet run X.cs`），反引号表格/正文形态（如 `| scripts/x.sh |`、`参见 scripts/x.sh`）不在抽取范围。F5 的 3 处失实正是该盲区的产物，且 `--quick` 每次提交都会跑 V9 却测不到它们。两个选项待裁决：① 扩展 V9 覆盖反引号路径形态（需防误报：历史叙述由 `IsHistoricalMention` 兜底，可能要扩关键词）；② 维持现状，接受该类漂移靠人工发现。

### F7（P3·环境信息）verify-conventions full 模式在无 Docker 机器上必然失败

full 模式（grep + build + test 全量）已真实运行：静态四项与构建全过，测试阶段必红（§三.2）。该模式无自动化调用者（CI 不跑它，pre-commit 用 `--quick`），其"本地全量自检"定位在无 Docker 开发机上不可完成。非缺陷，但建议在 `docs/testing.md` 的验证章节标注前提（"full 模式需 Docker 或仅 CI 环境可全跑"），避免执行者误判为代码问题。

---

## 五、口径核对（计数锚实测）

| 声明处 | 声明值 | 实测值 | 结论 |
|--------|--------|--------|------|
| README.md ADR 计数 | 22 份 | `ls docs/decisions/*.md` = 22 | ✅ 一致 |
| AGENTS.md pre-commit 守卫 | 7 道 | hook 实际 7 道 | ✅ 一致 |
| AGENTS.md CI job 列表 | 含 path-gate | 4 job，无 path-gate | ❌ 见 F4 |
| CI 四门循环 | encoding/doc-consistency/tech-debt/test-gate | `ci.yml:127` 循环体一致 | ✅ 一致 |
| 主仓 scripts/ 形态 | 全 C#（0 .sh） | 32 .cs、0 .sh | ✅ 一致（文档引用除外，见 F5） |
| 测试项目数 | — | 16 src + 15 test（含 Testing 支持库） | ✅ |

---

## 六、环境说明与复现命令

```bash
# 本报告全绿项的复现（仓库根执行）
dotnet run scripts/secret-scan.cs && dotnet run scripts/encoding-gate.cs
dotnet run scripts/verify-ai.cs && dotnet run scripts/tech-debt.cs
dotnet run scripts/gate.cs -- --allow-dirty && dotnet run scripts/guard.cs
dotnet run scripts/check-all.cs && dotnet run scripts/gate-audit.cs
dotnet run scripts/vuln-scan.cs <(dotnet list package --vulnerable --include-transitive --format json)  # bash 进程替换需 bash

# 逐项目测试（MTP 协议：一次一项目）
for p in $(find test -name '*.Tests.csproj' ! -path '*/obj/*' ! -path '*/bin/*' | sort); do
  dotnet test "$p" --no-build -c Debug
done

# 环境受限项
docker info                      # 本机：command not found
# RabbitMQ AMQP 探针（不打印凭据）：TCP 12ms 可达，connection.start 18.9s 超时
```

**注意**：本报告所有数字均为本次真实运行的输出；`verify-conventions full` 测试阶段的 exit code 需用 `${PIPESTATUS[0]}` 或重定向捕获（`dotnet run ... | tail` 会掩码退出码——本仓已知陷阱，本次首跑即被掩码一次，已用重定向复跑确认 exit 1）。

---

## 七、修复轮闭环（2026-09-14 同日）

本报告 6 项发现（ITM-675~680）已在修复轮全部闭环，明细与验证证据见
[`action-items-2026-09-14-quality-run.md`](action-items-2026-09-14-quality-run.md)。要点：

- **ITM-675**：服务器自行恢复（约 14 时），`Messaging.Integration.Tests` 复测 8/8 通过——5 失败 + 3 跳过全部消解。
- **ITM-676**：Rabbit 预检升级为 AMQP 协议级握手，S3 反向验证三重——假 TCP 目标旧逻辑误判 `true` / 新逻辑 `false`；真服务器新逻辑 `true`（25ms）；端到端 8/8。
- **ITM-677/678/679**：D2 状态时间线 · AGENTS.md CI 段校准（path-gate 归位 + dapper-param-guard/template-gate 补全）· 4 处 .sh 引用修正。
- **ITM-680**：V9 边界裁决为方案②（维持现状），已知边界已登记在 V9 块头注释。
- 门禁复验：encoding-gate 5/5 · verify-conventions --quick 全过 · guard 8/8 GREEN · check-all 三段全零。

