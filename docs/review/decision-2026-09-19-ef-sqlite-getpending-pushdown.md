# 决策论证：EF SQLite GetPending 谓词下推（P-2 另案）

> 编号：DECISION-2026-09-19-getpending-pushdown
> 基线：commit `6faee53` · 日期：2026-09-19
> 性质：实施决策（decision-2026-09-17 §2.5-4 明确遗留的另案）
> 结构约束：本模板三段（消费状态/证据锚点表/开放决策点）为 V11 门禁必填段——
> `dotnet run scripts/verify-conventions.cs -- --quick` 机械校验段存在与锚点格式。

---

## 消费状态

> 决策文档的生命周期账本：落盘 ≠ 可采纳。评审逐行回查锚点表并全部置 ☒ 后，
> 状态从「未评审」流转为「已评审」；开放决策点经用户拍板后为「已裁决」。

| 项 | 值 |
|------|------|
| 落盘日期 | 2026-09-19 |
| 评审状态 | **已实施完毕**（2026-09-19）：前置一表征 3 用例 + 前置二 FromSqlRaw spike（物化 3 行/实体完整/组合 Count 可用）→ 下推落地 → 双态全绿（Integration 296：表征 3 用例下推前后皆绿 + SQL 次数锁定=1） |
| 评审记录 | 反方 8 项：①【高】原稿锚点表把 P-1 段落的 48.8 倍误作 P-2 定量证据（audit :290 属 P-1，P-2 段落纯定性）——已修正；②【高】FromSqlRaw SELECT 物化（非组合式，SQL 内 ORDER BY+LIMIT）仓内零先例零 spike（spike 三项只验了 ExecuteSqlAsync UPDATE；三栈 EF 先例均为组合式 .OrderBy().Take()）——已列为实施前置 spike；③【高】QueryEligibleAsync 翻页/时间过滤语义无 SQLite-EF 生产类行为测试（OutboxEfCoreTests 走 InMemory 变体不过 SqliteOutboxDbContext；DialectProbe 为 Dapper 栈）——已列为实施前置表征测试；④【中】ORDER BY CreatedAt 在 OR 谓词下索引不保序（审计 P-3 自录临时 B-tree），下推后仍可能全扫+排序——验收预期修订为「消除 N 页往返与重复物化」而非「与表大小无关」；⑤【中】伸缩性验收种子须全为未来重试行（否则对照归零）；生产唯一调用方健康检查 batchSize=1，现状实为逐行翻页更差；⑥【中】UTC 前提 remarks 未覆盖 CreatedAt 列（ORDER BY 新读的列）——remarks 扩列；⑦【低】ULID↔CreatedAt 等价声明跨进程不完备（两键对称退化，攻击力有限，记录在案）；⑧【低】V11 只校验锚点格式不校验指向（两处行号失准已修；门禁边界如实认知） |
| 裁决记录 | 开放决策点 1-3 按推荐执行（用户连续「继续」授权）：①QueryEligibleAsync 已删（零调用方私有方法，净简化）；②验收 = 结构性 SQL 次数锁定（300 行全未来表恒 1 条命令，防翻页回归）+ 表征双态绿，不设时间比值门；③排序键换 CreatedAt 已落地（同进程双序一致由表征测试运行时验证） |

## 证据锚点表

> 每个可验证的关键声明（数字 / 行为 / 覆盖现状）一行。来源锚 = 仓内 `文件.md:行号`
> 或 `文件.cs:行号`；**查不到仓内出处的声明如实填「仓内未定位」并留 ☐，禁止
> 「审计归档」类无锚引用**。核对列由评审逐行打开来源核对后置 ☒——全部 ☒ 才算消费完成。

| 关键声明 | 来源锚 | 核对 |
|----------|--------|:--:|
| P-2 现状：`QueryEligibleAsync` 稳态退避下最坏全表分页扫描（页数=表/batchSize），时间过滤在内存；生产唯一调用方健康检查 batchSize=1，现状实为逐行翻页（页数=表行数） | SqliteOutboxDbContext.cs:41 | ☒ |
| P-2 审计定级「高 [事实]」（段落纯定性，无定量数字——反方勘正：48.8 倍属 P-1 段落） | audit-2026-09-15-full.md:292 | ☒ |
| Lease 批量化后 `QueryEligibleAsync` 仅剩 GetPending 一个调用方（Lease 已走单语句 SQL） | SqliteOutboxDbContext.cs:89 | ☒ |
| ExecuteSqlAsync UPDATE 路径已验证（谓词/参数/回读三项 spike）；**FromSqlRaw SELECT 非组合式物化（SQL 内 ORDER BY+LIMIT）零先例零 spike——实施前置**（三栈 EF 先例均为组合式 .OrderBy().Take()，SQLite 因 ITM-261 不可组合） | SqliteOutboxDbContext.cs:121 | ☒ |
| 下推谓词与 Lease 子查询为同一谓词族（Status/RetryCount/NextAttemptAt/LockedUntil + UTC 前提）——三栈 GetPending 同为 SQL 谓词下推形态 | SqlTemplates.cs:157 | ☐ |
| GetPending 契约只读（观测/健康检查，AsNoTracking，不获取租约）——下推不触碰租约面 | OutboxDbContext.cs:79 | ☒ |
| GetPending medium 基线未单独测过（ShortRun 970us vs Dapper 507us 仅为参考，非裁决级） | bench-baseline-2026-09-13.md:15 | ☒ |
| ORDER BY CreatedAt 在 OR 谓词下索引 (Status,NextAttemptAt,CreatedAt) 第三列不保序（审计 P-3 自录临时 B-tree 排序）——下推后仍可能单次全扫+排序 | audit-2026-09-15-full.md:298 | ☒ |

## 开放决策点

> 需用户拍板的取舍，附推荐项与放弃理由。无则写「无——本决策不改变行为契约」。

1. **`QueryEligibleAsync` 删除还是保留**（下推后零调用方）？推荐：**删除**——私有方法无外部消费面（非框架库公共 API），代码价值判定五项中"性质/测试"均不命中保留证据，且保留即死代码违背本仓惯例。
2. **验收形态**：推荐**伸缩性对照**（万行表上 GetPending 耗时应与表大小无关：下推前全表翻页 ~100 页查询，下推后 1 页），不以时间比值为门（Lease 验收已证基准口径陷阱，P-2 的本质是 O(表)→O(batch) 伸缩性）。
3. **排序键从 Id 换 CreatedAt**（对齐 Lease 与三栈）：推荐**换**——ULID Id 序与创建序语义等价是仓内既有声明，统一后两路径同序。行为差异如实记录于 remarks。

---

## 0. 结论速览

| 项 | 建议 | 一句话理由 |
|---|---|---|
| **GetPending 谓词下推** | **做**（raw SQL 单页 + 删 QueryEligibleAsync） | P-2 与 P-1 同根（ITM-261 被动形态），Lease 批量化已铺完全部技术与验证路径，本项是同一形态的最后一块 |

## 1. 问题

`QueryEligibleAsync` 因 ITM-261（EF SQLite LINQ 不能翻译 DateTimeOffset 有序比较）被迫"物化整页 + 内存时间过滤 + 翻页直至填满"。稳态退避下表头全是未来重试行时，每 tick 全表扫描——P-2 审计定级「高」。Lease 批量化（637e73d）已证：**同一谓词族在手写 SQL 里完全合法**（ITM-261 只限 LINQ 翻译，UTC 前提已由回归测试锁定）。GetPending 是该谓词族的最后一个消费者。

## 2. 方案

**前置一（表征测试）**：为 SQLite-EF 生产类补 GetPending 行为测试（到期取/不到期不取/batch 上限/排序键），锁定下推前语义（反方发现③——现状该面零覆盖）。

**前置二（spike）**：FromSqlRaw 非组合式 SELECT 物化验证（SQL 内 ORDER BY CreatedAt LIMIT + 全列返回 + EF 实体映射，反方发现②）。

**实施**：`FromSqlRaw` 单页查询（谓词与 Lease 子查询逐字同族 + `ORDER BY CreatedAt LIMIT @batch`）+ EF 物化 + AsNoTracking（契约不变）。守卫随 QueryEligibleAsync 删除移入 GetPending override（ITM-659 形态）。时间参数直传原生 DateTimeOffset（ExecuteSqlAsync 路径已证，SELECT 路径由前置二验）。

## 3. 风险与验证

- 行为差异两处（排序键 Id→CreatedAt、内存过滤→SQL 过滤）语义等价性有仓内声明支撑（单进程形态；跨进程两键对称退化，反方发现⑦记录在案），由前置一的行为测试双态锁定（下推前后都跑）。
- 验收 = 伸缩性对照（修订，反方发现④⑤）：**全未来重试行**的万行表上，下推前后同表对照；预期修订为「消除 N 页往返与逐页重复物化」（索引不保序下 SQL 内仍可能单次全扫+排序，O(表·log表) 优于 O(表·页数) 但非与表大小无关）。
- 不触碰：租约面（Lease/Mark*/ReleaseForRetry）、公共 API、DDL；UTC 前提 remarks 扩覆盖 CreatedAt 列（反方发现⑥）。
