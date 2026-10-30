# PowerShell console and "Show PowerShell"

Two features share this document:

- **Show PowerShell:** the PowerShell that does what a change does. It is offered on each pending
  change, on the whole tray as one script, and on Change Log entries.
- **The console:** a tab with this computer's own PowerShell 7, already signed in to the tenant
  with Tenantry's sign-in. The sidebar's PowerShell button opens it, and Open in Console puts a
  shown script in its box.

## Decisions

| Area | Decision |
|---|---|
| Engine | The computer's installed `pwsh` (7.x), started in the background. Nothing is bundled; a missing module gets an Install Modules button (PowerShell Gallery, current user). |
| Sign-in | Tenantry's own tokens, handed to `pwsh` over stdin (never on the command line): Graph, Exchange, Purview (its second sign-in, silent only unless Sign In is clicked) and the SharePoint admin site. |
| Test mode | Doesn't apply: the console can't judge an arbitrary script. A banner says commands go straight to the tenant. |
| Logging | Every command is logged in the Change Log (`PowerShellCommand`): text, who, when, and whether it wrote errors. Output isn't kept. |
| Rendering | Graph changes as `Invoke-MgGraphRequest` (exactly the request Tenantry sends; one small module); Exchange and Purview changes as the same cmdlets and parameters; SharePoint changes as PnP.PowerShell. Read-backs aren't shown. |

## Show PowerShell

`IChangeScripts.For(ChangeOp)` returns a `PsScript`: the text, the services it needs and an
optional note (a password placeholder, "can't be undone"). `GraphChangeScripts` implements it
on the executor's partial class (`GraphChangeExecutor.ScriptFor`, one `*Script` method per area
in `GraphChangeScripts.*.cs`).

- **Same builders.** Wherever an executor built a body or cmdlet parameters inline, that moved
  into a static builder that both the executor and the script call, so they can't drift.
- **Quoting is in one place (`Ps`).** Values are single-quoted, with typographic quotes doubled
  too. Graph bodies are single-quoted here-strings. Cmdlet calls longer than a line go one
  parameter per line.
- **References to earlier steps** (a new user's ID) are made with `Ps.Ref("user", "id")`, a
  private-use marker that becomes `$($user.id)` in a double-quoted string. Every other `$` is
  escaped. Text from a person (a department called `$(…)`) therefore stays text and can never
  run as code. A forged marker only ever becomes a read of a variable path.
- **Secrets never appear:** passwords are `<new password>` with a note; new client secrets and
  Temporary Access Pass codes are read from the answer (`$secret.secretText`).
- `ChangeScripts.Batch` joins several changes in apply order, each under a numbered comment.
  Changes that are Tenantry's own records (Test mode, a secret viewed, a console command) say
  they have no PowerShell.
- `PowerShellScriptTests` builds a sample of every change type in every enum value. Each must
  have a script (or be on the short no-script list), and all of them must parse in real
  PowerShell 7 (skipped where `pwsh` isn't installed; GitHub's runners have it).

## The console

`PwshSession` (project `Tenantry.PowerShell`) runs `pwsh -NoProfile -NonInteractive -Command
<Host.ps1>` and talks to it one JSON line at a time:

| Request | Reply |
|---|---|
| (start) | `hello` with the version |
| `connect` with tokens and services | `connection` per service (Connecting, Connected, ModuleMissing, NeedsSignIn, Failed), then `connected` |
| `run` with the script | `line` per line (Output, Error, Warning, Information, Verbose), then `done` with the error count |
| `complete` with text and cursor | `completion` (TabExpansion2) |
| `install` | `line`s, then `installed` |

Replies start with `\u0001`. Anything else on stdout is a command writing straight to the console
and is shown as output.

- **Commands run in the global scope**, so variables last until a restart. Objects go through one
  steppable `Out-String -Stream`, so a long table keeps one header. Errors are shown as
  `Command: message`, because PowerShell's own view would point at Host.ps1.
- **One request at a time.** Stopping a command ends the process; a fresh one starts and
  reconnects. Variables are lost, which the Stop button's tip says.
- **Tokens last about an hour.** A command run 45 minutes after connecting reconnects first.
- **Connect order is Graph, Exchange, Purview, PnP.** The Graph step also sends one request
  straight away. Exchange and PnP carry an older `Microsoft.Graph.Core`; if theirs loads first,
  every Graph request fails with "The pipeline has been stopped" (a `TypeLoadException`
  underneath; seen live).
- **The page** (`PowerShellConsoleViewModel`):
  - a chip per service, with Install Modules or Sign In beside the ones that need it;
  - the banner;
  - one block per command, with its output by colour, the time it took and the error count,
    plus Edit Again and Copy Output;
  - output beyond 5,000 lines is cut, with a hint to send it to a file;
  - the box: Enter runs, Shift+Enter adds a line, Tab and Shift+Tab cycle completions, Up and
    Down bring back earlier commands.

  Output arrives on PowerShell's reader thread and is posted to the window in batches.
- The demo has `DemoPowerShellSession`. It looks connected, runs nothing, and answers a changing
  command with an error so both colours show.

## Live facts (checked 2026-10-30)

- `Connect-MgGraph -AccessToken`, `Connect-ExchangeOnline -AccessToken -Organization`,
  `Connect-IPPSSession -AccessToken` and `Connect-PnPOnline -AccessToken` all accept Tenantry's
  tokens. Purview takes its search-only token.
- Connecting all four takes about 11 s.
- Show PowerShell's own scripts, run in the console on test users, did what the pipeline does.
  A Graph profile edit and an Exchange address-list change were each applied, read back and
  undone.
