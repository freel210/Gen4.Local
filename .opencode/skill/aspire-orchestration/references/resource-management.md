# Resource Management

Use this when the task is scoped to one resource or depends on a specific resource becoming healthy.

## Wait For One Resource Before Touching It

```bash
aspire wait <resource>
aspire wait <resource> --status up --timeout 60
```

- Use `aspire wait` before a dependent action when readiness is the blocker.
- Add `--status` and `--timeout` for explicit readiness conditions.
- Treat readiness as resource-scoped — a missing ready signal is not a reason to restart the whole AppHost.
- Use `displayName` from `aspire ps --format Json`, not `name` ([#15842](https://github.com/microsoft/aspire/issues/15842)).
- `--status` accepts `healthy`, `up`, and `down`. Default is `healthy`; `--timeout` defaults to 120s.
- `aspire wait` **returns the moment the state is reached** — that is the point of it. A wait that returns in
  seconds is working correctly, not suspiciously fast.
- **Never replace it with a fixed sleep.** Chain one `aspire wait` per resource you actually depend on,
  each with its own `--timeout`, rather than one blanket delay sized to cover everything.

For a one-shot resource (a migration job, a smoke test) that exits on its own, `--status up` is the right
target: it fires as soon as the process starts, and you then read the result while the output is still there.

## Read The Result, Then Tear Down Early

Once a resource is ready, read the one thing you need and stop. Do not wait out the rest of a scenario.

```bash
aspire wait smoke-test --status up --timeout 300
aspire logs smoke-test --search "response body"   # returns immediately
aspire stop
```

| Command | Blocks? | Agent verdict |
|---------|---------|---------------|
| `aspire logs <res> --search "<pattern>"` | No — one non-blocking round trip | ✅ Use this |
| `aspire logs <res> --follow --search "<p>"` | Yes — streams until interrupted | ❌ Burns the tool timeout, then needs a kill |
| `aspire describe <res> --format Json` | No | ✅ Use for state, health reports, available commands |
| `aspire_execute_resource_command` on a long-running resource | Yes — waits for exit | ❌ Same problem as `--follow` |

`--follow` is correct for a human watching a terminal and wrong for an agent: the call never returns.
Without `--follow`, the same `--search` filter returns the matching lines immediately.

Read the result **while the resource is still alive**. A one-shot resource that has already exited can
report `No logs found` for `--search`, because its console buffer is no longer retained.

MCP equivalents when the Aspire MCP server is connected — all one-shot: `list_console_logs` (with a
full-text `search`), `list_resources`, `describe`, `list_traces`, `list_structured_logs`.

## Fail Fast: Stop At The First Error

When something breaks, do not sit out a timeout waiting for more output.

1. `aspire describe --format Json` — read `state`, `exit_code`, and each `health_reports` entry.
2. `aspire logs <failing-resource> --search "<error term>"` — pull the relevant lines.
3. `aspire stop` — release the ports and file locks, then debug with a clean tree.

`health_reports` in `aspire describe` carries the exception message for failing health checks, which often
identifies the cause without opening the logs at all.

## Fix Or Operate On One Resource Without Bouncing The Whole App

```bash
aspire resource <resource> start
aspire resource <resource> stop
aspire resource <resource> <command>
```

- Prefer resource-scoped commands when the task doesn't require an AppHost-wide restart.
- If one resource is wedged, use resource-scoped commands such as `stop`, `start`, or `rebuild` when the resource exposes them before escalating to a full AppHost restart.
- Use `aspire resource <resource> <command>` when the AppHost exposes resource-specific dashboard or operational commands.
- If the resource's own framework watch/HMR/debug workflow is already handling the change, do not force an Aspire resource command.

## What Changed Determines the Action

| What Changed | Action | Command |
|--------------|--------|---------|
| AppHost project (Program.cs, .csproj) | Full restart | `aspire stop` → edit → `aspire start` |
| .NET service project (.cs files) | Rebuild/refresh resource if exposed | `aspire resource <name> rebuild` or the resource's IDE/watch workflow |
| JavaScript/Python/Go files | Usually no Aspire action | File watchers/HMR handle it automatically |
| Configuration (appsettings.json) | Check first | `aspire describe` then decide |
| TypeScript AppHost deps | Restore | `aspire restore` |
