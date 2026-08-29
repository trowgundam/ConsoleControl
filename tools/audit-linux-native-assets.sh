#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="${2:-$repo_root/tools/linux-x64-native-assets.tsv}"
package_root="${1:?Usage: $0 <assembled-package-directory> [manifest]}"
inventory="$package_root/NATIVE-ASSET-INVENTORY.tsv"

if [[ ! -d "$package_root" || ! -f "$manifest" ]]; then
  echo "Native asset audit input is missing." >&2
  exit 2
fi

printf 'path\tsha256\tcomponent\trequired notices\n' > "$inventory"
native_count=0
while IFS= read -r -d '' candidate; do
  magic="$(od -An -N4 -tx1 "$candidate" | tr -d ' \n')"
  [[ "$magic" == "7f454c46" ]] || continue

  relative="${candidate#"$package_root/"}"
  matches=0
  matched_component=""
  matched_notices=""
  while IFS=$'\t' read -r pattern component notices; do
    [[ -n "$pattern" && "${pattern:0:1}" != "#" ]] || continue
    if [[ "$relative" =~ $pattern ]]; then
      matches=$((matches + 1))
      matched_component="$component"
      matched_notices="$notices"
    fi
  done < "$manifest"

  if [[ "$matches" -ne 1 ]]; then
    echo "Native asset '$relative' matched $matches audit rules; expected exactly one." >&2
    exit 1
  fi

  IFS=';' read -ra notice_paths <<< "$matched_notices"
  for notice in "${notice_paths[@]}"; do
    if [[ ! -f "$package_root/$notice" ]]; then
      echo "Native asset '$relative' requires missing notice '$notice'." >&2
      exit 1
    fi
  done

  printf '%s\t%s\t%s\t%s\n' \
    "$relative" \
    "$(sha256sum "$candidate" | cut -d ' ' -f 1)" \
    "$matched_component" \
    "$matched_notices" >> "$inventory"
  native_count=$((native_count + 1))
done < <(find "$package_root" -type f -print0 | LC_ALL=C sort -z)

if [[ "$native_count" -eq 0 ]]; then
  echo "Native asset audit found no ELF files." >&2
  exit 1
fi

printf 'Audited %d native assets.\n' "$native_count"
