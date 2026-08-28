#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
firmware_dir="$repo_dir/firmware/ConsoleControl.ControllerBridge"
probe_project="$repo_dir/tools/ConsoleControl.DeviceProbe/ConsoleControl.DeviceProbe.csproj"
artifact_dir="$repo_dir/artifacts/hardware-proof"
application_start=0x00026000
application_end=0x000F4000

usage() {
  echo "Usage: $0 inspect <UF2 mount>"
  echo "       $0 build"
  echo "       $0 verify <UF2 file>"
}

case "${1:-}" in
  inspect)
    test "$#" -eq 2 || { usage; exit 2; }
    dotnet run --project "$probe_project" -- inspect-mount "$2"
    ;;
  build)
    test "$#" -eq 1 || { usage; exit 2; }
    mkdir -p "$artifact_dir"
    build_dir="$artifact_dir/cpp-build"
    output_dir="$artifact_dir/cpp-output"
    arduino-cli compile --clean \
      --fqbn adafruit:nrf52:feather52840 \
      --build-path "$build_dir" \
      --output-dir "$output_dir" \
      --build-property recipe.objcopy.zip.pattern=true \
      "$firmware_dir"
    hex="$output_dir/ConsoleControl.ControllerBridge.ino.hex"
    bin="$artifact_dir/controller-bridge-proof.bin"
    uf2="$artifact_dir/controller-bridge-proof.uf2"
    arm-none-eabi-objcopy -I ihex -O binary "$hex" "$bin"
    dotnet run --project "$probe_project" -- pack-uf2 "$bin" "$uf2" "$application_start" "$application_end"
    ;;
  verify)
    test "$#" -eq 2 || { usage; exit 2; }
    dotnet run --project "$probe_project" -- verify-uf2 "$2" "$application_start" "$application_end"
    ;;
  *)
    usage
    exit 2
    ;;
esac
