#!/usr/bin/env python3
"""Verify that a TCJ release is listed on NuGet.org and inspect downloaded packages."""

from __future__ import annotations

import argparse
import gzip
import importlib.util
import json
import sys
import time
import zipfile
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

_VALIDATOR_PATH = Path(__file__).resolve().with_name("verify-release.py")
_VALIDATOR_SPEC = importlib.util.spec_from_file_location("tcj_verify_release", _VALIDATOR_PATH)
if _VALIDATOR_SPEC is None or _VALIDATOR_SPEC.loader is None:
    raise RuntimeError(f"Unable to load release validator: {_VALIDATOR_PATH}")
_VALIDATOR = importlib.util.module_from_spec(_VALIDATOR_SPEC)
_VALIDATOR_SPEC.loader.exec_module(_VALIDATOR)
SEMVER_PATTERN = _VALIDATOR.SEMVER_PATTERN
package_readme_source = _VALIDATOR.package_readme_source
readme_policy_required = _VALIDATOR.readme_policy_required
validate_primary_package = _VALIDATOR.validate_primary_package

DEFAULT_FLAT_CONTAINER = "https://api.nuget.org/v3-flatcontainer"
DEFAULT_REGISTRATION = "https://api.nuget.org/v3/registration5-gz-semver2"


def fail(message: str) -> None:
    raise RuntimeError(message)


def request_bytes(url: str, attempts: int = 3) -> bytes:
    request = Request(
        url,
        headers={"User-Agent": "TCJ-Framework-published-package-verifier/1.0"},
    )

    last_error: Exception | None = None

    for attempt in range(1, attempts + 1):
        try:
            with urlopen(request, timeout=30) as response:
                payload = response.read()

                # NuGet RegistrationsBaseUrl/3.6.0 returns gzip-compressed
                # registration documents. urllib does not automatically
                # decompress these responses.
                if payload.startswith(b"\x1f\x8b"):
                    payload = gzip.decompress(payload)

                return payload

        except (HTTPError, URLError, TimeoutError, gzip.BadGzipFile) as error:
            last_error = error

            if attempt < attempts:
                time.sleep(attempt * 2)

    fail(f"Unable to fetch {url}: {last_error}")


def request_json(url: str) -> dict[str, object]:
    return json.loads(request_bytes(url).decode("utf-8"))


def load_manifest(path: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    required = {"schemaVersion", "version", "tag", "releaseDate", "repository", "licenseExpression", "packages"}
    missing = sorted(required.difference(data))
    if missing:
        fail(f"Published release manifest is missing fields: {', '.join(missing)}")
    if data["schemaVersion"] != 1:
        fail("Unsupported published release manifest schemaVersion.")
    version = str(data["version"])
    if not SEMVER_PATTERN.fullmatch(version):
        fail(f"Published version is not valid semantic versioning: {version}")
    if data["tag"] != f"v{version}":
        fail("Published release tag must be the version prefixed with 'v'.")
    packages = data["packages"]
    if not isinstance(packages, list) or not packages:
        fail("Published release packages must be a non-empty array.")
    return data


def registration_entries(index: dict[str, object]) -> list[dict[str, object]]:
    entries: list[dict[str, object]] = []
    for page in index.get("items", []):
        if not isinstance(page, dict):
            continue
        page_items = page.get("items")
        if page_items is None:
            page_url = page.get("@id")
            if not isinstance(page_url, str):
                continue
            page_items = request_json(page_url).get("items", [])
        for item in page_items or []:
            if isinstance(item, dict):
                entries.append(item)
    return entries


def find_catalog_entry(
    package_id: str,
    version: str,
    registration_base_url: str,
) -> dict[str, object] | None:
    url = f"{registration_base_url.rstrip('/')}/{package_id.lower()}/index.json"
    try:
        index = request_json(url)
    except RuntimeError as error:
        if "HTTP Error 404" in str(error):
            return None
        raise

    for item in registration_entries(index):
        catalog = item.get("catalogEntry")
        if isinstance(catalog, str):
            catalog = request_json(catalog)
        if not isinstance(catalog, dict):
            continue
        if str(catalog.get("version", "")).lower() == version.lower():
            return catalog
    return None


def version_in_flat_container(
    package_id: str,
    version: str,
    flat_container_base_url: str,
) -> bool:
    url = f"{flat_container_base_url.rstrip('/')}/{package_id.lower()}/index.json"
    try:
        data = request_json(url)
    except RuntimeError as error:
        if "HTTP Error 404" in str(error):
            return False
        raise
    versions = [str(item).lower() for item in data.get("versions", [])]
    return version.lower() in versions


def download_package(
    package_id: str,
    version: str,
    destination: Path,
    flat_container_base_url: str,
) -> Path:
    lower_id = package_id.lower()
    lower_version = version.lower()
    url = (
        f"{flat_container_base_url.rstrip('/')}/{lower_id}/{lower_version}/"
        f"{lower_id}.{lower_version}.nupkg"
    )
    destination.mkdir(parents=True, exist_ok=True)
    path = destination / f"{package_id}.{version}.nupkg"
    path.write_bytes(request_bytes(url))
    return path


def verify_once(
    manifest: dict[str, object],
    version: str,
    expected_license_expression: str,
    output_directory: Path,
    flat_container_base_url: str,
    registration_base_url: str,
    *,
    expected_readmes: dict[str, bytes] | None = None,
    enforce_readme_policy: bool = False,
) -> list[str]:
    failures: list[str] = []
    repository = str(manifest["repository"])

    for package_id_value in manifest["packages"]:
        package_id = str(package_id_value)
        if not version_in_flat_container(package_id, version, flat_container_base_url):
            failures.append(f"{package_id} {version} is absent from the flat container")
            continue

        catalog = find_catalog_entry(package_id, version, registration_base_url)
        if catalog is None:
            failures.append(f"{package_id} {version} has no registration metadata")
            continue

        listed = catalog.get("listed", True)
        published = str(catalog.get("published", ""))
        if listed is not True or published.startswith("1900-"):
            failures.append(f"{package_id} {version} is unlisted")
            continue

        package_path = download_package(
            package_id,
            version,
            output_directory,
            flat_container_base_url,
        )
        try:
            validate_primary_package(
                package_path,
                package_id,
                version,
                repository,
                expected_license_expression,
                expected_readme=(expected_readmes or {}).get(package_id),
                enforce_readme_policy=enforce_readme_policy,
            )
        except (ValueError, OSError, zipfile.BadZipFile) as error:
            failures.append(f"{package_id} {version} content validation failed: {error}")
            continue

        print(f"{package_id} {version}: LISTED, DOWNLOADED, VERIFIED")

    return failures


def resolve_expected_license_expression(
    version: str,
    published_manifest: dict[str, object],
    release_manifest_path: Path,
    explicit_license_expression: str | None = None,
) -> str:
    if explicit_license_expression is not None and explicit_license_expression.strip():
        return explicit_license_expression.strip()

    if version.casefold() == str(published_manifest["version"]).casefold():
        return str(published_manifest["licenseExpression"]).strip()

    release_manifest = json.loads(release_manifest_path.read_text(encoding="utf-8"))
    if version.casefold() == str(release_manifest.get("version", "")).casefold():
        license_expression = str(release_manifest.get("licenseExpression", "")).strip()
        if not license_expression:
            fail("eng/release-manifest.json has no licenseExpression for the requested version.")
        return license_expression

    fail(
        f"No expected license expression is recorded for {version}. "
        "Pass --license-expression explicitly when verifying a historical version "
        "that is not represented by the current release manifests."
    )


def expected_readmes_for_current_release(
    version: str,
    release_manifest_path: Path,
) -> dict[str, bytes] | None:
    release_manifest = json.loads(release_manifest_path.read_text(encoding="utf-8"))
    if version.casefold() != str(release_manifest.get("version", "")).casefold():
        return None

    repository_root = release_manifest_path.resolve().parents[1]
    package_ids = release_manifest.get("packages", [])
    if not isinstance(package_ids, list) or not package_ids:
        fail("eng/release-manifest.json must define packages for README verification.")

    readmes: dict[str, bytes] = {}
    for package_id_value in package_ids:
        package_id = str(package_id_value)
        path = package_readme_source(repository_root, package_id)
        if not path.is_file():
            fail(f"Package README source is missing: {path.relative_to(repository_root)}")
        readmes[package_id] = path.read_bytes()
    return readmes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(__file__).resolve().with_name("published-release.json"),
    )
    parser.add_argument("--version")
    parser.add_argument(
        "--license-expression",
        help="Expected SPDX license expression for an explicitly selected historical version.",
    )
    parser.add_argument(
        "--release-manifest",
        type=Path,
        default=Path(__file__).resolve().with_name("release-manifest.json"),
    )
    parser.add_argument(
        "--output-directory",
        type=Path,
        default=Path("artifacts/published-packages"),
    )
    parser.add_argument("--wait-seconds", type=int, default=0)
    parser.add_argument("--interval-seconds", type=int, default=30)
    parser.add_argument("--flat-container-base-url", default=DEFAULT_FLAT_CONTAINER)
    parser.add_argument("--registration-base-url", default=DEFAULT_REGISTRATION)
    args = parser.parse_args()

    if args.wait_seconds < 0 or args.interval_seconds <= 0:
        fail("wait-seconds must be non-negative and interval-seconds must be positive.")

    manifest = load_manifest(args.manifest.resolve())
    version = args.version or str(manifest["version"])
    if not SEMVER_PATTERN.fullmatch(version):
        fail(f"Requested version is not valid semantic versioning: {version}")

    release_manifest_path = args.release_manifest.resolve()
    expected_license_expression = resolve_expected_license_expression(
        version,
        manifest,
        release_manifest_path,
        args.license_expression,
    )
    expected_readmes = expected_readmes_for_current_release(
        version,
        release_manifest_path,
    )
    enforce_readme_policy = readme_policy_required(version)

    deadline = time.monotonic() + args.wait_seconds
    while True:
        failures = verify_once(
            manifest,
            version,
            expected_license_expression,
            args.output_directory.resolve(),
            args.flat_container_base_url,
            args.registration_base_url,
            expected_readmes=expected_readmes,
            enforce_readme_policy=enforce_readme_policy,
        )
        if not failures:
            print(f"Published package verification succeeded for {version}.")
            return 0
        if time.monotonic() >= deadline:
            print("Published package verification failed:", file=sys.stderr)
            for failure in failures:
                print(f"  - {failure}", file=sys.stderr)
            return 1
        print("NuGet.org has not converged yet; retrying:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        time.sleep(args.interval_seconds)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, KeyError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"Published package verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
