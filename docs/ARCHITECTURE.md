# Architecture

## Production topology

```text
ChatGPT phone or web
        |
        v
one custom ChatGPT app
        |
        v
one MCP tool: eye
        |
        v
OpenAI Secure MCP Tunnel
        |
        v
official tunnel-client.exe
        |
        v
127.0.0.1 Streamable HTTP
        |
        v
eye.exe service (LocalSystem)
   |-- MCP dispatcher
   |-- shells / files / processes / WSL / Git / builds / installs / models
   `-- asynchronous named-pipe IPC
             |
             v
       eye.exe session
       |-- desktop capture and input
       |-- UI Automation
       |-- browsers
       |-- Unity and Unreal
       `-- interactive applications and devices
```

The tunnel is transport infrastructure, not a second StealthEye control plane.

## Implementation

- C# and .NET 10
- official `ModelContextProtocol.AspNetCore` SDK, pinned to stable `2.1.0`
- ASP.NET Core/Kestrel bound only to loopback
- stateless Streamable HTTP
- one self-contained win-x64 executable, ReadyToRun, not Native AOT initially
- Windows service for backend authority
- same executable in the logged-in owner session for GUI authority
- asynchronous named pipes between service and session process

## Authority

`eye.exe service` runs as LocalSystem. It owns noninteractive administration, installs, services, files, networking, builds, and other machine-level operations.

`eye.exe session` runs elevated in the owner's interactive session. It owns the desktop, profile-bound tools, browsers, WSL identity, Unity, Unreal, audio, camera, and visible applications.

Execution context selects real Windows context; it is not a StealthEye permission system.

## Concurrency

MCP requests are independently asynchronous. There is no global execution lock. Long-running commands return handles, allowing work to continue after the initiating MCP call returns. Serialization is limited to resources that cannot safely interleave, such as one physical input stream or conflicting writes to one file.

## Process architecture

The baseline uses redirected process streams with output persisted to bounded runtime files and cursor-based reads. Production expansion adds ConPTY for true interactive terminals and Windows Job Objects for process-tree lifetime control.

## Desktop architecture

Desktop automation uses, in order:

1. Windows UI Automation for semantic controls;
2. DXGI Desktop Duplication / Windows Graphics Capture for visual state;
3. native input APIs for raw pointer and keyboard fallback.

## Browser architecture

Browser control uses a Playwright-managed persistent profile first, CDP attachment where appropriate, Windows UI Automation next, and visual input fallback last.

## Game engines

Unity uses batch mode, editor scripting, tests, and build APIs before GUI control. Unreal uses Automation Tool, BuildGraph, commandlets, editor Python, native builds, tests, and GUI control where visual asset work requires it.
