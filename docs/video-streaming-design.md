# Video streaming design

## Problem

ConsoleControl needs selectable, low-latency capture without coupling video failures to controller input. The NanoKVM exposes one video node and one metadata node. Its video node supplies MJPEG or YUYV at 1920x1080 and 60 fps. Measured MJPEG pass-through is about 8.4 MB/s. Decoded BGRA would move about 498 MB/s between processes before gRPC or rendering overhead.

## Usage

The GUI gets capture inventory and changes the selected source through `IConsoleSession`. The daemon returns a loopback multipart MJPEG URI with the inventory. The GUI opens that URI as one continuous HTTP response and displays only the newest complete frame.

```csharp
VideoInventory inventory = await session.GetVideoInventoryAsync(stop.Token);
await session.SelectVideoSourceAsync(source.Id, inventory.Revision, stop.Token);
await presenter.WatchAsync(inventory.LiveStreamUri, stop.Token);
```

## Shape

The daemon owns source enumeration, stable selection, one FFmpeg child process, and one latest-frame hub. Source IDs use `/dev/v4l/by-id` names rather than `/dev/videoN`. The GUI dropdown never persists or opens a device node.

FFmpeg negotiates the selected source as MJPEG at the best supported mode up to 1920x1080 at 60 fps. It copies frames into its `mpjpeg` muxer without transcoding. The installed FFmpeg emits a `Content-length` header for every part. The adapter parses that exact length, rejects oversized parts, drops malformed JPEG frames, and publishes compressed frames. It drains stderr concurrently and applies bounded process shutdown.

The daemon keeps one latest frame shared by all HTTP viewers. Capture replaces that frame without waiting for a viewer. The `/video/live.mjpeg` endpoint writes a multipart response with an explicit `Content-Length` per frame. The GUI keeps one encoded frame waiting for decode, decodes off the UI thread, and posts the bitmap to Avalonia.

gRPC carries source inventory, selection revisions, and capture status. It does not carry live frames. A later screenshot RPC can copy the daemon's retained latest JPEG.

Controller and video runtimes are siblings. A missing bridge does not prevent video startup. A capture failure does not release control. Daemon shutdown stops FFmpeg and controller output independently under bounded deadlines.

## Synthesis decision

The measured data rules out decoded BGRA transport for the first release. Both architecture candidates selected compressed MJPEG. The initial comparison preferred a JPEG-aware `image2pipe` parser over probabilistic multipart boundary search. A hardware proof then showed FFmpeg supplies exact multipart `Content-Length` values, so the implementation uses multipart length framing without boundary search. The public live-video boundary is loopback HTTP rather than per-frame gRPC, following Jeff's preference for an actual video stream.

## Tradeoffs accepted

- We accept one JPEG decode per GUI viewer in exchange for avoiding raw-frame transport and video transcoding.
- We accept a second loopback listener in exchange for keeping live media independent of the HTTP/2-only gRPC endpoint.
- We require a system FFmpeg executable during development in exchange for avoiding native FFmpeg ABI packaging.
- We expose source selection, not mode selection, in the first GUI. The daemon chooses the best supported MJPEG mode up to 1080p60.

## Verification

The automated checks cover latest-frame replacement and source revision conflicts. A recorded three-second FFmpeg stream also checks multipart parsing and malformed-frame recovery.

The NanoKVM hardware check found the stable source, opened 1920x1080 MJPEG at 60 fps, and displayed the Switch picture in Avalonia. A ten-minute run sampled 59 to 61 frames each second. FFmpeg resident memory stayed fixed at 173,056 KiB. GUI resident memory moved between 273,640 KiB and 337,780 KiB and dropped after collection. Daemon resident memory rose from 102,996 KiB to 107,732 KiB, with no capture interruption.

Ten loopback connections received their first response byte in 0.485 to 3.409 milliseconds. This measures delivery from the daemon's retained latest frame. It does not measure HDMI input, JPEG decode, Avalonia presentation, or display latency.

An end-to-end response probe sent three D-pad right and D-pad left pairs through the normal gRPC control session. It watched the decoded 160x90 video stream for the first frame whose mean absolute pixel difference exceeded five times the measured idle noise. The six responses took 149.8 to 180.6 milliseconds, with a median of 164.4 milliseconds. This includes client scheduling, Bluetooth controller delivery, Switch processing, HDMI output, NanoKVM capture, daemon streaming, and probe decoding. It excludes Avalonia presentation and monitor latency.

The current hardware setup has one capture device, so switching between two physical sources cannot be tested. A second device is not required for the current release. True glass-to-glass latency remains unmeasured.

Physical recovery checks covered both a daemon restart and a NanoKVM USB disconnect. The GUI cleared its stale bitmap, retried the MJPEG connection, and resumed controller input after the daemon returned. On USB reconnection, the NanoKVM temporarily supplied 640x480 JPEG frames despite reporting a 1920x1080 V4L2 mode. The adapter now reads each JPEG SOF marker and rejects a frame whose encoded dimensions differ from the selected mode. The daemon retried until the NanoKVM supplied 1920x1080 frames, then the GUI recovered without a process restart or a squashed picture.
