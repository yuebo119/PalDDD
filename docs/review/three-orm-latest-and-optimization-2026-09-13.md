# 三 ORM 最新版本特性梳理与极致优化分析

> 报告编号：THREE-ORM-OPT-2026-09-13
> 版本时点:Dapper 2.1.79(2026-05)/ Dapper.AOT 1.1.0(2026-09-11)/ EF Core 10 GA + 11 RC1(本仓钉 11.0.0-rc.1)/ PalORM 5.5.1(自研,2026-09)
> 方法:官方 release notes / 本地 git 变更史 × 本仓源码逐点核对(每条"能用/不用"均给证据)

---

## 一、最新版本特性 × 本仓适用性

### Dapper 2.1.79(本仓已用)

| 上游特性 | 本仓判定 |
|---|---|
| enum TypeHandler 优先修复(#2200) | ✅ 已受益——Ulid/Guid/DateTimeOffset handler 语义更确定 |
| ICustomQueryParameter 值类型段错误修复(#2189) | ✅ 稳定性受益 |
| QueryUnbuffered 性能 + First API 正确性(2.1.66 #2121) | ✅ 已受益——本仓 QueryFirst/FirstOrDefault 调用点 |
| net10.0 TFM 打包(2.1.72 #2195) | ✅ 已受益 |
| SqlBuilder/OutputExpression | ➖ 未用(手写 SQL 模板,无动态构建需求) |
| dynamic 对象值类型属性修复 | ➖ 未用(全具体类型物化) |

### Dapper.AOT 1.1.0(本仓已用,全量启用)

| 上游特性 | 本仓判定 |
|---|---|
| 拦截器接线(.NET 11 实测正常) | ✅ 已用——34 调用点 |
| 声明式 `[Dapper.TypeHandler]`(shim 经典 handler) | ✅ 已用——Ulid/Guid/DateTimeOffset |
| List expansion(`where id in @ids` 生成) | ⚠️ 部分受益——byte[] 误展开需 DynamicParameters 绕行(实验报告坑 3,已修);真 List 参数本仓暂无调用点,未来可用 |
| CommandBehavior 对齐 vanilla(修 async 单行 ~10x 慢) | ✅ 已受益 |
| DAP001/DAP057 显式拒绝(替代 1.0.x 静默丢弃) | ✅ 依赖中——"静默失败"防线 |
| DynamicParameters 官方支持 | ✅ 已用——blob 参数绕行路径 |
| `Command<T>` 管道 ct 参数 | ⏳ **待上游入口**——管道支持 ct 但调用点语法无通道(CommandDefinition 拒/直接重载无 ct),值得提 issue |

### EF Core 10 GA(2025-11)/ 11 RC(本仓已用 11 rc.1)

| 上游特性 | 本仓判定 |
|---|---|
| to-one join 优化(EF 11:split query **-29%**/ORDER BY 去冗余 **-22%**) | ✅ GA 后自动受益(读路径 Include/投影,零代码) |
| no-op CAST 剥离(EF 11) | ✅ GA 后自动受益 |
| ExecuteUpdateAsync JSON 列支持(EF 10) | ➖ 未用(本仓无 JSON complex type 映射) |
| 参数化集合标量展开 + padding(EF 10) | ➖ 未用(本仓 EFCore 无 IN 集合查询;Dapper 栈有但形状不同) |
| 命名查询过滤器(EF 10) | ➖ 未用(本仓无全局过滤器/软删/多租户 EF 实现) |
| Complex types EF 10 大扩展(struct/optional/JSON)/ EF 11 TPT-TPC | ➖ 未用(本仓 EFCore 栈映射全部平铺实体) |
| SQL Server 向量/JSON 类型/全文目录 | ➖ 不适用(本仓 EFCore 面向 PG/MySQL/SQLite) |
| 迁移 `--add` 一步化 / 快照迁移 ID 分叉预警 / `.config/dotnet-ef.json` | ✅ 对 EFCore 栈消费者的 DX 受益(本仓自身用手写 DDL 体系) |
| Compiled models / precompiled queries | ➖ **有意不用**——本仓 EFCore 栈无冷启动瓶颈报告,预编译只省启动;等上游转正再评估 |

### PalORM 5.4 / 5.5.x(自研上游,本仓钉 5.5.1)

| 上游特性 | 本仓判定 |
|---|---|
| 5.4 弹性层 `configureResilience` 回调(Provider 瞬时异常重试) | ➖ 未接——本仓 PalDDD 层未表达重试需求;外层 Outbox 重试(OutboxBatchProcessor)已承载业务级重试,两层重试叠加反而制造重复投递风险 |
| 5.5.0 `[SensitiveData]` 脱敏信道 | ➖ 未接——本仓连接串经 appsettings 注入,DI 注册面无敏感数据经 PalORM 诊断信道输出 |
| 5.5.0 WhereJson 方言守卫 / 方言词法与可观测性 | ✅ 被动受益(上游质量修复) |
| 5.5.0 外部事务语义变更 | ✅ 已核对——本仓适配层外部事务用法(UoW 挂接)语义兼容 |
| 5.5.1 纯依赖升级(Roslyn 5.9/Sqlite.Core rc.1/NU1903 清零) | ✅ 已用 |

**结论**:三栈当前钉的版本无"放着不用的大收益特性";未采用的特性全部有明确理由(场景不存在/已有替代/双保险反风险)。

---

## 二、极致优化分析(现状盘点 → 剩余空间 → 裁决)

### 已就位的优化(盘点确认,无需再动)

| 栈 | 已做 |
|---|---|
| Dapper | 拦截器全量接管(JIT 省反射 emit + AOT 全链路);SQL 模板全 const(Npgsql MaxAutoPrepare=20 自动预备,PostgreSqlMultiHost 强制);连接池由驱动层管理(Npgsql/MySqlConnector Pooling);blob 绕行 DynamicParameters;声明式 TypeHandler |
| PalORM | BulkInsertAsync 方言最优路径(PG COPY / MySQL BulkCopy / SQLite 多值 INSERT,batchSize 1000);源生成物化零反射;租约 SQL 单语句原子 |
| EFCore | 写路径全 ExecuteUpdate(单语句,绕 ChangeTracker);读路径全 AsNoTracking(6 处);CAS 乐观并发 |

### 剩余优化点(按收益/代价比排序,附裁决)

**P1|EFCore 栈 DbContext Pooling 解锁(唯一真机会,待裁决)**

- 现状:`OutboxDomainEventInterceptor` 的 `_pending`/`_injectedOutboxIds` 是实例级可变状态,与 `AddDbContextPool` 互斥(ITM-640,三十八轮"声明不修")——本仓 EFCore 栈每请求重建 DbContext。
- 解法:把两块状态从拦截器实例迁到 **DbContext 派生类字段**(拦截器经 `eventData.Context` 取用)——`AddDbContextPool` 每次租出自动 `ResetState`,天然池化安全。这是 EF 官方推荐的 per-context 拦截器状态模式。
- 代价:**API 形状变化**——消费者的 TContext 需要继承一个 `OutboxEventCapableContext` 基类(或实现接口)承载状态;破坏性变更需走 major 窗口。
- 收益:[推断] 中等——省 DbContext 构造/检查器初始化,但相对网络 IO 是小头;高吞吐 EFCore 消费者受益明显。
- **裁决建议:登记 ITM,排入下一个 major 窗口(与 IPalOutboxStore 异步化同批),本窗口不动。**

**P2|Dapper 栈 ct 恢复(上游依赖,非本仓工作量)**

- `Command<T>` 管道已支持 ct,只差调用点语法入口(见特性表)。向上游提 issue 请愿;落地后本仓 34 调用点改形状即可恢复 SQL 执行层取消。
- **裁决建议:起草 issue 文本,跟踪上游;本仓不动。**

**P3|PalORM 同步 DIM 阻塞(已被 ADR-020 吸收,无独立动作)**

- `AddMessage`/`MarkProcessed` 同步方法在 PalORM 栈被迫 `GetAwaiter().GetResult()`——解药是 IPalOutboxStore 异步化(已排队 major 窗口)。**无独立动作。**

**明确不做(反过度优化,各附理由)**:

| 项 | 不做理由 |
|---|---|
| EFCore Compiled Models / precompiled queries | 只省启动时间;本仓无冷启动瓶颈证据;上游 experimental(Karpathy 懒惰阶梯:无瓶颈不优化) |
| PalORM `configureResilience` 接入 | 外层业务重试已存在(OutboxBatchProcessor);两层重试叠加 = 重复投递风险 |
| Dapper MySQL/SQLite 语句预备 | 仅 PG 有 MaxAutoPrepare 且已配;MySqlConnector 预备收益未测量,无瓶颈证据不引入 |
| 批量 lease/批量 MarkProcessed | 现有单语句原子租约已是最优形状(全守卫内联);批量化改变 fencing 语义,风险 > 收益 |
| 序列化/诊断/连接复用跨栈优化 | MemoryPack/TimeProvider/驱动池化均已就位,无新空间 |

### 总结

三栈在各自机制内的热路径已处于"诚实可证"的优化态:本轮唯一剩余的真机会是 **EFCore pooling 解锁**(API 代价,major 窗口),其余为上游跟踪项与"有理由不做"项。极致优化的下一杠杆不在 ORM 层——在接口异步化(major 窗口)与上游 ct 入口。
