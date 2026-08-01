# Security policy

## Supported versions

TCJ Framework is currently in preview and has not published a stable release.

| Version | Supported |
| --- | --- |
| Latest preview source on supported branches | Yes |
| Older preview snapshots | No guaranteed support |

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability.

Use GitHub's **Private vulnerability reporting** feature for the repository:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Include affected components, impact, reproduction details, and any proposed mitigation.

Do not include real credentials, personal data, or production secrets in a report.

## Response expectations

The maintainer will make a best effort to acknowledge a report, assess impact, coordinate a fix, and publish remediation guidance. Response times are not guaranteed while the project is maintained independently.

## Security boundaries

Consumers remain responsible for:

- authentication and authorization policy;
- secret storage and connection-string protection;
- database permissions and migrations;
- dependency and platform patching;
- logging and disclosure controls;
- validating any custom error metadata returned to clients.
