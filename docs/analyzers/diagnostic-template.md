# TCJxxxx: Diagnostic title

| Property | Value |
| --- | --- |
| ID | `TCJxxxx` |
| Category | `TCJ.Category` |
| Default severity | `Error`, `Warning`, or `Info` |
| Code fix | Mandatory, optional, or unavailable |
| Introduced in | Unshipped or package version |

## Cause

Describe the exact TCJ contract that triggers this diagnostic. State whether the analyzer proves an invalid framework state or reports a correctness/compatibility risk.

## Rule description

Explain what the analyzer checks, including important symbol-resolution or project-configuration rules. Avoid implementation details that are not useful to consumers.

## How to fix

Describe the safe remediation. If a code fix is available, state what it changes and what it deliberately preserves. If no code fix is offered, explain why an automatic transformation would be unsafe.

## Examples

### Code with diagnostic

```csharp
// Minimal example that produces TCJxxxx.
```

### Corrected code

```csharp
// Minimal corrected example.
```

## Suppression

Explain when suppression can be justified and what risk remains. Prefer standard `.editorconfig`, scoped pragma, or `SuppressMessage` mechanisms. Do not recommend disabling all TCJ diagnostics.

## Known limitations

Document conservative-analysis cases, intentional false negatives, unsupported language/project patterns, or other boundaries that consumers should understand.

## Compatibility notes

Record compatibility-sensitive behavior such as default severity, category, code-fix semantics, and interactions with runtime TCJ validation. State explicitly when runtime behavior remains unchanged.
