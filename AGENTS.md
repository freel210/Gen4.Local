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

## Repo conventions worth knowing

- **Lyra3 services launch via `AddExecutable`, not `AddProject`.** Every Lyra3 repo names its project
  file `Src.csproj`, and Aspire derives the `Projects.*` metadata type from the file name, so
  `Projects.Src` resolves to exactly one of them (currently Core). A fourth Lyra3 service cannot be
  reached through `AddProject`.
- **NuGet restore succeeds offline.** The private feed `devrepos.mvslyra.com` is unreachable; restore
  resolves from the global packages cache. A slow first build is not a feed failure.
- **The repos under `C:\Shared\Repos\*` are read-only.** All local-environment changes belong here.
