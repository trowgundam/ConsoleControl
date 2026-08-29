# ConsoleControl portable Linux release

This archive contains self-contained .NET builds of the ConsoleControl daemon, GUI, and MCP server for x86-64 Linux. It does not include controller-bridge firmware. The self-contained claim applies only after a controller bridge has been flashed; building and initially flashing that firmware requires the separate source archive and its documented toolchain.

## Requirements

- Linux with glibc 2.38 or newer. Ubuntu 24.04 or newer meets this baseline. Ubuntu 22.04, Debian 12, and RHEL 9 do not.
- `ffmpeg` and `v4l2-ctl` on `PATH`
- BlueZ and D-Bus
- The distribution's Fontconfig, FreeType, Expat, zlib, bzip2, libpng, Brotli, and X11 or Wayland client libraries
- A Bluetooth adapter, a supported video-capture device, and an nRF52840 controller bridge running ConsoleControl firmware

The firmware source and build procedure are in the source archive attached to the same release. Follow `docs/hardware-proof.md` there. Only the Teyleten Robot Pro Micro nRF52840 clone sold under Amazon ASIN `B0CYLNZ6V4` has been tested.

## Start a watched session

Run these commands from the extracted archive:

```sh
bin/consolecontrol-daemon --adapter hci0
```

In another terminal, run:

```sh
bin/consolecontrol-gui --daemon http://127.0.0.1:5041
```

Choose the controller bridge and video source in the GUI. The GUI takes control automatically when no other client owns it. Choose **Take Control** only when another client owns the controller lease. Use **Configure input mapping...** to map a keyboard or gamepad.

The daemon also serves MJPEG video on `http://127.0.0.1:5042/video/live.mjpeg`. Both listeners are restricted to `127.0.0.1` and have no authentication. Any process on the local machine can observe video or request controller control. The proof firmware also accepts state writes from an unpaired BLE central, so a nearby Bluetooth device can bypass daemon control arbitration. Use this release only where local processes and nearby Bluetooth devices are trusted.

## Connect an agent

Copy `examples/codex-config.toml` into the project's `.codex/config.toml`, then replace its command with the absolute path to `bin/consolecontrol-mcp` in this archive. Start the daemon before connecting the MCP client.

The archive includes two agent skills under `skills/`. To make them visible to a project-scoped Codex session, copy both skill directories into that project's `.agents/skills/` directory:

```sh
mkdir -p /path/to/project/.agents/skills
cp -a skills/operate-console-control skills/navigate-nintendo-switch /path/to/project/.agents/skills/
```

The operation skill asks whether to start a watched GUI session or run headless, then explains hardware selection, screenshots, control leases, digital input, sequences, and structured error recovery.
