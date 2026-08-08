# ASP.NET Core end-to-end integration testing

`TCJ.AspNetCore` participates in the ASP.NET Core hosting, dependency-injection, authentication, authorization, exception-handling, Problem Details, and request-scope pipelines. Unit tests remain useful for individual types, but they cannot prove that those pieces work together after the application starts and an HTTP request crosses the real middleware pipeline.

The repository therefore keeps a dedicated in-memory integration suite under:

```text
tests/TCJ.AspNetCore.IntegrationTests/
```

The suite never binds a public TCP port, does not require a deployed server, and does not use external network services or permanent secrets.

## Test host structure

The integration fixture creates a normal `WebApplication`, configures `Microsoft.AspNetCore.TestHost`, calls the public `AddTcjAspNetCore` registration API, builds the application, and places `UseTcjAspNetCore` in the real request pipeline before authentication, authorization, and endpoint execution.

Representative test-only endpoints cover:

- application health and JSON round trips;
- current-user identity, claims, and roles;
- framework-service resolution;
- scoped, transient, and singleton lifetimes;
- validation, not-found, conflict, unauthorized, and forbidden Result mappings;
- unhandled exceptions;
- empty error status codes;
- authenticated and role-protected endpoints;
- request cancellation.

The test host and endpoints live only in the integration-test assembly. No test endpoint or test authentication type is compiled into a production package.

## Deterministic authentication

`TestAuthenticationHandler` is a test-only ASP.NET Core authentication handler. Requests opt into authentication with deterministic headers used only by the in-memory host. Tests can select a numeric user identifier, roles, additional claims, duplicate identifier claims, or an explicit authentication failure.

The diagnostics layer never records request headers. In particular, authorization headers, cookies, bearer tokens, raw test credentials, and password-like values are excluded or redacted before artifacts are uploaded.

## Development and Production behavior

The production fixture keeps `TcjAspNetCoreOptions.IncludeExceptionDetails` disabled. An unexpected exception therefore returns a safe `500` Problem Details response without the internal exception message or stack trace.

A dedicated Development test host enables `IncludeExceptionDetails` to exercise the documented opt-in development diagnostic behavior. Status-code mapping stays the same; only intentionally environment-dependent detail changes.

Both environment scenarios run on every supported workflow platform.

## Current-user and request-scope isolation

Authenticated tests send different deterministic principals through separate HTTP requests and assert that `ICurrentUserProvider` resolves only the active request identity. An anonymous request following authenticated requests must return the anonymous state.

Scoped marker services are resolved twice inside one request, compared across separate requests, and checked after disposal. Transient markers must differ within one request and singleton markers must remain stable across requests. Root-provider resolution of a scoped marker is rejected because the test host enables scope validation.

## Cancellation

The cancellation endpoint awaits the request's `RequestAborted` token. Tests cancel the client request, assert that endpoint logic observes cancellation, verify that the TCJ exception handler does not turn the canceled request into an application `500`, and then issue another anonymous request to prove that request identity was not retained.

## Local execution

Validate repository wiring first:

```bash
python3 eng/verify-aspnetcore-integration.py validate-config
```

Restore and run only the ASP.NET Core integration suite:

```bash
dotnet restore tests/TCJ.AspNetCore.IntegrationTests/TCJ.AspNetCore.IntegrationTests.csproj

dotnet test \
  tests/TCJ.AspNetCore.IntegrationTests/TCJ.AspNetCore.IntegrationTests.csproj \
  --configuration Release \
  --filter "Category=AspNetCore" \
  --logger "trx;LogFileName=aspnetcore-integration.trx" \
  --results-directory TestResults/AspNetCoreIntegration
```

Verify the generated result set and diagnostics:

```bash
python3 eng/verify-aspnetcore-integration.py verify \
  --results TestResults/AspNetCoreIntegration \
  --output artifacts/aspnetcore-integration
```

The normal solution test/coverage command intentionally excludes both dedicated integration suites:

```bash
dotnet test TCJ.slnx -c Release --no-build \
  --filter "Category!=SqlServer&Category!=AspNetCore"
```

## Filtering categories

The suite uses the repository's xUnit trait convention. Important categories include:

```text
Integration
AspNetCore
Startup
DependencyInjection
ExceptionHandling
ProblemDetails
CurrentUser
RequestScope
Middleware
Cancellation
```

For example, run only current-user scenarios with the test platform's trait filter, or keep the repository-level `Category=AspNetCore` filter to execute the complete end-to-end gate.

## Diagnostics and secret redaction

Generated diagnostics are written under:

```text
TestResults/AspNetCoreIntegration/diagnostics/
```

They contain sanitized host log events, HTTP method/path/status/body summaries, environment information, and the runtime summary needed to investigate failures. They deliberately omit request headers and unrelated environment variables.

The verifier scans generated text artifacts for:

- authorization header values;
- bearer tokens;
- authentication cookies;
- password-like values;
- raw deterministic test credentials.

A detected leak fails the gate and the generated text is redacted before artifact upload.

Verifier output is written to:

```text
artifacts/aspnetcore-integration/ASPNETCORE_INTEGRATION_SUMMARY.md
artifacts/aspnetcore-integration/aspnetcore-integration-summary.json
artifacts/aspnetcore-integration/logs/
```

These generated paths are ignored by Git.

## CI and release behavior

`.github/workflows/aspnetcore-integration.yml` runs the same integration project on `ubuntu-latest` and `windows-latest`. Each platform validates policy, restores and builds the integration project, runs `Category=AspNetCore`, verifies the TRX and sanitized diagnostics, publishes a platform summary, and uploads artifacts.

A final cross-platform job downloads both platform summaries and rejects the run unless Linux and Windows both passed for the same source commit.

Normal CI runs `validate-config` but leaves the full suite to the dedicated workflow. Release preflight and the official tagged release call the reusable ASP.NET Core integration workflow and cannot continue when the cross-platform gate fails.

## Adding a new test endpoint

Keep new endpoints inside `TCJ.AspNetCore.IntegrationTests`. Add only the smallest endpoint required to expose the behavior under test, assign a stable display name when diagnostics benefit from it, and avoid test-only behavior in `src/`.

When a new behavior is critical to the gate, add or update its policy category and verifier expectation. Do not weaken minimum counts, platform requirements, diagnostics checks, or secret scanning merely to make a failing workflow green.

## Why unit tests are not enough

An isolated unit test can prove that a service or mapper behaves correctly when called directly. It cannot prove that application startup succeeds, middleware appears in the correct pipeline, authentication creates the expected principal, a scoped service is isolated per request, the registered exception handler has logging available, or a client actually receives the expected status code and Problem Details content type.

The end-to-end suite exists to validate those composed ASP.NET Core behaviors through HTTP while remaining deterministic and hermetic.
