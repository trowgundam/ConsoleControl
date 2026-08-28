#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ "$#" -ne 2 ]]; then
  echo "Usage: $0 <version> <output-directory>" >&2
  exit 2
fi

version="$1"
output_dir="$2"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Version must use MAJOR.MINOR.PATCH with an optional prerelease suffix." >&2
  exit 2
fi

required_files=(
  "$repo_root/LICENSE"
  "$repo_root/THIRD-PARTY-NOTICES.md"
  "$repo_root/examples/codex-config.toml"
)

for required_file in "${required_files[@]}"; do
  if [[ ! -f "$required_file" ]]; then
    echo "Release input is missing: ${required_file#"$repo_root/"}" >&2
    exit 1
  fi
done

mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

package_name="ConsoleControl-$version-linux-x64"
package_root="$work_dir/$package_name"
libexec_root="$package_root/libexec/consolecontrol"

mkdir -p \
  "$package_root/bin" \
  "$libexec_root/daemon" \
  "$libexec_root/gui" \
  "$libexec_root/mcp" \
  "$package_root/controller-personalities" \
  "$package_root/examples" \
  "$package_root/skills" \
  "$package_root/third-party-licenses"

publish_app() {
  local project="$1"
  local destination="$2"

  dotnet publish "$repo_root/$project" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --no-restore \
    --disable-build-servers \
    -p:ContinuousIntegrationBuild=true \
    -p:Version="$version" \
    --output "$destination"
}

publish_app src/ConsoleControl.Daemon/ConsoleControl.Daemon.csproj "$libexec_root/daemon"
publish_app src/ConsoleControl.Gui/ConsoleControl.Gui.csproj "$libexec_root/gui"
publish_app src/ConsoleControl.Mcp/ConsoleControl.Mcp.csproj "$libexec_root/mcp"

launcher="$work_dir/consolecontrol-launcher"
# These single-quoted strings must preserve expansions for the generated launcher.
# shellcheck disable=SC2016
printf '%s\n' \
  '#!/usr/bin/env bash' \
  'set -euo pipefail' \
  'launcher_path="$(readlink -f -- "$0")"' \
  'package_root="$(cd "$(dirname "$launcher_path")/.." && pwd)"' \
  'case "$(basename "$0")" in' \
  '  consolecontrol-daemon) app_dir=daemon; executable=ConsoleControl.Daemon ;;' \
  '  consolecontrol-gui) app_dir=gui; executable=ConsoleControl.Gui ;;' \
  '  consolecontrol-mcp) app_dir=mcp; executable=ConsoleControl.Mcp ;;' \
  '  *) echo "Unknown ConsoleControl launcher: $0" >&2; exit 2 ;;' \
  'esac' \
  'exec "$package_root/libexec/consolecontrol/$app_dir/$executable" "$@"' \
  > "$launcher"
chmod 0755 "$launcher"

install -m 0755 "$launcher" "$package_root/bin/consolecontrol-daemon"
install -m 0755 "$launcher" "$package_root/bin/consolecontrol-gui"
install -m 0755 "$launcher" "$package_root/bin/consolecontrol-mcp"

cp -a "$repo_root/controller-personalities/." "$package_root/controller-personalities/"
cp -a "$repo_root/.agents/skills/." "$package_root/skills/"
cp "$repo_root/examples/codex-config.toml" "$package_root/examples/codex-config.toml"
cp "$repo_root/LICENSE" "$package_root/LICENSE"
cp "$repo_root/THIRD-PARTY-NOTICES.md" "$package_root/THIRD-PARTY-NOTICES.md"

desktop_license_files=(
  Avalonia-LICENSE.txt
  DotNetExtensions-LICENSE.txt
  DotNetRuntime-LICENSE.txt
  GrpcDotNet-LICENSE.txt
  HarfBuzz-LICENSE.txt
  McpCSharpSdk-LICENSE.txt
  MicroCom-LICENSE.txt
  ProtocolBuffers-LICENSE.txt
  SDL-LICENSE.txt
  SDL3-CS-LICENSE.txt
  Skia-LICENSE.txt
  SkiaSharp-LICENSE.txt
  Tmds.DBus-LICENSE.txt
)

for license_file in "${desktop_license_files[@]}"; do
  cp \
    "$repo_root/third-party-licenses/$license_file" \
    "$package_root/third-party-licenses/$license_file"
done

nuget_root="$(dotnet nuget locals global-packages --list | sed 's/^global-packages: //')"
netcore_version="$(jq -r '.libraries | keys[] | select(startswith("runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/")) | split("/")[1]' "$libexec_root/daemon/ConsoleControl.Daemon.deps.json")"
aspnet_version="$(jq -r '.libraries | keys[] | select(startswith("runtimepack.Microsoft.AspNetCore.App.Runtime.linux-x64/")) | split("/")[1]' "$libexec_root/daemon/ConsoleControl.Daemon.deps.json")"

cp \
  "$nuget_root/microsoft.netcore.app.runtime.linux-x64/$netcore_version/THIRD-PARTY-NOTICES.TXT" \
  "$package_root/third-party-licenses/DotNetRuntime-$netcore_version-THIRD-PARTY-NOTICES.txt"
cp \
  "$nuget_root/microsoft.aspnetcore.app.runtime.linux-x64/$aspnet_version/THIRD-PARTY-NOTICES.TXT" \
  "$package_root/third-party-licenses/AspNetCoreRuntime-$aspnet_version-THIRD-PARTY-NOTICES.txt"

archive_name="$package_name.tar.gz"
archive="$output_dir/$archive_name"
source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" log -1 --format=%ct)}"
tar \
  --sort=name \
  --mtime="@$source_date_epoch" \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  --create \
  --file=- \
  --directory="$work_dir" \
  "$package_name" \
  | gzip -n -9 > "$archive"

(cd "$output_dir" && sha256sum "$archive_name" > "$archive_name.sha256")
printf '%s\n' "$archive"
