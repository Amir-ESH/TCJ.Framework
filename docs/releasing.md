# Release automation

TCJ Framework publishes from Git tags through `.github/workflows/release.yml`.
The workflow uses NuGet.org Trusted Publishing and GitHub OIDC. It does not require a long-lived NuGet API key.

## Release guarantees

A release is rejected unless all of the following are true:

- the tag starts with `v` and contains a valid semantic version;
- the tag version matches `eng/Packaging.props`;
- the tagged commit is reachable from `main`;
- restore, Release build, tests, and pack succeed;
- exactly five `.nupkg` and five `.snupkg` files are produced.

After validation, the protected `nuget-production` environment publishes the packages to NuGet.org and creates a GitHub Release with all package files attached.

## One-time GitHub configuration

### Allow the NuGet login action

The repository currently limits workflows to selected actions. Go to:

```text
Settings → Actions → General → Actions permissions
```

Keep GitHub-created actions enabled and add this allowed action pattern:

```text
NuGet/login@v1
```

The CI/CD workflows also use these GitHub-owned actions:

```text
actions/checkout@v6
actions/setup-dotnet@v6
actions/upload-artifact@v7
actions/download-artifact@v8
```

### Create the release environment

Go to:

```text
Settings → Environments → New environment
```

Create:

```text
nuget-production
```

Recommended protection:

- add yourself as the required reviewer;
- leave **Prevent self-review** disabled while you are the only maintainer;
- restrict deployment branches and tags to protected branches and release tags when that option is available.

Create an environment variable:

```text
Name: NUGET_USER
Value: your nuget.org profile username, not your email address
```

No NuGet API-key secret is needed.

If the **Trusted Publishing** page is not available in your NuGet.org account, do not create a release tag yet. Keep the tag-based workflow disabled until a scoped fallback publishing credential is configured deliberately; never place an API key in the repository or workflow file.

## One-time NuGet.org configuration

Sign in to NuGet.org and open:

```text
Account → Trusted Publishing → Add policy
```

Create a GitHub Actions policy with these exact values:

```text
Policy owner: your individual NuGet.org account
Repository owner: Amir-ESH
Repository: TCJ.Framework
Workflow file: release.yml
Environment: nuget-production
```

Enter only `release.yml` as the workflow filename, not the `.github/workflows/` path.

## Prepare a release

1. Update the shared version in `eng/Packaging.props`.
2. Move release notes from `Unreleased` into the matching version section in `CHANGELOG.md`.
3. Merge `develop` into `main` through a protected pull request.
4. Confirm `Build, test and pack` succeeds on `main`.
5. Create and push the annotated tag from the exact `main` commit.

Example preview release:

```bash
git switch main
git pull --ff-only

git tag -a v0.1.0-preview.1 \
  -m "TCJ Framework 0.1.0-preview.1"

git push origin v0.1.0-preview.1
```

A version containing a prerelease suffix, such as `-preview.1`, produces a GitHub pre-release automatically.

## Publication sequence

The workflow performs these operations:

1. validate the tag and version;
2. build and test the complete solution;
3. create and verify all primary and symbol packages;
4. pause for the `nuget-production` environment approval when configured;
5. exchange the GitHub OIDC token for a short-lived NuGet API key;
6. publish all packages and associated symbol packages;
7. create the GitHub Release and attach `.nupkg` and `.snupkg` files.

`--skip-duplicate` allows a safe rerun after a partial NuGet.org outage. The immutable tag guarantees that reruns use the same source commit and package version.

## Failed release

Do not move or recreate an existing public release tag with different content.

If no packages were published, delete the failed tag, correct the release commit, and create the tag again.

If any package was published, increment the version, update `eng/Packaging.props` and `CHANGELOG.md`, and publish a new tag. NuGet package versions are immutable.
