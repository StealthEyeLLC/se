# Repository Layout

```text
se/
|-- README.md
|-- StealthEye.slnx
|-- Directory.Build.props
|-- Directory.Packages.props
|-- global.json
|-- docs/
|-- src/
|   `-- Eye/
|       |-- Configuration/
|       |-- Mcp/
|       |-- Operations/
|       |-- Runtime/
|       `-- Windows/
|-- tests/
|   `-- Eye.Tests/
`-- scripts/
```

Rules:

- one primary executable: `eye.exe`;
- no microservices, plugin framework, policy packages, workflow packages, approval packages, receipt packages, evidence packages, audit packages, or database by default;
- use plainly named native components;
- add tests only where they provide real value;
- keep install scripts ordinary and readable.
