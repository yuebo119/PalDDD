#!/usr/bin/env python3
"""CI 失败自诊断（unified v2.0，2026-08-20）：三通道把失败原因以 GitHub `::error`
控制台注解输出——GitHub 将其转为 check-run 注解，注解 API 公开可读，无需认证下载日志。

通道：① TUnit JSON 报告中的失败测试名；② 失败项目输出日志尾（最后 ~30 行）；
      ③ Verify 快照对（*.received.txt vs *.verified.txt）的首个差异上下文。

用法：python3 scripts/ci-failed-tests.py <项目名词干> [日志文件路径]
退出码：0=未发现可诊断信息；1=发射过诊断（仅作标记，调用方用 || true 兜底）。
"""
import glob
import json
import sys


def emit(message: str) -> None:
    # GitHub 注解单条上限；截断并压平换行
    print(f"::error ::{message[:250].replace(chr(10), ' | ')}")


def from_reports(stem: str) -> int:
    found = 0
    for report in glob.glob("TestResults/*.tunit-report.json"):
        if stem not in report:
            continue
        try:
            data = json.load(open(report, encoding="utf-8"))
        except (OSError, ValueError):
            continue
        for group in data.get("groups", []):
            for test in group.get("tests", []):
                if test.get("status") != "failed":
                    continue
                exception = test.get("exception") or {}
                message = (exception.get("message") or "")[:180]
                name = f"{test.get('className', '?')}.{test.get('methodName', '?')}"
                emit(f"FAILED {name} — {message}")
                found += 1
    return found


def from_log_tail(log_path: str) -> int:
    try:
        lines = open(log_path, encoding="utf-8", errors="replace").read().splitlines()
    except OSError:
        return 0
    # 2026-08-20 二次校准：[-40:][-12:] 只截到 MTP 汇总，失败明细（测试名+断言）在更高处——
    # 扩窗到尾部 120 行、发射尾部 30 条非空行（注解有数量上限，30 条足够覆盖 3 个失败块）
    tail = [ln for ln in lines[-120:] if ln.strip()][-30:]
    for ln in tail:
        emit(f"LOG| {ln}")
    return len(tail)


def from_verify_snapshots() -> int:
    found = 0
    for received in glob.glob("test/**/*.received.txt", recursive=True):
        verified = received.replace(".received.txt", ".verified.txt")
        try:
            got = open(received, encoding="utf-8", errors="replace").read().splitlines()
            want = open(verified, encoding="utf-8", errors="replace").read().splitlines()
        except OSError:
            continue
        for i, (g, w) in enumerate(zip(got, want)):
            if g != w:
                emit(f"SNAPSHOT {received}:{i + 1} received={g[:100]!r} verified={w[:100]!r}")
                found += 1
                break
        else:
            if len(got) != len(want):
                emit(f"SNAPSHOT {received} 行数 received={len(got)} verified={len(want)}")
                found += 1
    return found


def main() -> int:
    if len(sys.argv) < 2:
        print("用法: ci-failed-tests.py <proj_stem> [log]", file=sys.stderr)
        return 2
    found = from_reports(sys.argv[1])
    if len(sys.argv) > 2:
        found += from_log_tail(sys.argv[2])
    found += from_verify_snapshots()
    return 0 if found == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
