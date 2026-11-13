#!/usr/bin/env python3
"""Runs ONE .NET test project the way CI does (.github/workflows/ci.yml), with the quarantine list and one retry.

    python3 tests/ci/dotnet_test.py tests/MineClaw.Bot.Tests [--configuration Release] [--results DIR]

1. Tests listed in tests/ci/quarantine.txt for this project are excluded from the gating run.
2. The gating run: `dotnet test <project> --filter <not quarantined>` with a TRX log.
3. If tests fail, ONLY the failed ones run once more. A test that passes on the retry is reported as flaky (a warning
   annotation on the run) and does not fail the job; a test that fails twice does.
4. The quarantined tests then run on their own, report-only: their result is printed and annotated, never gating.

Exit code: 0 when every non-quarantined test passed (possibly on the retry), 1 otherwise, 2 on usage errors.
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
QUARANTINE = ROOT / "tests" / "ci" / "quarantine.txt"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def gh(kind: str, msg: str) -> None:
    """A GitHub Actions annotation (plain text elsewhere)."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::{kind}::{msg}", flush=True)
    else:
        print(f"{kind.upper()}: {msg}", flush=True)


def quarantined(project: str) -> list[str]:
    """Fully-qualified name prefixes quarantined for this project. Line format: `<Project> <FQN prefix>  # reason`."""
    out: list[str] = []
    if not QUARANTINE.exists():
        return out
    for raw in QUARANTINE.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) != 2:
            gh("warning", f"quarantine.txt: ignoring malformed line: {raw!r}")
            continue
        if parts[0] == project:
            out.append(parts[1])
    return out


def esc(name: str) -> str:
    # dotnet test filter syntax: escape the operators and parentheses that can appear in names.
    return re.sub(r"([\\(),&|=!~])", r"\\\1", name)


def run(project_dir: Path, conf: str, results: Path, name: str, flt: str | None) -> tuple[int, list[str], int]:
    """dotnet test; returns (exit code, failed test method FQNs, executed count)."""
    trx = f"{name}.trx"
    cmd = ["dotnet", "test", str(project_dir), "-c", conf, "-m:1", "--nologo",
           "--logger", f"trx;LogFileName={trx}", "--results-directory", str(results),
           "--logger", "console;verbosity=normal"]
    if name != "main":
        cmd.append("--no-build")
    if flt:
        cmd += ["--filter", flt]
    print("+ " + " ".join(cmd), flush=True)
    rc = subprocess.call(cmd, cwd=ROOT)
    failed: list[str] = []
    executed = 0
    path = results / trx
    if path.exists():
        tree = ET.parse(path)
        # testName can carry theory arguments; the method FQN comes from the TestMethod definition.
        defs = {d.get("id"): d for d in tree.iterfind(".//t:UnitTest", NS)}
        for r in tree.iterfind(".//t:UnitTestResult", NS):
            executed += 1
            if r.get("outcome") == "Failed":
                d = defs.get(r.get("testId"))
                m = d.find("t:TestMethod", NS) if d is not None else None
                if m is not None:
                    failed.append(f"{m.get('className')}.{m.get('name')}")
                else:
                    failed.append(re.sub(r"\(.*$", "", r.get("testName", "")))
    return rc, sorted(set(failed)), executed


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("project_dir")
    ap.add_argument("--configuration", "-c", default="Release")
    ap.add_argument("--results", default=str(ROOT / "artifacts" / "test-results"))
    a = ap.parse_args()
    project_dir = Path(a.project_dir)
    if not project_dir.is_absolute():
        project_dir = (ROOT / project_dir).resolve()
    if not project_dir.is_dir():
        print(f"no project dir {project_dir}", file=sys.stderr)
        return 2
    project = project_dir.name
    results = Path(a.results) / project
    results.mkdir(parents=True, exist_ok=True)

    q = quarantined(project)
    main_filter = "&".join(f"FullyQualifiedName!~{esc(p)}" for p in q) or None
    if q:
        print(f"quarantined in {project}: {len(q)} pattern(s): {', '.join(q)}")

    rc, failed, executed = run(project_dir, a.configuration, results, "main", main_filter)
    status = 0
    if rc != 0 and not failed:
        gh("error", f"{project}: dotnet test exited {rc} without failed test results (build error or crash)")
        status = 1
    elif failed:
        print(f"{len(failed)} failed test(s) in {project}; retrying them once:", *failed, sep="\n  ")
        retry_filter = "|".join(f"FullyQualifiedName={esc(f)}" for f in failed)
        rc2, failed2, _ = run(project_dir, a.configuration, results, "retry", retry_filter)
        flaky = [f for f in failed if f not in failed2]
        for f in flaky:
            gh("warning", f"{project}: flaky test (failed, then passed on retry): {f}")
        for f in failed2:
            gh("error", f"{project}: test failed twice: {f}")
        if failed2 or (rc2 != 0 and not flaky):
            status = 1
    print(f"{project}: {executed} test(s) run in the gating pass")

    if q:
        qfilter = "|".join(f"FullyQualifiedName~{esc(p)}" for p in q)
        qrc, qfailed, qexec = run(project_dir, a.configuration, results, "quarantine", qfilter)
        if qfailed:
            gh("warning", f"{project}: quarantined tests still failing ({len(qfailed)}/{qexec}): {', '.join(qfailed[:20])}")
        elif qexec:
            gh("notice", f"{project}: all {qexec} quarantined test(s) passed: consider removing them from tests/ci/quarantine.txt")
    return status


if __name__ == "__main__":
    sys.exit(main())
