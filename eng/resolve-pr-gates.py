#!/usr/bin/env python3
"""Resolve and verify the branch-aware TCJ required pull-request gate plan."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

DEFAULT_POLICY = Path(__file__).with_name("required-pr-gates.json")
SUCCESS_RESULT = "success"
ALLOWED_NON_REQUIRED_RESULTS = {"success", "skipped"}


@dataclass(frozen=True)
class GateDecision:
    required: bool
    reasons: tuple[str, ...]


def load_policy(path: Path = DEFAULT_POLICY) -> dict[str, Any]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ValueError("required-pr-gates.json must use schemaVersion 1.")
    gates = data.get("gates")
    if not isinstance(gates, dict) or not gates:
        raise ValueError("required-pr-gates.json must define at least one conditional gate.")
    supported = data.get("supportedTargets")
    if not isinstance(supported, list) or not supported:
        raise ValueError("required-pr-gates.json must define supportedTargets.")
    return data


def _glob_regex(pattern: str) -> re.Pattern[str]:
    parts: list[str] = ["^"]
    index = 0
    while index < len(pattern):
        char = pattern[index]
        if char == "*":
            if index + 1 < len(pattern) and pattern[index + 1] == "*":
                while index + 1 < len(pattern) and pattern[index + 1] == "*":
                    index += 1
                parts.append(".*")
            else:
                parts.append("[^/]*")
        elif char == "?":
            parts.append("[^/]")
        else:
            parts.append(re.escape(char))
        index += 1
    parts.append("$")
    return re.compile("".join(parts))


def matches(path: str, pattern: str) -> bool:
    return bool(_glob_regex(pattern).match(path))


def matching_paths(paths: Iterable[str], patterns: Iterable[str]) -> tuple[str, ...]:
    compiled = [(pattern, _glob_regex(pattern)) for pattern in patterns]
    matched: list[str] = []
    for path in paths:
        if any(regex.match(path) for _, regex in compiled):
            matched.append(path)
    return tuple(matched)


def resolve_plan(policy: dict[str, Any], target: str, changed: Iterable[str]) -> dict[str, Any]:
    supported = tuple(policy["supportedTargets"])
    if target not in supported:
        raise ValueError(f"Unsupported PR target '{target}'. Expected one of: {', '.join(supported)}.")

    changed_files = tuple(sorted(set(path.strip() for path in changed if path.strip())))
    decisions: dict[str, GateDecision] = {}

    for gate in policy.get("alwaysRequired", []):
        decisions[gate] = GateDecision(True, (f"always required for {target}",))

    gate_policy: dict[str, Any] = policy["gates"]
    for gate, config in gate_policy.items():
        matched = matching_paths(changed_files, config.get("paths", []))
        decisions[gate] = GateDecision(
            bool(matched),
            tuple(f"matched {path}" for path in matched[:8]),
        )

    self_matches = matching_paths(changed_files, policy.get("selfProtectionPaths", []))
    if self_matches:
        reason = f"required-gate infrastructure changed ({self_matches[0]})"
        for gate in gate_policy:
            prior = decisions[gate]
            decisions[gate] = GateDecision(True, prior.reasons + (reason,))

    if target == "main":
        escalation = policy.get("mainEscalation", {})
        escalation_matches = matching_paths(changed_files, escalation.get("paths", []))
        if escalation_matches:
            reason = f"main release infrastructure changed ({escalation_matches[0]})"
            for gate in escalation.get("gates", []):
                if gate not in decisions:
                    raise ValueError(f"mainEscalation references unknown gate '{gate}'.")
                prior = decisions[gate]
                decisions[gate] = GateDecision(True, prior.reasons + (reason,))

    serialized = {
        gate: {"required": decision.required, "reasons": list(decision.reasons)}
        for gate, decision in decisions.items()
    }
    return {
        "schemaVersion": 1,
        "target": target,
        "changedFiles": list(changed_files),
        "gates": serialized,
    }


def changed_files(base: str, head: str) -> tuple[str, ...]:
    result = subprocess.run(
        ["git", "diff", "--name-only", f"{base}...{head}"],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return tuple(line.strip() for line in result.stdout.splitlines() if line.strip())


def write_github_outputs(output_path: Path, plan: dict[str, Any]) -> None:
    gates: dict[str, Any] = plan["gates"]
    with output_path.open("a", encoding="utf-8") as handle:
        for gate, decision in gates.items():
            handle.write(f"{gate}={'true' if decision['required'] else 'false'}\n")
        verification_plan = {
            "schemaVersion": plan["schemaVersion"],
            "target": plan["target"],
            "gates": {
                gate: {"required": bool(decision["required"])}
                for gate, decision in gates.items()
            },
        }
        compact = json.dumps(verification_plan, separators=(",", ":"), sort_keys=True)
        handle.write(f"plan_json={compact}\n")


def write_summary(path: Path, plan: dict[str, Any]) -> None:
    lines = [
        "# Required PR Gate plan",
        "",
        f"Target branch: `{plan['target']}`",
        "",
        "| Gate | Required | Reason |",
        "|---|---:|---|",
    ]
    for gate, decision in plan["gates"].items():
        reason = "; ".join(decision["reasons"]) if decision["reasons"] else "not affected"
        lines.append(f"| `{gate}` | {'yes' if decision['required'] else 'no'} | {reason} |")
    lines.extend(["", f"Changed files: **{len(plan['changedFiles'])}**", ""])
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines), encoding="utf-8")


def verify_results(plan: dict[str, Any], results: dict[str, str]) -> tuple[bool, list[str]]:
    messages: list[str] = []
    valid = True
    for gate, decision in plan.get("gates", {}).items():
        result = results.get(gate, "missing")
        required = bool(decision.get("required"))
        if required and result != SUCCESS_RESULT:
            valid = False
            messages.append(f"FAIL {gate}: required but result was '{result}'.")
        elif not required and result not in ALLOWED_NON_REQUIRED_RESULTS:
            valid = False
            messages.append(f"FAIL {gate}: not selected but unexpected result was '{result}'.")
        else:
            messages.append(f"PASS {gate}: {'required' if required else 'not required'} -> {result}.")
    return valid, messages


def _parse_json_argument(value: str, name: str) -> dict[str, Any]:
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exc:
        raise ValueError(f"{name} is not valid JSON: {exc}") from exc
    if not isinstance(parsed, dict):
        raise ValueError(f"{name} must be a JSON object.")
    return parsed


def cmd_resolve(args: argparse.Namespace) -> int:
    policy = load_policy(Path(args.policy))
    paths = changed_files(args.base, args.head)
    plan = resolve_plan(policy, args.target, paths)
    print(json.dumps(plan, indent=2, sort_keys=True))
    if args.github_output:
        write_github_outputs(Path(args.github_output), plan)
    if args.summary:
        write_summary(Path(args.summary), plan)
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    plan = _parse_json_argument(args.plan_json, "--plan-json")
    results = _parse_json_argument(args.results_json, "--results-json")
    valid, messages = verify_results(plan, {str(k): str(v) for k, v in results.items()})
    for message in messages:
        print(message)
    return 0 if valid else 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    resolve = subparsers.add_parser("resolve")
    resolve.add_argument("--base", required=True)
    resolve.add_argument("--head", required=True)
    resolve.add_argument("--target", required=True)
    resolve.add_argument("--policy", default=str(DEFAULT_POLICY))
    resolve.add_argument("--github-output")
    resolve.add_argument("--summary")
    resolve.set_defaults(func=cmd_resolve)

    verify = subparsers.add_parser("verify-results")
    verify.add_argument("--plan-json", required=True)
    verify.add_argument("--results-json", required=True)
    verify.set_defaults(func=cmd_verify)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        return args.func(args)
    except (ValueError, subprocess.CalledProcessError) as exc:
        print(f"Required PR Gate error: {exc}")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
