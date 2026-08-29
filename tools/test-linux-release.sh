#!/usr/bin/env bash
set -euo pipefail

archive="${1:?Usage: $0 <release-archive.tar.gz>}"
checksum="$archive.sha256"

if [[ ! -f "$archive" || ! -f "$checksum" ]]; then
  echo "Release archive or checksum is missing." >&2
  exit 2
fi

archive_directory="$(cd "$(dirname "$archive")" && pwd)"
archive_name="$(basename "$archive")"
(cd "$archive_directory" && sha256sum --check "$(basename "$checksum")")

work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
tar -xzf "$archive" -C "$work_dir"

shopt -s nullglob
package_roots=("$work_dir"/ConsoleControl-*-linux-x64)
if [[ "${#package_roots[@]}" -ne 1 || ! -d "${package_roots[0]}" ]]; then
  echo "Release archive must contain one versioned ConsoleControl package directory." >&2
  exit 1
fi
package_root="${package_roots[0]}"

for launcher in consolecontrol-daemon consolecontrol-gui consolecontrol-mcp; do
  if [[ ! -x "$package_root/bin/$launcher" ]]; then
    echo "Release launcher is missing or not executable: bin/$launcher" >&2
    exit 1
  fi
done

"$package_root/bin/consolecontrol-daemon" --help >/dev/null
"$package_root/bin/consolecontrol-mcp" --help >/dev/null

if find "$package_root" -type f -name '*.uf2' -print -quit | grep -q .; then
  echo "Release archive unexpectedly contains controller firmware." >&2
  exit 1
fi

"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/audit-linux-native-assets.sh" \
  "$package_root"
printf 'Release archive smoke test passed: %s\n' "$archive_name"
