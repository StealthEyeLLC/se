# Single-Tool Interface

## Public tool

```text
name: eye
```

## Input

```json
{
  "op": "string",
  "args": {}
}
```

`args` is optional and operation-specific. The ChatGPT app does not need republishing when the operation vocabulary expands.

## Output

```json
{
  "ok": true,
  "result": {}
}
```

or:

```json
{
  "ok": false,
  "error": {
    "message": "...",
    "code": "optional_machine_code",
    "details": {}
  }
}
```

The result is not a receipt or audit record.

## Implemented vocabulary

```text
capabilities
system.info
system.status
system.doctor
run
wsl.run
process.start
process.read
process.write
process.resize
process.stat
process.list
process.stop
process.remove
terminal.open
terminal.read
terminal.write
terminal.resize
terminal.stat
terminal.list
terminal.stop
terminal.remove
file.read
file.write
file.list
file.stat
file.mkdir
file.copy
file.move
file.remove
file.hash
session.info
```

## Execution contexts

`run`, `wsl.run`, and `process.start` accept:

```text
current
system
user
interactive
```

At the installed MCP endpoint, `system` executes in the LocalSystem service. `user` and `interactive` are forwarded to the elevated owner-session process. WSL defaults to the owner session unless `context: system` is explicitly requested.

This is selection of real Windows execution context, not a StealthEye permission system.

## Raw execution

`run` permanently preserves direct PowerShell, cmd, executable plus argument vector, and WSL capability. It supports working directory, environment overrides, stdin, timeout, and output limits.

## Long processes

`process.start` returns a handle:

```text
proc_s_*   service / LocalSystem process
proc_u_*   owner-session process
proc_c_*   local CLI process
```

`process.read` consumes stdout and stderr by byte offset. Service calls automatically route `proc_u_*` operations back to the session process. Output pumps are independent of the initiating request token, so capture continues after the starter request ends or disconnects.

`terminal.open` creates a true Windows pseudoterminal through the pinned Microsoft Pty.Net backend. `terminal.write` sends UTF-8 input, `terminal.read` returns cursor-based output, and `terminal.resize` changes the ConPTY dimensions. Managed processes are assigned to native Windows Job Objects so complete process trees can be stopped and cleaned up.

## Files

`file.read` and `file.write` support UTF-8 and base64. Reads are offset/length based so large binary content does not require one enormous JSON response.

## Local CLI argument input

```text
eye call <op> '<inline-json>'
eye call <op> --args-file <path>
eye call <op> @<path>
eye call <op> -
```

`-` reads JSON from standard input. File input avoids quoting problems in Windows PowerShell 5.1.
