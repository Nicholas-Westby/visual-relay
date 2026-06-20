# Shrink `visual-relay` to a bootstrap; move every command into `VisualRelay.Cli`

`visual-relay` is a 600-line bash dispatcher carrying real logic: tool-prerequisite gates,
a timeout watchdog, a weekly swival upgrade check, nono provisioning, and a per-command
`case`. Move all of it into a new C# **`VisualRelay.Cli`**, and reduce `visual-relay` to the
**irreducible pre-dotnet bootstrap** that enters the toolchain and `exec`s the CLI. Fold
`test.sh`'s logic into the CLI's `test` command in the same pass.

This design is decided — implement exactly this, no alternatives:

- **New `tools/VisualRelay.Cli`** owns every subcommand: `build`, `test`, `format`,
  `screenshot`, `run-task`, `init`, `check`, `inspect`, `gen-backend-config`, `guards`,
  `install-hooks`, and the post-dotnet half of `launch`. Logic lives in C# (tested);
  the CLI may shell out (`dotnet`, `nono`, `swival`, the existing tool projects) but holds
  no big bash blocks.
- **`visual-relay` keeps only the bootstrap:** resolve its own dir, enter `nix develop`
  once, detect/exec published brew binaries, the consent-gated Determinate-Nix install
  offer, then `exec` the CLI. Target **≤ 20 logic lines** (it must pass `./visual-relay
  guards`).
- **`test.sh` → thin wrapper** over `./visual-relay test`; its log-dir + TRX failing-test
  logic moves into a tested C# `TestRunner`.

> **Sequencing — task 2 of 6 (12 → 17).** Lands after the advisory guard (12). The bootstrap
> stays shell *by design* (it runs before dotnet exists — see the design doc's "Why a couple
> of scripts stay shell"); the goal here is to make it lean, not to eliminate it.

## Current state (researched)

- **The dispatcher** is one bash `case` (`visual-relay:468-580`, usage at `:577`) wrapped in
  a `main()` (`:3,600`). Bootstrap helpers, all pre-dotnet and **staying in bash**:
  `_find_nix` (`:48-59`), `_ensure_devshell` (`:62-81`), `_offer_nix_install` (`:84-119`).
  Helpers whose work **moves to C#**: `_missing_required_tools` (`:153-191`), `_require_nono`
  (`:212-234`), `_require_swival` / `_offer_swival_install` (`:245-311`), `_swival_upgrade_check`
  (`:318-373`), `_provision_nono` (`:375-402`, but see task 11 below), `_timeout_watchdog`
  (`:405-463`), `_read_bypass_sandbox` (`:200-210`), `_require_dotnet` (`:122-144`).
- **Brew/published fast paths** (`visual-relay:31-45,490-497,529-531,566-568`): when the
  published `App`/`Init`/`GenBackendConfig` binaries are present (`HAS_PUBLISHED`), `launch`/
  `init`/`gen-backend-config` `exec` them directly. Brew users only ever run `launch`/`init`
  (`AGENTS.md:88-89`), so `VisualRelay.Cli` does **not** need to be published — the bootstrap
  execs the published app for `launch`, and falls back to `dotnet run --project …Cli` for the
  dev commands (a source checkout always has dotnet/nix).
- **`check` flow to preserve** (`visual-relay:543-564`): source-enum guard → file-size guard →
  `dotnet format --verify-no-changes` → build → inspect-code → watchdog'd test → screenshots.
- **`test.sh`** computes a host/VM-safe log stem (`test.sh:17-25`), passes `NO_BUILD`/filter
  args (`:28-34`), runs `dotnet test` with console+trx loggers (`:41-45`), and on failure
  greps the TRX for failed `testName`s (`:54-68`).
- **C# seams to reuse:** process orchestration has existing runners (see
  `ProcessCaptureConcurrencyTests`, `ProcessCaptureEnvStripTests`); the watchdog has a C#
  precedent in `SwivalSubagentRunnerWatchdogTests` (process-group kill, CPU pulse) — adapt it
  rather than re-porting the bash. Git goes through `GitInvoker`.
- **Characterization tests are the safety net:** `Installer5LauncherTests(.CwdSandbox)`,
  `Installer5Bootstrap2/3LauncherTests`, `Installer5Bootstrap4SwivalInstallTests`,
  `Installer5Bootstrap5SwivalUpgradeTests`, `Installer5Sandbox2LauncherTests` drive the real
  launcher with stub `nix`/`nono`/`swival`. Bootstrap-behavior tests stay (against the thin
  bash); per-command/watchdog/upgrade-check tests move to C#.

## What to build

TDD. Land in ordered, commit-sized steps:

1. **Scaffold `tools/VisualRelay.Cli`** (mirror an existing tool csproj; ref `Core` (+`Domain`
   if needed); register in `VisualRelay.slnx`). Top-level arg dispatch `cmd → handler`,
   usage to stderr, numeric exit codes — mirror `tools/VisualRelay.RunTask/Program.cs`.
2. **Port the thin commands** (`build`, `format`, `screenshot`, `run-task`, `init`,
   `gen-backend-config`, `inspect`, `install-hooks`): each just orchestrates `dotnet`/an
   existing tool project/a guard. Invoke the existing tools directly; do not reimplement them.
   `install-hooks` = `git config core.hooksPath .githooks` + `chmod` the hooks (drop the
   `check-file-size.sh` chmod — task 15 retires it).
3. **Port the watchdog** into C# (adapt the `SwivalSubagentRunner` watchdog): run a child
   process, kill the tree on timeout, return 124. Used by `test` and `check`. Honor
   `VISUAL_RELAY_TEST_TIMEOUT` / `VISUAL_RELAY_CHECK_TEST_TIMEOUT`.
4. **Port `test.sh` → a tested `TestRunner`**: host/VM-safe timestamped log dir
   (`VR_TEST_LOG_DIR` default `./test-logs`), `NO_BUILD`/filter args, console+trx loggers,
   TRX failed-test extraction. Unit-test the TRX parser and log-stem builder. Wire `test` to
   it. Replace `test.sh` with a thin wrapper: `exec "$SCRIPT_DIR/visual-relay" test "$@"`.
5. **Port the swival upgrade check** into C# (XDG state stamp, 7-day window, consent prompt,
   non-fatal). **Preserve the override seams** the tests use (`VISUAL_RELAY_SWIVAL_LATEST_CMD`,
   `…_UPGRADER`, `…_INSTALLER`) so `Installer5Bootstrap5SwivalUpgradeTests` can re-point.
6. **Port `check`** to C# preserving the `:543-564` order (call the existing guard scripts
   for now; task 15 swaps in the C# guards).
7. **Shrink `visual-relay`** to the bootstrap: keep `_find_nix`/`_ensure_devshell`/
   `_offer_nix_install` + `HAS_PUBLISHED` detection + published-exec, then
   `exec dotnet run --project "$SCRIPT_DIR/tools/VisualRelay.Cli/VisualRelay.Cli.csproj" -- "$@"`
   (preferring a published Cli if one exists). Verify it is **≤ 20 logic lines** via
   `./visual-relay guards`.
8. **Migrate tests:** keep the bootstrap launcher tests (nix re-entry, install offers,
   published exec) against the thin bash; add C# tests for the moved watchdog/upgrade-check/
   test-runner/command handlers; keep every `Installer5*` test green or re-pointed.

## Done when

- Every former subcommand works through `./visual-relay <cmd>` with parity to today
  (`build`/`test`/`format`/`screenshot`/`run-task`/`init`/`check`/`inspect`/
  `gen-backend-config`/`guards`/`install-hooks`/`launch`), backed by `VisualRelay.Cli`.
- `visual-relay` contains only the pre-dotnet bootstrap and **passes `./visual-relay guards`**
  (≤ 20 logic lines); `test.sh` is a thin wrapper with no TRX/log logic.
- Watchdog, swival upgrade-check, and the test runner are C# with unit/integration tests that
  fail against the (absent) C# code; the upgrade-check override env seams still work.
- All `Installer5*` launcher tests are green (or re-pointed to the C# command they now cover).
- `./visual-relay check` is green; changed C# files < 300 lines; Conventional Commit subject
  e.g. `refactor(cli): move visual-relay commands into VisualRelay.Cli; thin the launcher`.
- Coordination (implementer sees one task at a time):
  - **Task 14 (backend):** `launch` starts the backend by calling the existing
    `tools/backend/backend.sh start` for now; task 14 repoints it to `VisualRelay.Backend`.
  - **Task 11 (nono profile self-heal):** it moves profile-ensuring into C# at run start and
    strips `_provision_nono`'s copy block. Drop `_provision_nono` from the bootstrap and rely
    on task 11's C# ensurer (move any still-needed `nono pull jedisct1/swival` into the Cli);
    whichever of 11/13 lands second adapts.
  - **Task 15 (guards):** `check`/`inspect` call the existing guard scripts here; task 15
    swaps in the C# guards.
