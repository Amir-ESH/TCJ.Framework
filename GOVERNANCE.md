# TCJ Framework Governance

This document defines governance for the **official TCJ Framework project**. It separates the freedoms granted to users and fork authors by the software license from authority over the official repository, releases, package identities, licensing decisions, and project brand.

## Official project

For governance purposes, the **Official TCJ Project** consists of:

- the upstream repository `Amir-ESH/TCJ.Framework`;
- protected upstream branches, including `develop` and release/default branches;
- official GitHub releases and release artifacts;
- official `TCJ.*` NuGet packages published by or under authority of the Project Owners;
- official TCJ documentation and project-controlled distribution channels; and
- the TCJ Framework project identity governed by [`TRADEMARKS.md`](TRADEMARKS.md).

A fork is an independent project unless the Project Owners explicitly designate it as an official TCJ repository or distribution channel.

## Roles

### Project Owners

Project Owners hold final governance authority over the Official TCJ Project.

The current Project Owner is:

- [`@Amir-ESH`](https://github.com/Amir-ESH)

Only an existing Project Owner may appoint or remove another Project Owner. Changes to the owner list must be made through the protected upstream repository and approved by an existing Project Owner.

Project Owners have final authority over:

- accepting and merging changes into protected official branches;
- publishing official releases and NuGet packages;
- release signing, provenance, and package-publishing credentials;
- the project's roadmap and supported compatibility contracts;
- appointment or removal of maintainers and other privileged roles;
- the outbound software license and any relicensing or dual-licensing decision;
- [`CLA.md`](CLA.md), this governance document, [`TRADEMARKS.md`](TRADEMARKS.md), and the official `CODEOWNERS` policy;
- the TCJ name, logos, official package identity, and other project-controlled marks; and
- final resolution of governance disputes in the Official TCJ Project.

A Project Owner may delegate operational work without transferring final ownership or governance authority unless the delegation explicitly appoints another Project Owner under this document.

### Maintainers and reviewers

Project Owners may designate maintainers or reviewers to help triage issues, review pull requests, maintain subsystems, or operate automation.

A maintainer or reviewer role does **not** by itself grant ownership of the Official TCJ Project, authority to change its license, or rights to TCJ trademarks. Unless a person is also a Project Owner, owner-reserved decisions remain subject to Project Owner approval.

### Contributors

Anyone may report issues or propose changes through pull requests, subject to [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`CLA.md`](CLA.md).

A contribution, including an accepted contribution, does not automatically grant repository administration, merge rights, release authority, project ownership, trademark rights, or a governance vote. Contributors retain copyright in their original Contributions while granting the rights described in the CLA.

## Official changes and owner approval

Changes to the Official TCJ Project are accepted through owner-controlled upstream branches.

Contributors may **propose** any technically or legally appropriate change, but a proposal becomes an official TCJ change only after the repository's required checks pass and a Project Owner approves/merges it according to repository protection rules.

The following are **owner-reserved changes** and require explicit Project Owner approval:

- `LICENSE.txt` or the outbound package-license expression;
- `CLA.md`;
- `GOVERNANCE.md`;
- `TRADEMARKS.md`;
- `.github/CODEOWNERS`;
- official package IDs, signing/publishing identity, or release ownership;
- rules that grant or remove Project Owner authority; and
- any relicensing, dual-licensing, or transfer of official project stewardship.

No pull request, issue comment, contribution volume, or fork creates a right to force acceptance of a change into the Official TCJ Project.

## Relicensing authority

The current outbound license for the development line is **GNU LGPL v3.0 only (`LGPL-3.0-only`)**.

Only Project Owners may approve a future license change for the Official TCJ Project. Any such change must respect copyrights and permissions already granted for the material being relicensed.

Contributions accepted under [`CLA.md`](CLA.md) grant the Project Owners broad copyright rights, including sublicensing/relicensing rights, so accepted Contributions can continue to be used if the official project's licensing model changes later.

A later license change does not retroactively cancel permissions already granted with copies distributed under an earlier license.

## Forks and independent commercial development

Forking TCJ Framework is permitted under the applicable software license.

Subject to the LGPL, third-party dependency licenses, and [`TRADEMARKS.md`](TRADEMARKS.md), a fork author may:

- maintain and develop the fork independently;
- add or remove features;
- rename the independent fork and use separate package identities;
- offer consulting, hosting, support, or other paid services around the fork;
- charge money for distributing copies; and
- build a separate community or commercial product around the fork.

A fork author owns their **original modifications and fork-specific branding** to the extent provided by applicable law. They do not acquire ownership or control of the Official TCJ Project, its upstream repository, official release channels, or TCJ-controlled marks merely by forking or modifying the code.

An independent fork must not claim to be an official TCJ release, imply Project Owner endorsement, or use TCJ marks in a confusing manner. Factual attribution such as **"Based on TCJ Framework"** is permitted as described in [`TRADEMARKS.md`](TRADEMARKS.md).

Charging money does not remove or replace the LGPL obligations that apply when covered TCJ code is conveyed.

## Repository enforcement

The repository uses [`.github/CODEOWNERS`](.github/CODEOWNERS) to identify owner-controlled paths and the Project Owner responsible for review.

For this policy to be mechanically enforced, the protected official branches should use GitHub branch protection or rulesets that, at minimum:

1. require a pull request before merging;
2. require Code Owner approval for changes submitted by non-owners;
3. require the repository's blocking status checks;
4. dismiss stale approvals or require approval of the most recent reviewable push;
5. prevent force-pushes and branch deletion; and
6. restrict direct pushes and bypass privileges to Project Owners where the repository plan and ownership model support that setting.

GitHub does not allow a pull-request author to approve their own pull request. If the Official TCJ Project has only one Project Owner, a rule that unconditionally requires Code Owner approval can therefore block that owner's own pull requests. In that configuration, any administrator/ruleset bypass needed for an owner-authored pull request must remain reserved to Project Owners and should be used only after the required status checks pass.

`CODEOWNERS` identifies the responsible owner and routes review requests; GitHub branch/ruleset settings are what make owner review or owner-only bypass rules enforceable.

## Security and emergency changes

Security-sensitive changes may be prepared privately when public disclosure would create material risk. Publication and merge authority for the Official TCJ Project remains with Project Owners, and the process must continue to respect the project's security, licensing, and release-integrity requirements.

## Governance changes

This governance policy may be changed only through the Official TCJ Project with explicit approval from a Project Owner.

Substantial changes to ownership, relicensing authority, or trademark stewardship should be documented in the pull request or release notes so the history remains auditable.
