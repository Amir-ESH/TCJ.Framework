#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PROJECT = ROOT / "tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj"


def main() -> int:
    parser = argparse.ArgumentParser(description="Replay a deterministic TCJ concurrency stress scenario.")
    parser.add_argument("--scenario", required=True)
    parser.add_argument("--seed", type=int, required=True)
    parser.add_argument("--workers", type=int)
    parser.add_argument("--iterations", type=int)
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()

    env = os.environ.copy()
    env["TCJ_STRESS_SEED"] = str(args.seed)
    if args.workers is not None:
        env["TCJ_STRESS_WORKERS"] = str(args.workers)
    if args.iterations is not None:
        env["TCJ_STRESS_ITERATIONS"] = str(args.iterations)

    command = [
        "dotnet", "test", str(PROJECT), "--configuration", args.configuration,
        "--filter", f"FullyQualifiedName~{args.scenario}",
        "--logger", f"trx;LogFileName=replay-{args.scenario}-{args.seed}.trx",
        "--results-directory", str(ROOT / "TestResults/Concurrency/replay"),
    ]
    print("Replay:", " ".join(command))
    return subprocess.call(command, cwd=ROOT, env=env)


if __name__ == "__main__":
    sys.exit(main())
