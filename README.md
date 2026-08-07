# StealthEye

**Repository:** `StealthEyeLLC/se`
**Product:** StealthEye
**CLI / executable:** `eye`
**Primary host:** `STEALTHEYELLC`

StealthEye gives ChatGPT one stable MCP tool for broad native control of the owner's Windows laptop.

```text
ChatGPT -> one custom app -> one tool: eye -> Secure MCP Tunnel -> eye.exe -> Windows / WSL / desktop / browser / GPU / tools
```

## Maxim

> Maximum capability. Minimum bullshit. No theater.

## Current build

StealthEye `0.4.0` implements:

- the `eye` CLI and Streamable HTTP MCP server;
- exactly one MCP tool named `eye` with stable `{op,args}` input;
- stateless concurrent MCP transport using the official C# MCP SDK;
- `capabilities`, `system.info`, `system.status`, and `system.doctor`;
- raw PowerShell, cmd, direct executable, and WSL execution;
- `current`, `system`, `user`, and `interactive` execution-context semantics;
- service-to-session routing over compact asynchronous named-pipe IPC;
- reconnectable long-running process handles with incremental stdout/stderr;
- native Windows Job Objects for complete process-tree ownership;
- real ConPTY terminal sessions with streamed UTF-8 output, input, and resize;
- distinct `proc_s_*`, `proc_u_*`, and `proc_c_*` process ownership;
- binary-safe chunked file reads and writes plus ordinary file operations;
- native monitor and top-level window inspection;
- foreground activation, window movement, sizing, minimization, maximization, restoration, and hiding;
- serialized Win32 pointer, keyboard, and Unicode clipboard operations in the owner session;
- automatic service-to-session routing for all desktop operations;
- PNG/JPEG desktop, region, and window screenshots with optional scaling and file persistence;
- real MCP image content blocks for screenshots alongside the stable structured `{ok,result}` envelope;
- direct UI Automation 3 discovery, focus, invoke, value, toggle, selection, expand/collapse, and scroll-into-view operations;
- stateless semantic UI selectors instead of an in-memory element-handle registry;
- one executable in service, interactive-session, and local CLI modes;
- a PowerShell 5.1-compatible installer for the LocalSystem service and elevated logon task;
- behavioral tests for files, native execution, durable output capture, and service/session routing.

Browser automation, higher-performance Windows Graphics Capture/DXGI capture, Unity, Unreal, the Secure MCP Tunnel, and model operations follow behind the same public tool.

## Development

```powershell
./scripts/build.ps1
./artifacts/publish/eye.exe --version
./artifacts/publish/eye.exe call capabilities
./artifacts/publish/eye.exe call system.info
./artifacts/publish/eye.exe call terminal.open --args-file .\terminal.json
./artifacts/publish/eye.exe call display.list
./artifacts/publish/eye.exe call window.list --args-file .\windows.json
./artifacts/publish/eye.exe call screen.capture --args-file .\capture.json
./artifacts/publish/eye.exe call ui.find --args-file .\ui.json
./artifacts/publish/eye.exe serve
```

For arguments that are awkward to quote in Windows PowerShell:

```powershell
./artifacts/publish/eye.exe call run --args-file .\run.json
Get-Content .\run.json -Raw | ./artifacts/publish/eye.exe call run -
```

Local MCP endpoint:

```text
http://127.0.0.1:37921/mcp
```

See `docs/ARCHITECTURE.md`, `docs/INTERFACE.md`, and `docs/ROADMAP.md`.
