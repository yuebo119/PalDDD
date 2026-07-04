# 审计文档命名规范

> 范围：`docs/review/` 目录下的审计产出。
> 全局命名规范（项目/接口/类型/文档文件）见 `docs/conventions.md` §3。
> 核心规则：**`{type}-{date}[-v{n}].md`**。禁止主观形容词。

---

## 一、命名模式

| 文档类型 | 命名格式 | 示例 |
|----------|---------|------|
| **审计报告** | `audit-{YYYY-MM-DD}.md` | `audit-2026-06-30.md` |
| **审计报告（同日多份）** | `audit-{YYYY-MM-DD}-v{n}.md` | `audit-2026-06-29-v2.md` |
| **审计补充** | `audit-supplement-{YYYY-MM-DD}.md` | `audit-supplement-2026-06-30.md` |
| **任务清单** | `action-items-{YYYY-MM-DD}.md` | `action-items-2026-06-30.md` |
| **任务清单（同日多份）** | `action-items-{YYYY-MM-DD}-v{n}.md` | `action-items-2026-06-29-v2.md` |
| **元审计报告** | `meta-audit-{YYYY-MM-DD}.md` | `meta-audit-2026-06-30.md` |
| **元审计报告（同日多份）** | `meta-audit-{YYYY-MM-DD}-v{n}.md` | `meta-audit-2026-06-30-v3.md` |
| **模板** | `{TYPE}_TEMPLATE.md` | `REVIEW_TEMPLATE.md` |
| **本规范** | `NAMING.md` | — |

---

## 二、语法规则

### 规则 1：字段顺序

```
{type}-{date}[-v{version}].md
  ↑       ↑        ↑
  类型    日期     可选版本号
```

**错误示例**：
- `comprehensive-review-2026-06-28.md` — 类型词不在最前
- `serena-comprehensive-review.md` — 工具名混入类型
- `meta-audit-v2-2026-06-30.md` — 版本号在日期前
- `definitive-audit-2026-06-30.md` — 主观形容词禁止

### 规则 2：日期格式

- `YYYY-MM-DD`，四位年 + 两位月 + 两位日
- 单数月/日前加 `0`：`06-05`，不是 `6-5`

### 规则 3：版本号格式

- `v{n}`，n 从 1 开始递增
- 同日第一份不加版本号（版本号隐含为 v1）
- 版本号反映**产出顺序**，不是重要性：`v7` 比 `v5` 晚产出

### 规则 4：全小写 + 连字符

- 禁止：空格、下划线（`_`）、中文字符、大写字母（模板除外）
- 正确：`audit-2026-06-30.md`
- 错误：`Audit_2026_06_30.md`、`审计报告-2026-06-30.md`

### 规则 5：禁止主观形容词

以下词汇禁止出现在文件名中：
`final` `definitive` `ultimate` `comprehensive` `full` `complete` `professional` `qualitative` `recursive`

**如果一份报告确实比前一份更完整——用版本号区分，让读者自己判断**。

---

## 三、查找规则

| 需求 | 命令 |
|------|------|
| 最新审计报告 | `ls -t docs/review/audit-20*.md \| head -1` |
| 最新任务清单 | `ls -t docs/review/action-items-*.md \| head -1` |
| 最新元审计 | `ls -t docs/review/meta-audit-*.md \| head -1` |
| 全部审计报告（按时间） | `ls -t docs/review/audit-20*.md` |
| 某日的所有产出 | `ls docs/review/*2026-06-30*` |

---

## 四、类型定义

| 类型前缀 | 含义 | 何时产生 |
|---------|------|---------|
| `audit-` | 对代码的审计报告。6 流逐行审计 + 10 维度评分 + 发现清单。 | `/audit` 命令执行后 |
| `audit-supplement-` | 审计补充报告。发现于审计后或针对特定维度的深度验证。 | 主审计报告后发现遗漏时 |
| `action-items-` | 可执行的任务清单。每项含优先级（危害×复杂度）+ 验证方式 + 涉及文件。 | 审计报告产出后，将发现转化为任务时 |
| `meta-audit-` | 对审计过程本身的审计。评估审计质量、识别误判、提炼改进方案。 | 审计轮次完成后 |

---

## 五、版本号演进示例

同一天产出多份审计报告时：

```
audit-2026-06-29.md        → 第 1 份（原始 Serena 评审）
audit-2026-06-29-v2.md     → 第 2 份（增加了符号级分析）
audit-2026-06-29-v3.md     → 第 3 份（补充 SQL 安全分析）
audit-2026-06-29-v4.md     → 第 4 份（最终综合版）
```

同一天产出多份元审计时：

```
meta-audit-2026-06-30.md      → 第 1 轮（9 项错误）
meta-audit-2026-06-30-v2.md   → 第 2 轮（14 项错误）
meta-audit-2026-06-30-v3.md   → 第 3 轮（全范围，25 项）
meta-audit-2026-06-30-v4.md   → 第 4 轮（递归审计）
meta-audit-2026-06-30-v5.md   → 第 5 轮（定性审计）
meta-audit-2026-06-30-v6.md   → 第 6 轮（8 段专业框架）
meta-audit-2026-06-30-v7.md   → 第 7 轮（终结）
```

---

## 六、文件清单

| 文件 | 类型 | 日期 | 状态 |
|------|------|------|:--:|
| `REVIEW_TEMPLATE.md` | 模板 | — | 活跃 |
| `ACTION_ITEMS_TEMPLATE.md` | 模板 | — | 活跃 |
| `NAMING.md` | 规范 | — | 活跃 |
| `audit-2026-06-30.md` | 审计报告 | 2026-06-30 | 最新 |
| `audit-supplement-2026-06-30.md` | 审计补充 | 2026-06-30 | 最新 |
| `action-items-2026-06-30.md` | 任务清单 | 2026-06-30 | 进行中 |
| `audit-2026-06-29-v4.md` | 审计报告 | 2026-06-29 | 历史 |
| `audit-2026-06-29-v3.md` | 审计报告 | 2026-06-29 | 历史 |
| `audit-2026-06-29-v2.md` | 审计报告 | 2026-06-29 | 历史 |
| `audit-2026-06-29.md` | 审计报告 | 2026-06-29 | 历史 |
| `audit-supplement-2026-06-29.md` | 审计补充 | 2026-06-29 | 历史 |
| `audit-2026-06-28.md` | 审计报告 | 2026-06-28 | 历史 |
| `action-items-2026-06-29-v2.md` | 任务清单 | 2026-06-29 | 已完成 |
| `action-items-2026-06-29.md` | 任务清单 | 2026-06-29 | 已完成 |
| `meta-audit-2026-06-30-v7.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30-v6.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30-v5.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30-v4.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30-v3.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30-v2.md` | 元审计 | 2026-06-30 | 历史 |
| `meta-audit-2026-06-30.md` | 元审计 | 2026-06-30 | 历史 |

---

## 七、常见错误对照

| 错误 | 原因 | 正确 |
|------|------|------|
| `final-audit.md` | 主观形容词; 无日期 | `audit-2026-06-30.md` |
| `Audit-Report-2026-06-30.md` | 大写; 多余词 | `audit-2026-06-30.md` |
| `audit_2026_06_30.md` | 下划线而非连字符 | `audit-2026-06-30.md` |
| `audit-2026-6-30.md` | 月不足两位 | `audit-2026-06-30.md` |
| `review-2026-06-30.md` | 类型词错误（review≠audit） | `audit-2026-06-30.md` |
| `serena-audit-2026-06-30.md` | 工具名混入类型 | `audit-2026-06-30.md` |
