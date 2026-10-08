## Summary

<!-- What does this change and why? Link the issue it addresses. -->

## Checklist

- [ ] `dotnet build SboxNetworkStorage.sln` and `dotnet test` pass locally
- [ ] Tests added or updated for behaviour changes
- [ ] PostgreSQL behaviour covered (tests run with `NS_TEST_POSTGRES`) if storage code changed
- [ ] `CHANGELOG.md` updated under Unreleased
- [ ] `docs/` updated for new or changed config keys, CLI commands or defaults
- [ ] No change to request or response shapes used by the s&box client library, or the client impact is described below
- [ ] No secrets, real connection strings or database files committed

## Client impact

<!-- None, or describe what changes for games using the s&box client library. -->
