#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
package_directory="${2:-artifacts/packages}"

if [[ -z "$version" ]]; then
  echo "Usage: $0 <version> [package-directory]" >&2
  exit 2
fi

if [[ ! -d "$package_directory" ]]; then
  echo "Package directory does not exist: $package_directory" >&2
  exit 1
fi

package_ids=(
  TCJ.Core
  TCJ.DependencyInjection
  TCJ.EntityFrameworkCore
  TCJ.EntityFrameworkCore.SqlServer
  TCJ.AspNetCore
)

for package_id in "${package_ids[@]}"; do
  primary="$package_directory/$package_id.$version.nupkg"
  symbols="$package_directory/$package_id.$version.snupkg"

  if [[ ! -f "$primary" ]]; then
    echo "Missing primary package: $primary" >&2
    exit 1
  fi

  if [[ ! -f "$symbols" ]]; then
    echo "Missing symbol package: $symbols" >&2
    exit 1
  fi
done

is_tooling_package() {
  local package="$1"
  unzip -Z1 "$package" 2>/dev/null | grep -q '^analyzers/dotnet/cs/'
}

mapfile -t primary_packages < <(
  find "$package_directory" -maxdepth 1 -type f -name '*.nupkg' -print | while read -r package; do
    if ! is_tooling_package "$package"; then
      printf '%s\n' "$package"
    fi
  done | sort
)

mapfile -t tooling_packages < <(
  find "$package_directory" -maxdepth 1 -type f -name '*.nupkg' -print | while read -r package; do
    if is_tooling_package "$package"; then
      printf '%s\n' "$package"
    fi
  done | sort
)

mapfile -t symbol_packages < <(
  find "$package_directory" -maxdepth 1 -type f -name '*.snupkg' -print | sort
)

if (( ${#primary_packages[@]} != ${#package_ids[@]} )); then
  echo "Expected ${#package_ids[@]} primary packages, found ${#primary_packages[@]}." >&2
  printf '  %s\n' "${primary_packages[@]}" >&2
  exit 1
fi

if (( ${#symbol_packages[@]} != ${#package_ids[@]} )); then
  echo "Expected ${#package_ids[@]} symbol packages, found ${#symbol_packages[@]}." >&2
  printf '  %s\n' "${symbol_packages[@]}" >&2
  exit 1
fi

printf 'Verified package version %s:\n' "$version"
printf '  Runtime: %s\n' "${primary_packages[@]}"
printf '  Tooling: %s\n' "${tooling_packages[@]:-none}"
printf '  Symbols: %s\n' "${symbol_packages[@]}"
