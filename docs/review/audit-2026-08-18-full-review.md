# Pal.DDD 第三十轮全量评审报告（`/review --full`）

> 基线：`5d2acde`（Kafka 测试四层防线重设计）
> 生成：2026-08-18 · 执行：四片并行子代理地毯逐行（片 1/4 通道失败重派）+ 主线程专项（三新提交 diff 敌对审查）+ 跨片不变式
> 系统状态：误判库 PD1-PD32（40 模式）+ 四评审轴（机械/静态/实测/环境）

---

## 段 1：覆盖度声明

| 分片 | 文件数 | 行数 | 方式 |
|------|-------|------|------|
| 片 1（重派） | 16 | ~5,300 | 逐文件逐行 Read + 1 次编译探针（record struct partial）+ 4 项交叉 grep |
| 片 2 | 28 | ~6,400 | 逐文件逐行 Read + 2 次交叉 grep |
| 片 3 | 43 | ~6,300 | 逐文件逐行 Read + 3 项实证（NuGet XML/ReferenceEquals/Revision 递增） |
| 片 4（重派） | 88 | ~7,300 | 逐文件逐行 Read（目录级全覆盖） |
| **合计** | **175** | **~25,300** | 全部 Read 工具逐行真实读取，无缓存/记忆 |

**明确不检查**：test/（test 系统负责）、docs/（doc-consistency 10/10 已跑）、scripts、.g.cs 生成物（只验语义）。
**主线程专项**：`acea9b6..5d2acde` 三提交（f9039b5 修复轮 / 5548b4b 超时 / 5d2acde 测试重设计）16 文件 diff 逐行敌对审查——修复自身未引入新问题，ITM-174 successor 15 属性完整、ITM-176 数学边界复核（2^39<TimeSpan 上限）正确。

## 段 2：机械轴（执行门禁）

| 防线 | 结果 |
|------|------|
| gate-check PDDD-G1..G22 | **22/22 全 PASS** |
| verify-ai-system（V1-V16） | **16/16**（V7 误判库 40 条动态计数） |
| tech-debt-scan | 20 通过 / 2 允许 / 0 失败 |
| doc-consistency-check | **10/10** |
| review-snapshot | 已生成（基线 5d2acde） |
| 方言实测轴（dialect-probe） | **PG 20 + MySQL 20 全 PASS**（真库） |
| 全量测试（全新构建后） | **973/973 全绿（35s）** |

## 段 3：发现汇总

- **P0：0 · P1：1 · P2：2 · P3：25**
- **证伪：4 项**（PD32 容器探针规避、HandlerMarker 幂等、sync-over-async 契约、EFCore Parse 族内一致）

### P1

| ID | 位置 | 问题 |
|----|------|------|
| ITM-191 | IdempotencyProcessor.cs:71-84 | **ITM-180 姊妹漏网**：`MarkCompletedAsync` 失败（DB 瞬断）落入通用 catch → 已成功执行的记录被 `MarkFailedAsync` 降级为 Failed → `CanStartNewExecution`（Failed 可重入）→ handler 重放 → **副作用二次执行**。InboxProcessor 同位置已在二十九轮修复（单独 catch + 区分日志 + 按成功返回），Idempotency 是三兄弟（Inbox/Outbox/Idempotency）中未对齐者——ITM-180 自身犯了 PD24（修一个管线孪生漏另一个）。触发路径：handler 成功 + MarkCompleted 抛非 OCE |

### P2（SQLite 类型限定——ITM-188 姊妹漏网 2 处）

| ID | 位置 | 问题 |
|----|------|------|
| ITM-192 | DapperSagaStateStore.cs:222-227 | `IsUniqueConstraintViolation` SQLite 分支裸消息匹配无 `SqliteException` 限定（DapperEventLog ITM-188 已修，本处漏）→ 任意 DbException 消息含 "UNIQUE constraint" 被误判为唯一冲突 → 转 InvalidOperationException 掩盖真实数据错误 |
| ITM-193 | ProjectionCheckpointDbContext.cs:236-246 | 同型漏网：SQLite 分支裸匹配 + 过时 "P3-3 已知局限" 声明（姊妹全仓已统一修复）→ 误判使 TryCreateCheckpointAsync 返回 null、租约静默让出 |

### P3（25 项，代表项）

| 类别 | 项 |
|------|-----|
| 注释/文档三方不一致 | PostgreSqlMultiHost 覆盖 vs 条件化注释矛盾 ×2、PostgreSqlJsonbExtensions doc 滞后、PipelineBehavior "零闭包" 声明不符、MessageCatalog 单类型单描述符未文档化 |
| 守卫缺失 | SqliteJsonExtensions.OutboxByType、SqliteFtsExtensions 全文件、SqlitePerformanceOptimizer.GetDiagnosticsAsync、DapperBulkCopy extractor 长度、leaseDuration 非负（四家均匀缺口） |
| 资源 | Sqlite Scoped 工厂 ApplyOptimization 抛异常连接未 Dispose、KafkaBroker Dispose 前无 Flush（[推断]）、SystemCompressor GZip 输入拷贝 |
| 错误流 | ExceptionMiddleware HasStarted 分支无日志、InboxProcessor pending-confirmation 无指标、DapperBulkCopy MySQL 非原子+RowsInserted 未检 |
| 生成语义 | IdentityGenerator CS0282（readonly partial record struct 跨声明）、嵌套 containing 非 partial → CS0260、CodeFix 基类链不对称、MatchEventNameCodeFix 首个字面量、AddVersionSuffix v2.v3 残留 |
| 其他 | OutboxInterceptor Detach 扩面（手动 Add 消息误 Detach）、DapperEventLog 读头注释"流式"不符、LoggingBehavior 查询打 Command 日志、MessageConsumeContext 判空语义边界、DomainEventDispatcher 中英混排、Saga Dynamic 观察者口径 |

## 段 4：证伪记录（4 项）

| # | 候选 | 反证 |
|---|------|------|
| 1 | ServiceRegistration HandlerMarker 双注册 → 双 Register | `Dispatcher.Register` 字典赋值 `_entries[key]=` 幂等，双调用无害 |
| 2 | readonly partial record struct 修饰符不一致报错 | 编译探针实证组合语义不报错（仅剩 CS0282 警告，已转 P3） |
| 3 | PalOrmOutboxStore sync-over-async 死锁 | PD10/PD11 已声明契约（宿主无 SyncContext 不死锁） |
| 4 | EFCore 族 Ulid Parse 无容错 | 二十九轮已定案族内一致设计 |

## 段 5：趋势与热点

- P1 由二十九轮的 4 降至 **1**，跨实现不对称（PD17/PD24）仍是主要复发根因——本轮 3 个 P0-P2 全部为"修姊妹时漏另一姊妹"（ITM-180 漏 Idempotency、ITM-188 漏 Saga/Checkpoint）
- 新误判库 PD30-32 生效：PD32（DI 顺序探针）成功避免 1 次容器探针外的无谓推理；PD31 未触发场景但第四评审轴已在环境诊断协议固化
- 子代理通道仍不稳定（全量档 4 片 2 次失败重派）——重派机制按协议正常工作，覆盖度完整

## 段 6：局限声明

- 片 4 清单部分文件不存在（PalResult/UlidFactory 等），已由 `ls` 找真实宿主文件补齐覆盖
- 片 1 的 KafkaProducer Flush 疑点 [推断]（Dispose drain 行为未实证），P3 修复前先补探针
- 全量测试 973/973 于评审环境执行，无残留环境失败
