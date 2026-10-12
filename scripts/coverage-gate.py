#!/usr/bin/env python3
"""Fails when merged Cobertura line/branch coverage of the gated assemblies is below the thresholds.

usage: coverage-gate.py <results-dir> [--line 80] [--branch 70]
Counts are merged per source line across all test runs, so code covered by any test project counts as covered.
Migrations, generated code and the WPF/Host projects are excluded.
"""
import glob, sys, xml.etree.ElementTree as ET

GATED = ("ATM.Core", "ATM.Application", "ATM.Infrastructure")
EXCLUDE = ("/Migrations/", "\\Migrations\\", ".g.cs", ".designer.cs", "Generated")

def main():
    root = sys.argv[1]
    line_min = float(sys.argv[sys.argv.index("--line") + 1]) if "--line" in sys.argv else 80.0
    branch_min = float(sys.argv[sys.argv.index("--branch") + 1]) if "--branch" in sys.argv else 70.0
    lines, branches = {}, {}
    for f in glob.glob(f"{root}/**/coverage.cobertura.xml", recursive=True):
        for pkg in ET.parse(f).getroot().iter("package"):
            if pkg.get("name") not in GATED:
                continue
            for cls in pkg.iter("class"):
                fn = cls.get("filename", "")
                if any(x in fn for x in EXCLUDE):
                    continue
                for ln in cls.iter("line"):
                    key = (pkg.get("name"), fn, ln.get("number"))
                    lines[key] = max(lines.get(key, 0), int(ln.get("hits")))
                    cc = ln.get("condition-coverage")
                    if ln.get("branch") == "True" and cc:
                        pct, frac = cc.split(" ")
                        cov, tot = (int(x) for x in frac.strip("()").split("/"))
                        old = branches.get(key, (0, tot))
                        branches[key] = (max(old[0], cov), tot)
    if not lines:
        print("no coverage data found"); return 2
    lp = 100.0 * sum(1 for h in lines.values() if h) / len(lines)
    bt = sum(t for _, t in branches.values())
    bp = 100.0 * sum(c for c, _ in branches.values()) / bt if bt else 100.0
    print(f"line coverage   {lp:5.1f}%  (min {line_min}%)  over {len(lines)} lines")
    print(f"branch coverage {bp:5.1f}%  (min {branch_min}%)  over {bt} branches")
    return 0 if lp >= line_min and bp >= branch_min else 1

sys.exit(main())
