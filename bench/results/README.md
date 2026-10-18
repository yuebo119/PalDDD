# bench/results · EPSL 数据真源

本目录按 EPSL §5「结果归档三层」存放**可复核的原始数据**，入库；报告与过程日志不入库。

| 层 | 位置 | 入库 | 说明 |
| --- | --- | --- | --- |
| 结论报告 | `bench/reports/` | 否 | 由 BDN artifacts 与 `docs/performance.md` 轮次段承载；可再生 |
| 过程日志 | `bench/logs/` | 否 | 保留最近 20 份；`--smoke`/全量批的控制台输出与退出码 |
| 数据真源 | `bench/results/`（本目录） | 是 | history JSON / 统一报告的信封数据等；每批提交 |

当前 Pal.DDD 尚无 PerfCli 式编排入口，报告与日志暂由 BDN artifacts（`bin/**/BenchmarkDotNet.Artifacts`，已 gitignore）与 `docs/performance.md` 承载；引入编排件后按上表三层落盘。归档纪律见 `perf-budgets.md` §7 与 EPSL v1.6 §5。
