#!/usr/bin/env python3
"""CI 失败自诊断（unified v2.0，2026-08-20）：解析 TUnit JSON 报告中的失败测试，
以 GitHub `::error` 控制台注解格式输出——GitHub 将其转为 check-run 注解，
注解 API 公开可读，无需认证即可下载日志/工件。

用法：python3 scripts/ci-failed-tests.py <项目名词干>
匹配 TestResults/*<项目名词干>*.tunit-report.json 中 status == "failed" 的测试。
"""
import glob
import json
import sys


def main() -> int:
    if len(sys.argv) != 2:
        print("用法: ci-failed-tests.py <proj_stem>", file=sys.stderr)
        return 2
    stem = sys.argv[1]
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
                message = (exception.get("message") or "")[:140].replace("\n", " ")
                name = f"{test.get('className', '?')}.{test.get('methodName', '?')}"
                print(f"::error ::FAILED {name} — {message}")
                found += 1
    return 0 if found == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
