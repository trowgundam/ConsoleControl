#!/usr/bin/env bash
set -euo pipefail

duration_minutes="${1:-10}"
video_uri="${2:-http://127.0.0.1:5042/video/live.mjpeg}"

if ! [[ "$duration_minutes" =~ ^[1-9][0-9]*$ ]]; then
  echo "duration must be a positive whole number of minutes" >&2
  exit 2
fi

printf 'minute\tdaemon_kib\tgui_kib\tffmpeg_kib\tframes_per_second\n'
for ((minute = 0; minute <= duration_minutes; minute++)); do
  daemon_pid="$(pgrep -n -f '/ConsoleControl.Daemon$')"
  gui_pid="$(pgrep -n -f '/ConsoleControl.Gui$')"
  ffmpeg_pid="$(pgrep -n -x ffmpeg)"
  daemon_rss="$(ps -o rss= -p "$daemon_pid" | tr -d ' ')"
  gui_rss="$(ps -o rss= -p "$gui_pid" | tr -d ' ')"
  ffmpeg_rss="$(ps -o rss= -p "$ffmpeg_pid" | tr -d ' ')"

  sample="$(mktemp)"
  curl --silent --max-time 1 "$video_uri" --output "$sample" || true
  frame_count="$( (LC_ALL=C grep -a -o -- '--consolecontrol-frame' "$sample" || true) | wc -l)"
  rm -f -- "$sample"
  if ((frame_count < 30)); then
    echo "video stream produced only $frame_count frames during minute $minute" >&2
    exit 1
  fi

  printf '%d\t%s\t%s\t%s\t%s\n' \
    "$minute" "$daemon_rss" "$gui_rss" "$ffmpeg_rss" "$frame_count"
  if ((minute < duration_minutes)); then
    sleep 60
  fi
done
