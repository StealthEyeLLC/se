# Bootstrap

The existing HEC reverse SSH tunnel may be used to build and install StealthEye. It is not part of the final runtime.

## Build and test

```powershell
cd X:\Repos\se
.\scripts\build.ps1
.\artifacts\publish\eye.exe --version
.\artifacts\publish\eye.exe call capabilities
.\artifacts\publish\eye.exe call system.info
```

The build script restores, compiles with warnings as errors, runs the behavioral suite, publishes a self-contained win-x64 executable, and exercises `eye --version`.

## Local server

```powershell
.\artifacts\publish\eye.exe serve
```

Endpoints:

```text
http://127.0.0.1:37921/healthz
http://127.0.0.1:37921/readyz
http://127.0.0.1:37921/mcp
```

## Install

Run an elevated Windows PowerShell 5.1 or newer session:

```powershell
.\scripts\install.ps1
```

The installer:

- rebuilds and tests the source;
- installs `eye.exe` under Program Files;
- creates a protected ProgramData configuration;
- creates the LocalSystem Windows service;
- creates the elevated owner-session logon task;
- starts both modes;
- verifies HTTP health and `session.info` over the named pipe.

Tunnel credentials and custom ChatGPT app registration are completed only after the local service passes direct tests.
