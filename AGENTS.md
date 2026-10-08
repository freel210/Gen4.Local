# AGENTS.md

## Aspire lifecycle

**Teardown is part of the task. Every run leaves no AppHost running.**

A task that started Aspire is not finished until `aspire stop` has run. Stop the AppHost before
reporting back to the user, including when verification failed partway.

```
aspire start --apphost .\Gen4.Local\Gen4.Local.csproj --non-interactive
# ... verify ...
aspire stop
```

- **Verify through the Aspire MCP tools** — `aspire_list_resources` for state and
  `aspire_list_console_logs` for a failing resource. `AddExecutable` child-process stdout never
  reaches `logs/aspire.out.log`, so that file cannot tell you whether a service came up.
- **Expect a 120 s start timeout.** The default `ASPIRE_CLI_START_TIMEOUT` expires while the AppHost
  is still building, because building it builds the whole referenced dependency graph. Either build
  first and pass `--no-build`, or set `ASPIRE_CLI_START_TIMEOUT=300`.
- **Docker Desktop must already be running**; `aspire start` fails without its named pipe.

## Code style

**Code is self-documenting — it is written without comments.**

- No `//` and no `///` summaries in hand-written source. Names, types and structure carry the
  meaning. Where a comment looks unavoidable, raise it and get a case-by-case decision before
  writing it. Files that a generator owns (EF migration and designer output) are left as generated.
- **The build is warning-free, and that is a completion criterion, not an aspiration.** A build that
  emits even one warning is not finished. NuGet audit findings (`NU1902`, `NU1903`) count: they are
  security reports, not formatting noise, so either upgrade the dependency or get an explicit
  decision to suppress them. Do not silence a finding by suppressing an analyzer or a warning code
  to make a build pass — that is a decision about the rule, not about the code.
- **Async methods carry no suffix** — `ImportOne`, not `ImportOneAsync`. Framework members keep
  theirs (`SaveChangesAsync`, `FirstOrDefaultAsync`); that is their contract, not a choice here.
- **Named, reused or tool-wide values live in `Defines`**, grouped into the structs already there
  (`Patients`, `Lyra3Import`, `Nats`). A constant declared inside a command is a value the next
  command will need too. Field names of a foreign document are not constants and stay where the
  mapping reads them.

## Repo conventions worth knowing

- **Lyra3 services launch via `AddExecutable`, not `AddProject`.** Every Lyra3 repo names its project
  file `Src.csproj`, and Aspire derives the `Projects.*` metadata type from the file name, so
  `Projects.Src` resolves to exactly one of them (currently Core). A fourth Lyra3 service cannot be
  reached through `AddProject`.
- **A slow or failing restore here is usually a feed, not a change.** `devrepos.mvslyra.com` hosts
  `MVS.Lyra3.HP` and its `nuget-org-proxy` repository is often offline (503), which stalls every
  restore. Read the error code before assuming a dependency is missing.
- **The Gen4 packages live in per-project feeds, not in the group feed.** On `mvslabs.gitlab.yandexcloud.net`
  they are project **4** = `Gen4.HP.Core.*`, project **5** = `gen4.hp.lib`, project **13** =
  `Gen4.HP.HIS.*`; `groups/4` does not serve them. They are NuGet V2 feeds, so a credential only sees
  the packages it may download. A `NU1101` for `Gen4.HP.Core.*` or `Gen4.HP.HIS.*` therefore means the
  credential lacks `read_package_registry` on that project, not that the package is unpublished —
  check by downloading one `.nupkg` from the project's `download` resource before raising it.
