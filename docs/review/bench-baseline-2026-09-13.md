# 三栈持久化基准基线(X1 首批数字)

> 基线编号:BENCH-BASELINE-2026-09-13
> 环境:AMD Ryzen 9 8945HX(16C/32L)· Windows 10 · .NET SDK 11.0.100-rc.1 · BDN 0.15.8(InProcessEmitToolchain,ShortRun,InvocationCount=1)
> 口径:SQLite 命名共享内存库;BatchSize=100;耗尽型(Lease)每 invoke 含重灌,纯查询对照(GetPending)不含
> 运行:`dotnet run --project bench/PalDDD.Benchmarks -c Release -- --persist`
> 代码:bench/PalDDD.Benchmarks/PersistenceBenchmarks.cs(三栈 × 5 基准 = 15 项)

---

## 首批数字(Outbox 单位 us;Allocated 单位 KB)

| 基准 | Dapper | PalORM | EFCore |
|------|-------:|-------:|-------:|
| Outbox_GetPending_Batch100(纯查询,基线) | 507 | 554 | 970 |
| Outbox_Lease_Batch100(满额租约+重灌) | 3,091 | 3,979 | 15,669 |
| Outbox_AddSingle | 64 | 72 | 188 |
| EventLog_AppendSingle | 119 | 114 | 479 |
| Idempotency_TryStart_Completed | 102 | 65 | 447 |
| **GetPending 分配** | 75.5 KB | 83.0 KB | 102.2 KB |
| **Lease 分配** | 859.7 KB | 1,019.4 KB | 3,650.4 KB |

## 首批可验证结论(替代此前全部 [推断])

1. **EFCore 栈全线最慢,且不是常数因子**:Lease 5.1× Dapper / 3.9× PalORM;AddSingle 2.9× / 2.6×;Idempotency 4.4× / 6.8×。分配面同样放大(Lease 3.65 MB vs 860 KB)。**E1(Pooling 解锁)的预期收益需重估**——数字显示 EFCore 慢的主体不在 DbContext 构造(AddSingle 188us 里构造占比有限),而在 EF 管道本身(翻译/物化/SaveChanges 批处理)。E1 仍值得做(major 窗口),但"显著改善"预期降级为"部分改善";EFCore 栈的真实提速杠杆可能还要叠加预编译查询(上游 experimental,维持不做的裁决)。
2. **PalORM 与 Dapper 同量级,各有领先项**:Idempotency 全链 PalORM 反而最快(65us vs Dapper 102us——源生成参数化路径赢);GetPending Dapper 最快(507 vs 554);EventLog 基本打平(119 vs 114)。**三栈热路径"同一量级"的判断被数字证实**(orm-optimization-deep-analysis 的结论 1)。
3. **Lease 分配热点实锤**:三栈 Lease 都是 GetPending 的 11-12× 分配——重灌(100 条 Add)贡献大部分,但 EFCore 的 3.65 MB 远超重灌解释范围(EF 实体物化+ChangeTracker),与 E1 动机一致。
4. **InMemory vs 真库落差**:InfraBenchmarks 的 InMemory Lease 基准与此处真库数字相差一个数量级——持久化成本主体在 I/O 与驱动,框架层优化空间(此前争议焦点)被数字压缩。

## 基线纪律

- 本表数字为 **ShortRun 初基线**(误差条偏大,EFCore Lease StdDev 7.3ms)——裁决级决策前用 `--job medium` 复测。
- 后续任何三栈改动(如 E1 落地)跑同口径对比,Ratio 偏离 >10% 需解释。
- 数字随机器/版本漂移,**禁止把绝对值写入规范文档**;引用一律带环境上下文(PD34 同源教训)。

## 已知口径限制(诚实声明)

- InProcess 工具链:测量与被测同进程,GC 干扰略高于进程隔离的 CsProj 工具链(三栈间相对比较有效,绝对值偏保守)。
- SQLite :memory::无网络/磁盘 I/O——放大了框架层差异占比,真实 PG/MySQL 部署下三栈差距会进一步收窄。
- IterationSetup 含清表+重灌+新 context(EFCore),Lease 数字含这些成本;GetPending 对照不含。
