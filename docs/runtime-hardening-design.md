# Runtime hardening design

This checkpoint repairs four failures found during the publication review without changing the daemon-owned console-session boundary.

## Input transitions

The GUI maps each host snapshot at the input boundary. A shared Core mailbox retains every complete canonical state whose digital portion changed. Analog-only updates share one latest-state slot. Both the GUI writer and gRPC state pump drain digital states in order, then the newest analog state. This preserves taps through the transport boundary while keeping high-rate stick motion bounded.

Digital button and D-pad composition lives in `ConsoleControl.Core.ControllerStateComposer`. Mapping, GUI overlays, and daemon automation use the same implementation.

## Automation lease completion

`IAutomationSession.Completion` reports release, preemption, bridge loss, or transport loss. The gRPC client completes it when the control stream ends, even when the caller is idle. The MCP control owner observes that task and removes a dead session before reporting or running automation.

## Video selection

An empty configuration remains unselected. A persisted source remains selected while absent, and the capture loop re-enumerates until that exact stable ID returns. Only an explicit selection changes the ID or revision. Reconnection uses the source's newly enumerated mode rather than stale startup metadata.

## Release audit

The Linux packager classifies every ELF file in the assembled archive against a reviewed manifest. Each class names its required notices. Unknown files, ambiguous matches, or missing notices fail packaging. The generated inventory records the exact path, digest, component, and notices for release review without using hashes as an acceptance allowlist.
