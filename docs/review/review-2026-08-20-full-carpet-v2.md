# 全仓评审报告 · 第三十六轮（验证轮 · 八片并行地毯 · 第二遍）

> 基线：835ebe7（dev=main）｜日期：2026-08-20 ｜方式：8 片并行子代理真实逐行读取（无缓存零抽样）+ 主线程跨片横向/机械轴/终审
> 覆盖：**516/516 文件，78,252 行 100% 逐行**
> 机械轴：gate 21/1/0 严格｜verify-ai 20/20｜encoding-gate 4/4｜test-gate 0 失败｜doc-consistency 10/10｜棘轮 172/173｜CI main 三 job 绿｜全量 15/15 项目 1,087+ 测绿

## 段 1：发现总览

**P0=0｜P1=4｜P2=17｜P3≈80（新发现）+ ~70（上轮 P3 存量确认仍开放）**

### 本轮核心洞察：验证轮原则的残酷实证

**上轮 13 项 P2 修复中，4 项被本轮验证轮抓出引入回归或修复不完整（31% 修复缺陷率）**——比账本历史均值（50 项修复 3 项自带缺陷=6%）高 5 倍。原因：上轮修复在同一会话内连续执行，修复者疲劳+姊妹联动仍靠记忆。这验证了 engine.md "修复轮后必须验证轮" 协议的必要性——**没有第二轮地毯，这 4 个 P1 会潜伏到用户报告**。

## 段 2：P1 定稿清单（四项，全部为上轮修复引入/残留）

| # | 发现 | 来源 | 修法 |
|---|---|---|---|
| **P1-1** | **D4 修复回归**：EscapeLiteral 的 `'.'`/`'"'` fail-fast 被错误应用到 SQL 值位置参数——`OutboxByType(messageType)` 的 WHERE 值含 .NET 类型全名 `Order.Created` → 合法调用直接抛 ArgumentException | 片8 | 拆分为 `EscapeJsonPathSegment`（带守卫）与 `EscapeSqlLiteral`（仅引号翻倍），值位置调用后者 |
| **P1-2** | **mojibake 修复不完整（TransactionsTests 7 行 + SagaProcessorTests 4 处）**：上轮只修了 SagaProcessorTests 文件头与行 152，TransactionsTests 也只修了一半 | 片7+片2 | 两文件残余全清（编码门禁 E3 指纹修正后重跑确认） |
| **P1-3** | **编码门禁 E3 指纹与实际残余零重叠**：门禁 15 个指纹字符序列扫描命中 0，但实际 mojibake 用的是另一组字符——**防线检测不到自己的 motivating case** | 片2 | 扩充指纹集（增加 `鐢ㄤ|鈫|姝ラ|鍒涘缓|琛ュ伩|妯℃|瀹炲|妫€|闅旂|鍙傛暟` 等本仓实际产物） |
| **P1-4** | **编码门禁 E1 对 .ai/scripts 整体静默盲区**：.ai 被 gitignore → git cat-file 空 → PASS 假绿——encoding-gate 自身不在自己的保护范围内 | 片2 | E1 增加本地文件系统回退检查（.ai 文件不走 git blob） |

## 段 3：P2 定稿清单（17 项，按根因族）

### 族 A：上轮修复的姊妹联动缺口（4 项——T1 防线目标类别再次批量现身）
| # | 发现 | 修法 |
|---|---|---|
| A1 | ITM-228 INSERT IGNORE 修复未联动——同包 PalOrmProjectionCheckpointStore + PalOrmInboxStore + Dapper SqlTemplates 三处同型残留 | 对齐 ITM-228 修法（普通 INSERT+唯一冲突捕获） |
| A2 | DapperEventLog "流式读取"声明失实（QueryAsync Buffered 全量物化）——姊妹 PalORM/EFCore 均为真流式 | 改 unbuffered 或修注释 |
| A3 | Dapper↔EFCore MarkProcessed 对象字段语义注释矛盾（Dapper 声称对齐 EFCore 但 EFCore 兜底不动入参） | 修正注释或对齐行为 |
| A4 | DapperEventLog 读取路径零可观测性——三方姊妹均有 span/metrics | 补齐或文档限定 |

### 族 B：编码/门禁防线缺陷（4 项——新防线首战暴露的自体问题）
| # | 发现 | 修法 |
|---|---|---|
| B1 | flaky-gate 零报告=零 flaky 假绿 + skipped→F 误报 | 增"报告缺失即 FAIL"守卫 + skipped 分类 |
| B2 | fix-orchestrator 继承盘根死循环 | 与 P3-SH-003 合并修（加 `[ -f "$d/PalDDD.slnx" ]` 兜底 break） |
| B3 | verify-conventions A2 "退出码先行"是死代码（set -e 击穿） | 改 `BUILD_OUTPUT=$(...) || BUILD_RC=$?` |
| B4 | V19 台账超期检查 python3 缺失时静默假绿 | 对齐 D11/A1 模式（空串→WARN） |

### 族 C：公开接口/DDL 契约失实（4 项）
| # | 发现 | 修法 |
|---|---|---|
| C1 | ISagaManager 公共接口含 internal 抽象成员——外部不可实现（HITL 承诺被可见性卡死） | 成员改 public 或接口拆分 |
| C2 | MySQL DDL 列上限与 EFCore 映射系统性漂移（Reason 2048 vs VARCHAR(255)） | DDL 对齐 EFCore MaxLength |
| C3 | 四份 DDL 均缺 events.event_id 唯一索引（EFCore 有/Dapper 依赖/脚本没有） | 补 CREATE UNIQUE INDEX |
| C4 | CodeFix 前缀叠加不剥离旧前缀（ITM-221 姊妹缺口） | 对齐 AddVersionSuffix 的剥离逻辑 |

### 族 D：文档/测试/资源（5 项）
| # | 发现 | 修法 |
|---|---|---|
| D1 | bounded-context.prompt 引用不存在包 + "Dapper AOT 兼容"与 ⚠️ 修正矛盾 | 修正引用与口径 |
| D2 | tutorial.md 三处示例与框架主张冲突（反射序列化 vs AOT / HandlerNotFound 误用 / UtcNow） | 逐处修正 |
| D3 | PalDDD.Testing.csproj 缺 IsPackable=false | 一行修复 |
| D4 | KafkaSubscription Dispose 缺 Close（丢 offset 提交/离组） | 兜底分支补 Close |
| D5 | PalORM 多方言测试正常路径不 dispose 容器/Session | 补 await using |

## 段 4：修复清单

**立即（P1×4）**：
1. EscapeLiteral 拆分（P1-1）
2. 两文件 mojibake 全清 + E3 指纹扩充 + 重跑确认（P1-2/3）
3. E1 增加本地回退检查（P1-4）

**本迭代（P2 族 A-D）**：按族分批——族 A 姊妹联动（用 fix-orchestrator）、族 B 门禁自修、族 C DDL/接口、族 D 文档/测试

## 段 5：上轮修复逐项验证结论

| 上轮修复 | 本轮验证结果 |
|---|---|
| A1 D11 WARN | ✅ 生效（python3 缺失→WARN） |
| A2 verify-conventions 锚定 | ✅ 匹配逻辑正确 / ⚠ set -e 诊断死路（B3） |
| B1 PalORM NoWarn 理由 | ✅ 三方言理由注释在位 |
| B2 Dapper Description ⚠️ | ✅ 三方言已改 |
| B3 MemoryPack 文档 | ✅ usage.md 已修正 |
| C1 PalOrmUoW finally | ✅ RollbackAsync 正确 / ⚠ DisposeAsync 残留（片6 P3） |
| C2 EFCore DisposeAsync | ✅ 过滤器覆盖三 provider |
| C3 路由器端口全编码 | ✅ 与 EncodeHostEntry 语义逐条等价 |
| D1 usage 泛型重载 | ✅ 示例正确改为 When<X> |
| D2 分块读 OOM | ✅ 敌对推演六路径全过 |
| D3 安装器守卫 | ✅ .ai 不再短路 |
| D4 Sqlite 转义 fail-fast | **❌ P1-1 回归（守卫蔓延到值位置）** |
| mojibake 头注释 | **❌ P1-2 不完整（Saga+Transactions 各有残留）** |
| 快照去重 869 行 | ✅ 零重复段、泛型重载为真实签名 |
| encoding-gate E1-E4 | **❌ P1-3/4（E3 指纹零重叠 + E1 .ai 盲区）** |
| fix-orchestrator | ⚠️ 功能可用但有盘根死循环 + printf 乱码 |
| flaky-gate | ⚠️ 骨架好但零报告假绿 + skipped 误报 |
