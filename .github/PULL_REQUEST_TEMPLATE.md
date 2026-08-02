## Summary

Describe the problem and the approach taken.

## Changes

- 

## Compatibility

- [ ] No public API change
- [ ] Public API added or changed
- [ ] Breaking change documented
- [ ] Package validation passes against the published API baseline
- [ ] Any `CPxxxx` suppression is minimal and explained

## Validation

- [ ] Dependency security policy passes and no package source was added without review
- [ ] `dotnet restore TCJ.slnx --force-evaluate` reports no blocking advisory
- [ ] `dotnet build TCJ.slnx -c Release`
- [ ] `dotnet test TCJ.slnx -c Release`
- [ ] Code coverage quality gate passes; new behavior has focused tests
- [ ] The recorded mutation baseline is valid and the full mutation quality gate passes; relevant survived mutants were reviewed
- [ ] `dotnet pack TCJ.slnx -c Release --no-build` when packaging or release infrastructure changes
- [ ] Release-integrity automation remains valid and package checksums pass
- [ ] Documentation updated where required
- [ ] No secrets or generated artifacts included

## Related issue

Closes #
