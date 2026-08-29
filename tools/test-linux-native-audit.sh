#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

fixture="$work_dir/package"
mkdir -p "$fixture/bin" "$fixture/third-party-licenses"
cp "$repo_root/LICENSE" "$fixture/LICENSE"
cp "$repo_root/third-party-licenses/SDL-LICENSE.txt" "$fixture/third-party-licenses/SDL-LICENSE.txt"
printf '\177ELFfixture' > "$fixture/bin/known"
printf '^bin/known\tfixture\tthird-party-licenses/SDL-LICENSE.txt\n' > "$work_dir/manifest.tsv"

"$repo_root/tools/audit-linux-native-assets.sh" "$fixture" "$work_dir/manifest.tsv" >/dev/null
[[ -s "$fixture/NATIVE-ASSET-INVENTORY.tsv" ]]

printf '\177ELFunknown' > "$fixture/bin/unknown"
if "$repo_root/tools/audit-linux-native-assets.sh" "$fixture" "$work_dir/manifest.tsv" >/dev/null 2>&1; then
  echo "Native audit accepted an unclassified ELF file." >&2
  exit 1
fi
rm "$fixture/bin/unknown"

rm "$fixture/third-party-licenses/SDL-LICENSE.txt"
if "$repo_root/tools/audit-linux-native-assets.sh" "$fixture" "$work_dir/manifest.tsv" >/dev/null 2>&1; then
  echo "Native audit accepted a missing required notice." >&2
  exit 1
fi
