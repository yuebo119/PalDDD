# Dapper.AOT 1.1.0 启用可行性探针实证报告

> 报告编号：DAP-AOT-PROBE-2026-09-13
> 方法：scratch 探针项目（net11.0 + Dapper 2.1.79 + Dapper.AOT 1.1.0）NativeAOT 二进制实跑，
> 全部结论为本地可复现实证，非文档推断。
> 触发：三栈极致性分析推荐 Dapper.AOT 化 → 用户批准 → 实证中发现推翻前提的新事实 →
> 升级用户裁决 → 裁决"铺垫不动"。

---

## 1. 探针矩阵（全部 [事实]，AOT publish 实跑）

| # | 调用形状 | JIT | NativeAOT | 判据 |
|---|---------|:---:|:---:|------|
| A | `CommandDefinition` + const SQL | ✅ | ⚠️ 未拦截（DAP057 info 级） | 生成文件无该调用点 |
| B | 直接重载 + const 直引 | ✅ | ✅ 拦截 | 生成 `InterceptsLocation` 指向调用点 |
| C | 直接重载 + 实例属性传 SQL | ✅ | ✅ 拦截 | 属性被编译期常量追踪 |
| D | 直接重载 + switch 选 const（方言分支形状） | ✅ | ✅ 拦截 | switch 常量传播被支持 |
| E | `CommandDefinition` 无参数 | ✅ | ✅ 运行正常 | 零反射需求 |
| F | **`CommandDefinition` + 匿名对象参数（本项目全部调用点形状）** | ✅ | ❌ **`PlatformNotSupportedException`** | `SqlMapper.CreateParamInfoGenerator` 走 Reflection.Emit，AOT 禁用 |
| G | 声明式 `[Dapper.TypeHandler]` 消费经典 `SqlMapper.TypeHandler<T>`（shim） | ✅ | ✅ | 1.1.0 新特性实证 |
| H | 声明式 TypeHandler **未启用** DapperAot | — | ❌（运行时注册缺失即炸） | 声明式不被消费，运行时注册是必要兜底 |

辅助事实：
- Dapper 直接重载**无 ct 参数**（`CS1739: 最佳重载没有名为 cancellationToken 的参数`）——ct 只能经 `CommandDefinition` 传递，v17 轮全量改 `CommandDefinition` 正是为此。
- 生成拦截器仍用 `InterceptsLocation(string,int,int)` 旧属性形式，但 **.NET 11 SDK 下接线成功**（JIT 堆栈走生成代码 + AOT 二进制实跑双证据）。
- DAP057 警告原文自证失败面："This call-site is left on vanilla Dapper, which will not work under native AOT"。

## 2. 与既有结论的三处对撞

1. **c-sharpcorner 文章《Dapper.AOT Is Broken on .NET 10》**：指控 1.0.52 拦截器不接线。
   对 1.1.0（2026-09-11 发布）**不成立**——1.1.0 实测接线。文章发布时 1.1.0 未出。
2. **issue DapperLib/DapperAOT#148**（open，19 个月无响应）：旧属性形式被 Roslyn 弃用。
   实测 .NET 11 SDK（11.0.100-rc.1）仍接受该形式——issue 的通用性存疑，1.1.0 自带
   file-local attribute 定义可能绕开了弃用面。
3. **本项目二十五轮 A5 勘正**（声称 1.0.52 的 `TypeHandlerAttribute<,>` 完整双向面可用）：
   被 1.1.0 官方 release note 推翻——"The old generic `[TypeHandler<,>]` **never did anything** and is now obsolete"。
   文档声明（无论上游 README 还是本地 XML doc）都不是能力终审；**可运行实证才是**。

## 3. 关键取舍：ct 与 AOT 拦截互斥

```
ct 传递 ──只能──▶ CommandDefinition ──DAP057 拒绝──▶ 无拦截器
AOT 拦截 ──只支持──▶ 直接重载 ──无 ct 参数──▶ SQL 执行层取消能力消失
```

全量 AOT 化 = 60+ 调用点改形状 + ct 语义收缩（查询不可中断，连接超时兜底，优雅关停时
正在执行的 SQL 自然完成后退出）。而架构上 AOT 需求已由 PalORM 栈承担（AOT-ANALYSIS-2026-09-13 定案），
Dapper 栈定位 JIT 场景——ct 收缩换 AOT 真兼容的收益在退役路线（ADR-020）下趋近于零。

## 4. 用户裁决（2026-09-13）

**"铺垫不动"**：升级 1.1.0 + 声明式 TypeHandler 铺垫 + InterceptorsNamespaces 补齐；
调用点不动（ct 保留）；AOT 状态不变（仍为经典路径），未来启用只差一步。

已执行的铺垫改造：
| 文件 | 改动 |
|------|------|
| `Directory.Packages.props` | Dapper.AOT 1.0.52 → 1.1.0（升级理由注释） |
| `src/PalDDD.Dapper/PalDDD.Dapper.csproj` | `InterceptorsNamespaces` 补齐（net10+ SDK 只认新名）；注释重写（1.1.0 实证 + 启用动作清单） |
| `src/PalDDD.Dapper/DapperAotInitializer.cs` | 声明式 `[Dapper.TypeHandler]` ×3（未启用时不被消费，探针 H 实证无效无害）；头注重写（实证矩阵 + 启用前置）；运行时注册保留 |
| `SqliteTypeHandlers.cs` / `SqliteRowFactory.cs` / `DapperSagaStateStore.cs` | 行号引用改符号引用（防漂移）；AOT 状态注释精确化 |

未来启用动作清单（五步）：① `[module: DapperAot]` 启用 ② 调用点 `CommandDefinition`→直接重载
（ct 收缩需重新裁决）③ 删运行时注册（声明式接管）④ csproj 移除 NoWarn 的 DAP005
⑤ NativeAOT 发布实测。

## 5. 验证

- `dotnet build PalDDD.slnx -c Release`：0 警告 0 错误。
- 无外部依赖测试 14 项目 1212 全绿（CI 同款逐项目口径）。
- `PalORM.Tests` / `Messaging.Integration.Tests` 非 Testcontainers 环境非零退出——环境守卫
  （需 Docker），两项目均不引用 Dapper 栈，与本次变更无关联（git diff 六文件 + grep 引用核实）。
