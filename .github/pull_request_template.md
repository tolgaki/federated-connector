## Summary

<!-- What does this change and why? Link the issue: "Fixes #123". -->

## Type of change

- [ ] Bug fix
- [ ] New provider
- [ ] Provider contract (Abstractions) change — discussed in an issue first
- [ ] Host / tool surface change
- [ ] Registration scripts / infrastructure
- [ ] Documentation

## Checklist

- [ ] `dotnet build` is clean (warnings are errors) and `dotnet test` passes
- [ ] Tests added or updated; providers pass the conformance kit, including the two-user permission test
- [ ] Tools stay read-only and every user-scoped source call carries the user's identity
- [ ] No secrets, tenant or subscription ids, internal hostnames, personal paths or third-party product names
- [ ] Shell scripts pass `bash -n`; PowerShell parses; Bicep builds (if changed)
- [ ] README, design doc and CHANGELOG (*Unreleased*) updated where behaviour changed

## Notes for reviewers

<!-- Anything that needs a closer look, open questions, or follow-ups. -->
