# Implementation Roadmap

## 0. Foundation — implemented

Canonical docs, .NET solution, pinned dependencies, reproducible build, `eye --version`.

## 1. Single-tool MCP — implemented

Stateless Streamable HTTP, exactly one `eye` tool, stable envelope, capabilities and system information.

## 2. Native execution — implemented

PowerShell, cmd, direct execution, WSL, environment/cwd/stdin/timeout, reconnectable process handles, incremental output, request-independent capture, native Windows Job Objects, and true ConPTY terminal sessions with input and resize.

## 3. Files and development — baseline implemented

Chunked binary file operations and ordinary directory/file operations are implemented. Native Git, build tools, downloads, installation, Docker, and artifacts remain available through raw execution and can gain structured operations only where they add real capability.

## 4. Authority split — implemented and installed

LocalSystem service mode, elevated interactive-session mode, asynchronous named-pipe forwarding, execution contexts, and routed process-handle ownership are implemented, tested, installed, and runtime-proven on the laptop.

## 5. Direct ChatGPT connection

Install the pinned official tunnel client, register the custom ChatGPT app, expose only `eye`, and prove end-to-end calls from phone and web.

## 6. Cutover

Remove HEC/VPS from the normal path only after direct phone access works.

## 7. Desktop and browser — baseline implemented

Implemented:

- monitor and virtual-screen enumeration;
- top-level window inspection, activation, movement, sizing, and show-state control;
- pointer movement, clicks, and scrolling;
- Unicode text input and named key/chord input;
- Unicode clipboard read/write;
- desktop, region, and window snapshots in PNG/JPEG;
- native MCP image content for screenshots plus structured metadata;
- direct UI Automation 3 element discovery and common semantic control patterns;
- stateless UI selectors with no global element registry;
- mandatory service-to-owner-session routing for all desktop/capture/UIA operations.
- persistent Chromium profiles launched with loopback CDP and controlled through Playwright;
- reconnect across separate `eye.exe` invocations using on-disk profile/CDP state;
- tabs, navigation, click/fill/key input, waits, uploads, downloads, text/HTML snapshots, JavaScript evaluation, and raw CDP;
- browser screenshots returned as MCP image content through the installed service/session path.

Still required:

- Windows Graphics Capture / DXGI backend for sustained capture, recording, and hardware-composed cases.

## 8. Unity and Unreal

Native command-line/editor scripting/build/test integration, then visible editor control.

## 9. GPU, models, audio, camera, devices, and game control

All additions remain behind the one stable `eye` schema.
