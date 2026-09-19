# Dapper.AOT 全链路替换实验报告（experiment/dapper-aot-full 分支）

> 实验编号：DAP-AOT-FULL-2026-09-13
> 分支：`experiment/dapper-aot-full`（自 dev `6accffc` 切出，**不自动合并**——dev 保持
> 2026-09-13"铺垫不动"裁决，是否采纳本实验成果由用户后续裁决）
> 目标：回答"Dapper.AOT 完全替换经典 Dapper，能否实现全链路 AOT"。

---

## 结论

**能——但必须限定边界：本分支实现的是「本仓 Dapper 栈 34 个调用点封装的 API 面」三方言
AOT 实测通过；Dapper 库本身（Dapper.dll）仍非 AOT 干净，库级「全链路 AOT」不成立。**
（2026-09-13 抑制声明审计后修正——原结论「与 PalORM 同级」为过度声称，PalORM 是库级
源生成零反射，纯度高于 Dapper 栈。）

| 验证层 | 结果 |
|--------|------|
| 拦截器接管 | ✅ 34 个调用点全量生成拦截器（含 const/实例属性/switch 方言分支三形状） |
| JIT 行为回归 | ✅ 无外部依赖 14 项目 1212 全绿 0 失败 |
| NativeAOT 编译 | ✅ publish 成功（上游库自带 trim 警告按既有口径 NoWarn） |
| NativeAOT 实跑 | ✅ 探针 sample 13/13 全 PASS（Outbox/EventLog/Idempotency/Saga 四组件 CRUD+blob 往返） |
| PG 真实库（<INTERNAL_TEST_HOST>, PG 18.4） | ✅ **JIT 13/13 + NativeAOT 二进制 13/13**（外部连接串模式，appsettings.test.local.json 凭据） |
| MySQL 真实库（<INTERNAL_TEST_HOST>, MySQL 8.4.11） | ✅ **JIT 13/13 + NativeAOT 二进制 13/13** |

**ct 代价（已知且实验目的下显式接受）**：直接重载无 ct 参数，SQL 执行层取消能力消失
（连接超时兜底；接口签名与连接打开层 ct 保留）。

## 改造清单

| 类别 | 内容 |
|------|------|
| 调用点形状 | 34 处 `new CommandDefinition(..., cancellationToken: ct)` → 直接重载（6 文件，机械剥壳脚本） |
| 启用开关 | `[module: DapperAot]` 启用；运行时 `SqlMapper.AddTypeHandler` ×3 退役（声明式接管）；`MatchNamesWithUnderscores` 保留 |
| csproj | 移除 NoWarn 的 DAP005；`InterceptorsNamespaces` 双属性已就绪 |
| Row 类型迁出 | `SagaStateRow`（DAP051 泛型包含拒绝）、`IdempotencyRow`/`ProjectionCheckpointRow`（private 嵌套静默跳过）→ 命名空间级 internal |
| byte[] 绕行 | Outbox AddMessage / EventLog AppendAsync / Idempotency MarkCompletedAsync → `DynamicParameters` + 显式 `DbType.Binary` |

## 实验发现的四个坑（全部实证）

1. **DAP051 泛型包含拒绝**：泛型类内嵌 Row 类型直接报错（有诊断，1.1.0 官方拒绝口径）。
2. **private 嵌套 Row 静默跳过**：非泛型类的 private 嵌套 DTO，生成代码文件无法引用，
   生成器**静默跳过且无任何诊断**——最危险的失败形态（调用点悄悄走经典路径）。1.1.0
   "refuse cleanly" 口径在此场景失灵，建议上游补诊断。
3. **byte[] 参数误判为 IN 列表**（1.1.0 List expansion 新特性副作用）：匿名对象里的
   `byte[]` 被生成 `SqlMapper.PackListParameters` 展开，SQL 变行值 → SQLite
   "row value misused"。经典 Dapper 对 byte[] 直接 blob 绑定（基线全绿的原因）——
   **生成器与经典路径行为分歧，建议报上游**（参数侧，issue #156 只覆盖结果侧）。
   绕行：`DynamicParameters` + `DbType.Binary`（1.1.0 官方支持路径）；`(object)` 强转
   无效（生成器穿透强转读源类型）。
4. **诊断盲区教训（OPS-3 变体）**：改库后只 `dotnet test --no-build` 测试项目，跑的是
   测试 bin 里**上一次成功构建的旧库 DLL**——两轮"仍 23 失败"假象。必须重建测试项目。
5. **`DapperIdempotencyStore` 时间参数方言适配缺失（ITM-667 引入，dev 亦存在）**：该 Store
   时间参数直传 `DateTimeOffset`，PG 真实库上 timestamptz 收到 SQLite TypeHandler 编码的
   "O" string 直接炸（`Writing values of 'System.String' is not supported ... 'timestamp
   with time zone'`）——违反姊妹 Store 的 `ToTimeParam` 方言编码约定，且 ITM-667 当时只测了
   SQLite 分支。本分支已修（补同款 `ToTimeParam`）；**此修复适用于主线 dev（与 AOT 无关的
   方言缺陷），建议独立回合回移植**。真库实测价值：容器/单方言测试永远抓不到这类缺口。

## snake_case 列名验证（关键风险点清零）

生成代码用 `NormalizedHash`/`NormalizedEquals` 规范化匹配列名（lowercase 归一），
**不依赖 `DefaultTypeMap.MatchNamesWithUnderscores` 全局设置**——snake_case 表结构下
Outbox/EventLog/Saga/Idempotency 四组件读路径往返全部 PASS（JIT+AOT 双验证）。

## 抑制声明审计（2026-09-13 补充——回应"Dapper.AOT 只是抑制了报错"质疑）

### 被抑制的警告分两层，性质不同

| 层 | 警告 | 内容 | 判定 |
|----|------|------|------|
| 编译期 | IL3058 | "引用程序集未打 IsAotCompatible 标"——**连微软官方 Microsoft.Data.Sqlite、本仓 PalDDD.Transactions 都在报** | 元数据声明级，与运行时安全无必然关系 |
| 发布期 | IL2104/IL3053 | **Dapper.dll 自带 46 条方法级 AOT/trim 警告**（--singlewarn 展开实证） | 库级真实问题，不是我们的调用点问题 |

### 46 条库级警告的方法分布（全部落在经典反射路径）

- `SqlMapper.CreateParamInfoGenerator`（经典参数绑定，DynamicMethod IL emit）×3
- `SqlMapper.GenerateValueTupleDeserializer` / `LoadReaderValue*`（经典读物化）×8
- `DefaultTypeMap.*`（FindConstructor/FindExplicitConstructor/GetPropertySetter 等动态映射）×5
- `SqlMapper.DapperRow.*`（dynamic 支持面类型描述符）×8
- `CommandDefinition.GetInit` / `GetBasicPropertySetter`（DynamicMethod + 反射）×2
- `LookupDbType` / `StructuredHelper` / `GetOperator`（TypeHandler/表值参数 fallback）

**与拦截器生成代码路径（`Command<T>`/`RowFactory`/`UnifiedCommand`）完全正交。**

### 三重证据证明被警告代码不在本仓执行路径上（抑制 ≠ 安全，炸不炸是行为问题）

1. **反例先行（第一轮探针实证）**：走经典路径的调用点（匿名参数 `CommandDefinition`）在
   NativeAOT 下必炸 `PlatformNotSupportedException`，炸点正是 `CreateParamInfoGenerator`
   ——证明抑制警告救不了危险代码，危险代码在该炸的场合照炸。
2. **构建期零 DAP 诊断**：1.1.0 对会落回经典路径的调用点显式报 DAP001/DAP057；
   本分支 34 个调用点全部被拦截，零 DAP 报告（构建日志实证）。
3. **三方言 NativeAOT 二进制实跑 13/13 ×3**：若执行路径进任何被警告方法，AOT 下必炸
   （同反例机理）。跑通 = 执行路径全部走生成代码。
   （辅助物证：AOT 二进制中 CreateParamInfoGenerator 等符号以**元数据名表**形式残留
   ——NativeAOT 保留反射名，非可执行代码；生成代码对经典 SqlMapper 的唯一引用是
   VanillaTypeHandler 接口 shim。）

### 精确边界声明（对外文档与未来声称以此为准）

| 场景 | AOT 下行为 |
|------|-----------|
| 只用本仓六接口封装（DapperOutboxStore 等 6 Store，34 调用点） | ✅ 三方言 AOT 实测通过 |
| 绕过封装直接用 Dapper API（CommandDefinition/dynamic/运行时 AddTypeHandler/未拦截重载） | ❌ 炸（经典反射路径仍在库内，上游 46 条警告对应代码） |
| Dapper.dll 库级「全链路 AOT」 | ❌ 不成立——官方文档自述 "does not support all Dapper features"，承诺范围就是被拦截调用点 |

**上游视角**：Dapper.AOT 官方文档明确 "the trimmer and the ahead-of-time compiler can
only keep code they can see" + "Do check that your ADO.NET provider is also happy with
native AOT - that part isn't ours to promise" + "PLEASE TEST YOUR CODE CAREFULLY"——
库级警告未在官方承诺内，调用点级正确性靠 DAP 诊断 + 使用者实测。

## 复现路径

```bash
# JIT 对照
dotnet run --project samples/PalDDD.DapperAotProbe -c Release
# NativeAOT 实测
dotnet publish samples/PalDDD.DapperAotProbe -c Release -r win-x64 -p:PublishAot=true
./samples/PalDDD.DapperAotProbe/bin/Release/net11.0/win-x64/publish/PalDDD.DapperAotProbe.exe
# 全量回归（CI 同款口径）
dotnet build PalDDD.slnx -c Release
for csproj in $(find test -name '*.csproj' ! -name 'PalDDD.Testing.csproj' ...); do dotnet test ...
```

## 后续选项（用户裁决）

- **A. 采纳**：合并实验分支到 dev（Dapper 栈**封装 API 面** AOT 三方言真库闭环 + ct 收缩）；
  CI 加 AOT job；ADR-020 退役计划重新评估时注意：Dapper 栈 AOT 纯度低于 PalORM
  （调用点级 vs 库级），对外文档须按「精确边界声明」措辞。
- **A'. 独立回移植**：无论 A/B，第 5 坑的 IdempotencyStore 方言时间参数修复应尽快进 dev
  （ITM-667 引入的真实方言缺陷，PG/MySQL 下 TryStartAsync 即炸，与 AOT 无关）。
- **B. 归档**：实验分支保留不合并，报告归档（第 28 轮同款"明确不做"惯例），
  dev 维持铺垫不动；未来 Dapper 栈长寿化或 AOT 部署需求出现时按本报告路径启用。
