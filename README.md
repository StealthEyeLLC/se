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

The current baseline implements:

- the `eye` CLI and Streamable HTTP MCP server;
- exactly one MCP tool named `eye` with stable `{op,args}` input;
- stateless concurrent MCP transport using the official C# MCP SDK;
- `capabilities`, `system.info`, `system.status`, and `system.doctor`;
- raw PowerShell, cmd, direct executable, and WSL execution;
- `current`, `system`, `user`, and `interactive` execution-context semantics;
- service-to-session routing over compact asynchronous named-pipe IPC;
- reconnectable long-running process handles with incremental stdout/stderr;
- distinct `proc_s_*`, `proc_u_*`, and `proc_c_*` process ownership;
- binary-safe chunked file reads and writes plus ordinary file operations;
- one executable in service, interactive-session, and local CLI modes;
- a PowerShell 5.1-compatible installer for the LocalSystem service and elevated logon task;
- behavioral tests for files, native execution, durable output capture, and service/session routing.

ConPTY, Job Objects, desktop capture, browser automation, Unity, Unreal, the Secure MCP Tunnel, and model operations follow behind the same public tool.

## Development

```powershell
./scripts/build.ps1
./artifacts/publish/eye.exe --version
./artifacts/publish/eye.exe call capabilities
./artifacts/publish/eye.exe call system.info
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
