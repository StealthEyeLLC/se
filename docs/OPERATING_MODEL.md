# Operating Model

## Machine

The laptop is primary and authoritative. The VPS/HEC path is temporary bootstrap scaffolding only.

## Paths

```text
X:\Repos\se
X:\Repos\<project>
X:\Build\<project>

E:\StealthEye\models
E:\StealthEye\datasets
E:\StealthEye\checkpoints
E:\StealthEye\artifacts
E:\StealthEye\archives
E:\StealthEye\media

C:\Program Files\StealthEye\eye.exe
C:\ProgramData\StealthEye\config.json
C:\ProgramData\StealthEye\run\processes
C:\ProgramData\StealthEye\logs
%LOCALAPPDATA%\StealthEye\run\processes
```

`X:` is active development space but physically consumes the internal SSD through its VHDX. `E:` is bulk data and must not be silently replaced by `C:` when disconnected.

SYSTEM process output belongs under ProgramData. Owner-session process output belongs under LocalAppData. They are deliberately separate so neither authority depends on unsafe shared write permissions.

## Executable modes

```text
eye --version
eye serve
eye session
eye call <op> [json]
eye status
eye doctor
```

## Service

The StealthEye Windows service starts automatically as LocalSystem and hosts the loopback MCP server. Ordinary Windows SCM recovery may restart it after a crash.

## Session helper

The same executable starts in the owner session at logon with highest available privileges and communicates over asynchronous local named-pipe IPC. IPC uses compact one-record-per-line JSON and supports concurrent pipe instances.

## Context routing

The service executes `system` work locally. It forwards `user` and `interactive` execution to the owner-session process. User-owned process handles retain a `proc_u_*` prefix so later reads, writes, status calls, stops, and removals route to the same process registry.

## Tunnel

The supported OpenAI tunnel client runs on the laptop and points directly at the loopback MCP endpoint. No inbound firewall rule or public listener is required.

## Failures

Return real native exit codes, errors, stdout/stderr, process state, and transport readiness. Do not create approval tickets, incidents, receipts, or workflow states.
