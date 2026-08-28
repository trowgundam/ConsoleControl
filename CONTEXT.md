# ConsoleControl

ConsoleControl describes one local interaction with a game console through captured video and emulated controller input.

## Language

**Console**:
The game system that produces video and receives controller input.
_Avoid_: Host, target machine

**Console session**:
The active relationship between one console, one capture source, and an optional controller bridge.
_Avoid_: Connection, device session

**Capture source**:
The device that converts the console's video output into frames available to ConsoleControl.
_Avoid_: Camera, KVM

**Controller bridge**:
The device that receives controller state from ConsoleControl and presents a controller to the console.
_Avoid_: Dongle, adapter, puck

**Controller personality**:
The identity and behavior that a controller bridge presents to a console.
_Avoid_: Controller mode, driver, profile

**Canonical controller state**:
A complete, console-independent snapshot of buttons, directional controls, sticks, and triggers.
_Avoid_: Report, input event

**Control lease**:
Time-limited authority for one client to change the console's controller state.
_Avoid_: Lock, ownership token

**Interactive client**:
A client operated directly by a person. An interactive client may revoke an automation client's control lease.
_Avoid_: Human client, GUI owner

**Automation client**:
A client that controls the console without direct input for each action.
_Avoid_: Agent, bot

**Observer**:
A client that can view the console without holding the control lease.
_Avoid_: Read-only client, viewer
