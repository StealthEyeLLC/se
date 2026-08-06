# StealthEye Hard Constraints

This document is canonical.

## Identity

- Repository: `StealthEyeLLC/se`
- Product: **StealthEye**
- CLI and primary executable: **`eye`**
- Primary host: `STEALTHEYELLC`
- User-facing integration: one custom ChatGPT app exposing exactly one MCP tool named `eye`

## Objective

Give ChatGPT the broadest practical native control of the laptop through one stable public tool and one laptop-native product.

## Required steady-state path

```text
ChatGPT -> one custom app -> eye -> Secure MCP Tunnel -> laptop-native StealthEye
```

No VPS, HEC dependency, second control plane, remote orchestration tier, fleet abstraction, or ChatGPT desktop-app requirement remains after cutover.

## Explicit non-goals

Do not add project-level policy engines, command allowlists, permission tiers, approval workflows, safety wrappers, workflow engines, generalized task databases, planner hierarchies, multi-agent systems, mandatory sandboxes, rollback theater, receipts, evidence packages, action ledgers, audit products, dashboards, or abstractions that merely rename native facilities.

## Doctrine

- Prefer one native executable and ordinary OS facilities.
- Prefer raw native capability over wrappers.
- Keep the public interface small and the implementation powerful.
- Use state only when real runtime capability requires it.
- Keep logs bounded and diagnostic.
- External platform and operating-system constraints are not bypassed; StealthEye simply adds no duplicate restriction layer.
