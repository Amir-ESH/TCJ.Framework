#!/usr/bin/env python3
"""Restore, Native-AOT publish, and execute the packed-package TCJ smoke application."""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import unquote, urlparse

from sbom_common import get_runtime_release_packages, read_json

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "smoke/TCJ.NativeAot.SmokeTest/TCJ.NativeAot.SmokeTest.csproj"
NUGET_CONFIG = ROOT / "smoke/NuGet.Config"
DEFAULT_PACKAGES = ROOT / "artifacts/packages"
DEFAULT_OUTPUT = ROOT / "artifacts/aot/native-aot-smoke"
TCJ_LIBRARY_RE = re.compile(r"^(TCJ\.[^/]+)/(.+)$", re.IGNORECASE)
AOT_DIAGNOSTIC_RE = re.compile(r"\bwarning\s+(IL[23]\d{3})\b", re.IGNORECASE)
ANY_WARNING_RE = re.compile(r"\bwarning\s+[A-Z]{2,}\d+\b", re.IGNORECASE)
VERSION_LINE_RE = re.compile(r"^TCJ_PACKAGE_VERSION\s+(TCJ\.[^\s]+)\s+([^\s]+)\s*$")


class SmokeError(RuntimeError):
    pass


def expected_packages() -> tuple[str, ...]:
    try:
        project_root = ET.parse(PROJECT).getroot()
    except (ET.ParseError, OSError) as error:
        raise SmokeError(f"Invalid Native AOT smoke project: {error}") from error

    package_references = sorted(
        {
            (element.attrib.get("Include") or "").strip()
            for element in project_root.iter()
            if element.tag.rsplit("}", 1)[-1] == "PackageReference"
            and (element.attrib.get("Include") or "").strip().startswith("TCJ.")
        }
    )
    if not package_references:
        raise SmokeError("Native AOT smoke project does not declare any TCJ PackageReference entries.")

    try:
        manifest = read_json(ROOT / "eng/release-manifest.json")
        runtime_packages = set(get_runtime_release_packages(manifest))
    except (OSError, ValueError) as error:
        raise SmokeError(f"Invalid release manifest: {error}") from error

    non_runtime = sorted(set(package_references) - runtime_packages)
    if non_runtime:
        raise SmokeError(
            "Native AOT smoke references non-runtime TCJ package(s): "
            + ", ".join(non_runtime)
        )
    return tuple(package_references)


def normalize_architecture(value: str) -> str:
    normalized = value.strip().casefold().replace("-", "_")
    if normalized in {"x86_64", "amd64", "x64"}:
        return "x64"
    if normalized in {"arm64", "aarch64"}:
        return "arm64"
    return normalized or "unknown"


def write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def run(command: list[str], log: Path, env: dict[str, str]) -> tuple[int, str]:
    log.parent.mkdir(parents=True, exist_ok=True)
    process = subprocess.run(
        command,
        cwd=ROOT,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    output = process.stdout or ""
    log.write_text(output, encoding="utf-8", newline="\n")
    return process.returncode, output


def diagnostics(output: str) -> tuple[list[str], list[str], list[str], list[str], int]:
    trim: list[str] = []
    aot: list[str] = []
    tcj: list[str] = []
    upstream: list[str] = []
    generic_warning_count = 0
    for raw_line in output.splitlines():
        line = raw_line.strip()
        if ANY_WARNING_RE.search(line):
            generic_warning_count += 1
        match = AOT_DIAGNOSTIC_RE.search(line)
        if not match:
            continue
        code = match.group(1).upper()
        target = trim if code.startswith("IL2") else aot
        target.append(line)
        if "TCJ." in line or "tcj." in line:
            tcj.append(line)
        else:
            upstream.append(line)
    return trim, aot, tcj, upstream, generic_warning_count


def ensure_packages(packages: Path, version: str, expected: tuple[str, ...]) -> None:
    if not packages.is_dir():
        raise SmokeError(f"Packed-package feed does not exist: {packages}")
    missing = [
        package_id
        for package_id in expected
        if not (packages / f"{package_id}.{version}.nupkg").is_file()
    ]
    if missing:
        raise SmokeError(
            "Packed-package feed is missing required Native AOT package(s): "
            + ", ".join(missing)
        )


def source_path_candidates(value: str) -> set[str]:
    parsed = urlparse(value)
    if parsed.scheme.casefold() == "file":
        raw = unquote(parsed.path)
        if os.name == "nt" and raw.startswith("/") and len(raw) > 2 and raw[2] == ":":
            raw = raw[1:]
        path = Path(raw)
    else:
        path = Path(value)
    bases = (ROOT, NUGET_CONFIG.parent) if not path.is_absolute() else (None,)
    result: set[str] = set()
    for base in bases:
        resolved = path.resolve() if base is None else (base / path).resolve()
        result.add(os.path.normcase(os.path.normpath(str(resolved))))
    return result


def parse_assets(package_cache: Path, packages: Path, version: str, expected: tuple[str, ...]) -> dict[str, str]:
    assets_path = PROJECT.parent / "obj/project.assets.json"
    if not assets_path.is_file():
        raise SmokeError(f"Native AOT smoke restore did not create {assets_path.relative_to(ROOT)}")
    data = json.loads(assets_path.read_text(encoding="utf-8"))

    resolved: dict[str, str] = {}
    for library_name, metadata in data.get("libraries", {}).items():
        match = TCJ_LIBRARY_RE.match(library_name)
        if not match:
            continue
        package_id, resolved_version = match.groups()
        if not isinstance(metadata, dict) or metadata.get("type") != "package":
            raise SmokeError(f"{package_id} resolved as a non-package library in Native AOT smoke.")
        resolved[package_id] = resolved_version

    if set(resolved) != set(expected):
        raise SmokeError(
            "Native AOT TCJ package closure mismatch: "
            f"expected {sorted(expected)}, found {sorted(resolved)}."
        )
    wrong = {package_id: value for package_id, value in resolved.items() if value != version}
    if wrong:
        raise SmokeError(f"Native AOT smoke resolved unexpected TCJ versions: {wrong}; expected {version}.")

    restore = data.get("project", {}).get("restore", {})
    project_references = restore.get("projectReferences") or {}
    if project_references:
        raise SmokeError(
            "Native AOT smoke restored repository project references: "
            + ", ".join(sorted(project_references))
        )

    package_folders = {
        os.path.normcase(os.path.normpath(path.rstrip("/\\")))
        for path in data.get("packageFolders", {})
    }
    expected_cache = os.path.normcase(os.path.normpath(str(package_cache.resolve())))
    if expected_cache not in package_folders:
        raise SmokeError(
            f"Native AOT smoke did not use isolated NUGET_PACKAGES directory {package_cache.resolve()}."
        )

    expected_source = os.path.normcase(os.path.normpath(str(packages.resolve())))
    for package_id in EXPECTED_PACKAGES:
        metadata_path = package_cache / package_id.casefold() / version.casefold() / ".nupkg.metadata"
        if not metadata_path.is_file():
            raise SmokeError(f"Missing NuGet source metadata for {package_id} {version}: {metadata_path}")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        source = str(metadata.get("source", "")).strip()
        if not source:
            raise SmokeError(f"NuGet source metadata is empty for {package_id} {version}.")
        if source.startswith(("http://", "https://")) or expected_source not in source_path_candidates(source):
            raise SmokeError(
                f"{package_id} {version} restored from {source!r}; expected local packed feed {packages.resolve()}."
            )

    return dict(sorted(resolved.items()))


def loaded_versions(output: str, expected_version: str, expected: tuple[str, ...]) -> dict[str, str]:
    found: dict[str, str] = {}
    for line in output.splitlines():
        match = VERSION_LINE_RE.match(line.strip())
        if match:
            found[match.group(1)] = match.group(2)
    if set(found) != set(expected):
        raise SmokeError(
            "Native binary did not report the full loaded TCJ package closure: "
            f"expected {sorted(expected)}, found {sorted(found)}."
        )
    wrong = {package_id: value for package_id, value in found.items() if value != expected_version}
    if wrong:
        raise SmokeError(
            f"Native binary loaded unexpected TCJ package versions: {wrong}; expected {expected_version}."
        )
    return dict(sorted(found.items()))


def execute(version: str, rid: str, packages: Path, output: Path) -> tuple[dict, bool]:
    packages = packages.resolve()
    output = output.resolve()
    expected = expected_packages()
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True, exist_ok=True)

    for directory in (PROJECT.parent / "bin", PROJECT.parent / "obj"):
        if directory.exists():
            shutil.rmtree(directory)

    isolated = output / "nuget"
    package_cache = isolated / "packages"
    http_cache = isolated / "http-cache"
    cli_home = isolated / "home"
    for directory in (package_cache, http_cache, cli_home):
        directory.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    env.update(
        {
            "NUGET_PACKAGES": str(package_cache),
            "NUGET_HTTP_CACHE_PATH": str(http_cache),
            "DOTNET_CLI_HOME": str(cli_home),
            "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "true",
        }
    )

    payload = {
        "schemaVersion": 1,
        "status": "failed",
        "packageVersion": version,
        "runtimeIdentifier": rid,
        "consumerProject": PROJECT.relative_to(ROOT).as_posix(),
        "consumerSource": "PackedNuGet",
        "usesProjectReference": False,
        "publishAot": True,
        "expectedPackages": list(expected),
        "resolvedPackages": {},
        "loadedPackageVersions": {},
        "packageSourceStatus": "not-run",
        "restoreStatus": "not-run",
        "publishStatus": "not-run",
        "executionStatus": "not-run",
        "trimWarnings": [],
        "aotWarnings": [],
        "tcjWarnings": [],
        "upstreamWarnings": [],
        "unexpectedAotWarningCount": 0,
        "warningCount": 0,
        "operatingSystem": platform.system(),
        "architecture": normalize_architecture(platform.machine()),
        "failure": None,
    }

    all_output = ""
    result_path = output / "native-aot-result.json"
    try:
        if not version.strip():
            raise SmokeError("Native AOT smoke package version must not be empty; CI must pass steps.package-version.outputs.value.")
        ensure_packages(packages, version, expected)
        if packages != DEFAULT_PACKAGES.resolve():
            raise SmokeError(
                f"Native AOT smoke NuGet.Config is pinned to {DEFAULT_PACKAGES.resolve()}; "
                f"received package directory {packages}."
            )

        common_properties = [
            f"-p:TCJNativeAotPackageVersion={version}",
            f"-p:RuntimeIdentifier={rid}",
            f"-p:RestorePackagesPath={package_cache}",
            "-p:PublishAot=true",
            "-p:TreatWarningsAsErrors=true",
        ]
        restore = [
            "dotnet", "restore", str(PROJECT),
            "--configfile", str(NUGET_CONFIG),
            "--runtime", rid,
            "--force", "--no-cache", "--verbosity", "normal",
            *common_properties,
        ]
        code, restore_output = run(restore, output / "logs/restore.log", env)
        all_output += restore_output
        if code != 0:
            payload["restoreStatus"] = "fail"
            raise SmokeError(f"Native AOT smoke restore exited with code {code}.")
        payload["restoreStatus"] = "pass"
        payload["resolvedPackages"] = parse_assets(package_cache, packages, version, expected)
        payload["packageSourceStatus"] = "pass"

        publish_dir = output / "publish"
        publish = [
            "dotnet", "publish", str(PROJECT),
            "--configuration", "Release",
            "--runtime", rid,
            "--no-restore",
            "--output", str(publish_dir),
            "--verbosity", "normal",
            *common_properties,
        ]
        code, publish_output = run(publish, output / "logs/publish.log", env)
        all_output += "\n" + publish_output
        if code != 0:
            payload["publishStatus"] = "fail"
            raise SmokeError(f"Native AOT smoke publish exited with code {code}.")
        payload["publishStatus"] = "pass"

        trim, aot, tcj, upstream, warning_count = diagnostics(all_output)
        payload["trimWarnings"] = trim
        payload["aotWarnings"] = aot
        payload["tcjWarnings"] = tcj
        payload["upstreamWarnings"] = upstream
        payload["unexpectedAotWarningCount"] = len(trim) + len(aot)
        payload["warningCount"] = warning_count
        if trim or aot:
            raise SmokeError(
                "Native AOT smoke produced unexpected IL2xxx/IL3xxx diagnostics; "
                "Full support does not use a warning-count baseline."
            )

        binary_name = PROJECT.stem + (".exe" if rid.startswith("win-") else "")
        binary = publish_dir / binary_name
        if not binary.is_file():
            raise SmokeError(f"Native AOT publish did not produce executable {binary}.")
        code, runtime_output = run([str(binary)], output / "logs/runtime.log", env)
        if code != 0:
            payload["executionStatus"] = "fail"
            raise SmokeError(f"Native AOT smoke executable exited with code {code}.")
        if "TCJ Native AOT packed-package smoke passed" not in runtime_output:
            payload["executionStatus"] = "fail"
            raise SmokeError("Native AOT smoke executable did not emit the expected success marker.")
        payload["loadedPackageVersions"] = loaded_versions(runtime_output, version, expected)
        payload["executionStatus"] = "pass"
        payload["status"] = "passed"
        return payload, True
    except (SmokeError, OSError, json.JSONDecodeError) as error:
        trim, aot, tcj, upstream, warning_count = diagnostics(all_output)
        payload["trimWarnings"] = trim
        payload["aotWarnings"] = aot
        payload["tcjWarnings"] = tcj
        payload["upstreamWarnings"] = upstream
        payload["unexpectedAotWarningCount"] = len(trim) + len(aot)
        payload["warningCount"] = warning_count
        payload["failure"] = str(error)
        return payload, False
    finally:
        write_json(result_path, payload)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--rid", default="linux-x64")
    parser.add_argument("--packages", type=Path, default=DEFAULT_PACKAGES)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    payload, success = execute(args.version, args.rid, args.packages, args.output)
    if success:
        print(
            "Native AOT packed-package smoke passed for "
            f"{args.rid} with TCJ {args.version}."
        )
        return 0
    print(f"Native AOT packed-package smoke failed: {payload['failure']}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
