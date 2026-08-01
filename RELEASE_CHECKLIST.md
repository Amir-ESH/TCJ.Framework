# TCJ Framework release checklist

This checklist prepares `0.1.0-preview.1`. Do not create the tag until every required item is complete.

## Repository

- [ ] The release pull request from `develop` to `main` is squash-merged.
- [ ] `main` contains `eng/release-manifest.json` and the release workflows.
- [ ] `Build, test and pack` is successful on the exact `main` commit.
- [ ] No uncommitted local changes exist.

## GitHub configuration

- [ ] The `nuget-production` environment exists.
- [ ] `Amir-ESH` is a required environment reviewer.
- [ ] **Prevent self-review** is disabled while there is only one maintainer.
- [ ] Environment variable `NUGET_USER` contains the NuGet.org profile username.
- [ ] `NuGet/login@v1` is allowed in Actions permissions.

## NuGet.org configuration

- [ ] Two-factor authentication is enabled for the publishing account.
- [ ] A Trusted Publishing policy exists for `Amir-ESH/TCJ.Framework`.
- [ ] The policy workflow filename is `release.yml`.
- [ ] The policy environment is `nuget-production`.
- [ ] No long-lived NuGet API key is stored in the repository or workflow.

## Release candidate

- [ ] Run **Actions → Release preflight → Run workflow** from `main`.
- [ ] Use package-ID policy `available` for the first publication.
- [ ] `Validate release candidate` succeeds.
- [ ] Download the `release-candidate-*` artifact.
- [ ] Confirm it contains five `.nupkg`, five `.snupkg`, release notes, and the manifest.
- [ ] Review package metadata and README rendering.

## Publish

- [ ] Create annotated tag `v0.1.0-preview.1` on the verified `main` commit.
- [ ] Push only that tag.
- [ ] Approve the `nuget-production` deployment after the validation job succeeds.
- [ ] Confirm all five packages appear under NuGet.org **Manage Packages**.
- [ ] Confirm the GitHub release is marked as a pre-release and contains ten package assets.
- [ ] Confirm Source Link and symbols work from at least one package.

## After publication

- [ ] Never move or recreate the published tag.
- [ ] Never republish different bits with version `0.1.0-preview.1`.
- [ ] Open the next development version as `0.1.0-preview.2` before additional publishable changes.
- [ ] Update badges and public documentation only after NuGet.org indexing completes.
