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

Bounded commands use redirected native streams. Long commands and terminals persist output to cursor-readable runtime files. Interactive sessions use the pinned Microsoft Pty.Net backend with its side-by-side ConPTY runtime, while Windows Job Objects own full process trees and terminate descendants when their handle is released.

## Desktop architecture

Desktop operations run only in the elevated owner-session process. The LocalSystem service routes desktop, capture, and UI Automation vocabulary through the named pipe instead of pretending that session 0 is the user's desktop.

The implemented stack is layered:

1. UI Automation 3 through the thin COM interop binding for semantic element discovery and supported control patterns;
2. GDI+/`PrintWindow` snapshot capture for desktop regions and application windows;
3. direct Win32 window, pointer, keyboard, and Unicode clipboard APIs as the raw fallback.

`screen.capture` returns ordinary structured metadata and, when called through MCP, an `ImageContentBlock` containing the actual PNG or JPEG bytes. The public tool remains `eye`; image content does not create a second tool or interface.

UI Automation selectors are resolved per call from window roots, points, properties, and runtime IDs. StealthEye does not keep an in-memory element registry that would serialize unrelated work or strand handles across process restarts. Unfiltered UI discovery defaults to direct children; targeted selectors may search subtrees.

Windows Graphics Capture / DXGI Desktop Duplication remains a useful later backend for sustained capture, hardware-composed surfaces, and screen recording. It is an upgrade path, not a replacement for the working snapshot path.

## Browser architecture

Browser control uses a Playwright-managed persistent profile first, CDP attachment where appropriate, Windows UI Automation next, and visual input fallback last.

## Game engines

Unity uses batch mode, editor scripting, tests, and build APIs before GUI control. Unreal uses Automation Tool, BuildGraph, commandlets, editor Python, native builds, tests, and GUI control where visual asset work requires it.
