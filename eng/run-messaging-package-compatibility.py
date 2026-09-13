#!/usr/bin/env python3
"""Restore isolated package-only consumers and capture runtime descriptors."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def run(args):
    completed = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, check=False)
    if completed.returncode:
        # The consumer uses no real credentials; retain compiler diagnostics for actionable CI.
        print(completed.stdout, file=sys.stderr)
        print(completed.stderr, file=sys.stderr)
        raise RuntimeError("Package-only compatibility command failed.")
    return completed.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source", required=True, help="Candidate package directory or NuGet.org URL")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    module_spec = importlib.util.spec_from_file_location("matrix_verifier", ROOT / "eng/verify-messaging-compatibility.py")
    verifier = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(verifier)
    ctx = verifier.context()
    workspace = ROOT / "artifacts/compatibility/messaging-matrix"
    workspace.mkdir(parents=True, exist_ok=True)
    # A new cache is mandatory even for a rebuilt candidate with an unchanged version.
    work = Path(tempfile.mkdtemp(prefix="run-", dir=workspace))
    source = args.source if args.source.startswith("https://") else str(Path(args.source).resolve())
    config = ET.Element("configuration")
    feeds = ET.SubElement(config, "packageSources")
    ET.SubElement(feeds, "clear")
    ET.SubElement(feeds, "add", key="candidate", value=source)
    ET.SubElement(feeds, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="candidate"), "package", pattern="TCJ.*")
    ET.SubElement(ET.SubElement(mapping, "packageSource", key="nuget.org"), "package", pattern="*")
    config_path = work / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    project = ROOT / "compatibility/Consumers/MessagingMatrix.Console/MessagingMatrix.Console.csproj"
    if ET.parse(project).findall(".//ProjectReference"):
        raise RuntimeError("Package consumer cannot use project references.")
    for transport in ["InMemory", "RabbitMQ", "AzureServiceBus", "Kafka"]:
        target = work / transport
        cache = work / "cache"
        common = [str(project), f"-p:TCJCompatibilityVersion={args.version}", f"-p:MatrixTransport={transport}", "--artifacts-path", str(target)]
        run(["dotnet", "restore", *common, "--force-evaluate", "--no-cache", "--configfile", str(config_path), "--packages", str(cache)])
        assets_paths = list(target.rglob("project.assets.json"))
        if len(assets_paths) != 1:
            raise RuntimeError("Expected exactly one package-only dependency graph.")
        assets = json.loads(assets_paths[0].read_text())
        tcj = {identity: data for identity, data in assets["libraries"].items() if identity.startswith("TCJ.")}
        expected = {"TCJ.Core", "TCJ.Messaging"} | ({"TCJ.Messaging." + transport} if transport != "InMemory" else set())
        if {key.split("/")[0] for key in tcj} != expected:
            raise RuntimeError("Adapter dependency isolation failed.")
        for identity, data in tcj.items():
            if identity.split("/")[1] != args.version or data["type"] != "package":
                raise RuntimeError("TCJ package version or source-project leakage.")
            metadata = json.loads((cache / data["path"] / ".nupkg.metadata").read_text())
            if metadata.get("source", "").rstrip("/\\").lower() != source.rstrip("/\\").lower():
                raise RuntimeError("TCJ package source identity mismatch.")
        run(["dotnet", "build", *common, "-c", "Release", "--no-restore"])
        binaries = list((target / "bin").rglob("MessagingMatrix.Console.dll"))
        if len(binaries) != 1:
            raise RuntimeError("Expected exactly one package consumer executable.")
        descriptor = json.loads(run(["dotnet", str(binaries[0])]))
        spec = verifier.read_json(ROOT / verifier.CONTRACT)["transports"][transport]
        if descriptor.get("name") != spec["runtimeName"]:
            raise RuntimeError("Published transport identity drift.")
        for key, value in spec["descriptorCapabilities"].items():
            if descriptor.get("capabilities", {}).get(key[0].lower() + key[1:]) != value:
                raise RuntimeError("Published transport capability drift.")
        evidence = {"context": ctx, "overall": "pass", "packageOnly": True, "version": args.version, "descriptor": descriptor, "packages": sorted(tcj)}
        (output / (transport + ".json")).write_text(json.dumps(evidence, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
