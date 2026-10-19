## Summary

<!-- What does this change and why? Link the issue it addresses. -->

## Client-visible surface

Classify per [docs/compatibility.md](../docs/compatibility.md#when-sbox-evidence-is-required). Pick one:

- [ ] **None**: request/response bytes, headers, status/error codes and defaults seen by the s&box library are identical for every request it can send
- [ ] **Additive, off by default**: new behavior appears only when an owner enables it; the default-off path is identical
- [ ] **Changed**: something the library already sends or reads behaves differently
- [ ] **Library code** (library repo `Code/` or `Editor/`)

<!-- Evidence for None / Additive: parity diff vs stable-reference (no unrecorded differences), SQLite + PostgreSQL green, scripts/smoke.sh clean, default-off regression test, additive migration tag + legacy-request replay. If you cannot attach it, the class is not None. -->

**s&box evidence:** <!-- "Not required (None / Additive default-off), because ..." or "Changed / Library code: Compat engine legs green on <commit> (run URL)". A native editor run is optional; add it as a `tested` row if you do it. -->

## Checklist

- [ ] `dotnet build SboxNetworkStorage.sln` and `dotnet test` pass locally
- [ ] Tests added or updated for behaviour changes
- [ ] PostgreSQL behaviour covered (tests run with `NS_TEST_POSTGRES`) if storage code changed
- [ ] `CHANGELOG.md` updated under Unreleased
- [ ] `docs/` updated for new or changed config keys, CLI commands or defaults
- [ ] For `Changed` or `Library code`: the Compat engine legs are green on this commit (`Editor evidence` passes for `Code/` or `Editor/` changes). No native editor run is required
- [ ] No secrets, real connection strings or database files committed

## Client impact

<!-- None, or describe what changes for games using the s&box client library. -->
