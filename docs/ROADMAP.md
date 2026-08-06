# Implementation Roadmap

## 0. Foundation — implemented

Canonical docs, .NET solution, pinned dependencies, reproducible build, `eye --version`.

## 1. Single-tool MCP — implemented

Stateless Streamable HTTP, exactly one `eye` tool, stable envelope, capabilities and system information.

## 2. Native execution — baseline implemented

PowerShell, cmd, direct execution, WSL, environment/cwd/stdin/timeout, process handles, incremental output, and request-independent output capture.

Still required before process control is complete:

- ConPTY for true interactive terminals;
- Windows Job Objects for process-tree lifetime and accounting.

## 3. Files and development — baseline implemented

Chunked binary file operations and ordinary directory/file operations are implemented. Native Git, build tools, downloads, installation, Docker, and artifacts remain available through raw execution and can gain structured operations only where they add real capability.

## 4. Authority split — implemented in code

LocalSystem service mode, elevated interactive-session mode, asynchronous named-pipe forwarding, execution contexts, and routed process-handle ownership are implemented and covered by integration tests. The installer is ready for the first permitted elevated installation run.

## 5. Direct ChatGPT connection

Install the pinned official tunnel client, register the custom ChatGPT app, expose only `eye`, and prove end-to-end calls from phone and web.

## 6. Cutover

Remove HEC/VPS from the normal path only after direct phone access works.

## 7. Desktop and browser

UI Automation, DXGI/Windows Graphics Capture, native input, persistent browser profiles, Playwright, CDP, downloads/uploads.

## 8. Unity and Unreal

Native command-line/editor scripting/build/test integration, then visible editor control.

## 9. GPU, models, audio, camera, devices, and game control

All additions remain behind the one stable `eye` schema.
