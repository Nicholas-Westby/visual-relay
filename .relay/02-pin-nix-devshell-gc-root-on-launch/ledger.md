## Stage 1 - Ideate

{
  "summary": "Add `--profile` to the `nix develop` invocation in `_ensure_devshell` so the devshell's closure is pinned as a GC root via a nix profile symlink, preventing garbage collection from deleting store paths while the app runs. The profile lives at `$XDG_DATA_HOME/visual-relay/nix-dev-profile` (falling back to `$HOME/.local/share`), matching the existing backend-venv data root. Old generations are pruned before each entry to avoid accumulation. A comment documents the single-profile limitation across checkouts.",
  "options": [
    "Option A — Prescribed approach (nix develop --profile + best-effort prune): Compute profile path from XDG_DATA_HOME, mkdir -p its parent, best-effort `nix profile wipe-history --profile \"$profile\" || true` before entry, and append `--profile \"$profile\"` to the `nix develop` invocation. Minimal, matches the task prescription exactly, adds ~5 lines, stays under the 100-line ceiling. Generation pruning keeps the profile directory lean. Downside: multiple checkouts on different flake.lock revisions share one profile, protecting only the most recently launched one (accepted trade-off).",
    "Option B — Temp symlink via nix-store --add-root: Instead of `--profile`, add a separate `nix-store --add-root <temp-link> --realize <derivation>` step before exec, pointing at a symlink in a tmpdir that lives as long as the process. Avoids generation accumulation entirely and doesn't require `nix profile wipe-history`. However, this is fragile (tmpdir cleanup on crash), deviates from standard nix practice, and adds complexity to the bootstrap that the 100-line ceiling discourages. Higher risk of subtle breakage.",
    "Option C — Per-checkout profile (nix develop --profile with flake-lock hash): Derive the profile path from a hash of `flake.lock` (e.g., `$(sha256sum flake.lock | head -c 16)`), giving each revision its own pinned profile. This solves the multi-checkout problem cleanly — switching branches doesn't risk GC of the previous revision's devshell. But it's gold-plating per the task constraints: it adds more lines, requires reading flake.lock (which may not exist in published/brew installs), and the task explicitly says per-checkout profiles are out of scope."
  ]
}

## Stage 2 - Research

{
  "findings": "The bootstrap script `visual-relay` (99 lines total, ~68 logic lines) has no `--profile` on its `nix develop` invocation at line 78, confirming the root cause. The 100-logic-line ceiling (`ShellSizeGuard.BootstrapLimit = 100`) leaves ~32 lines of headroom — ample for the ~5-line addition. The `BackendPaths` C# class already uses `$XDG_DATA_HOME/visual-relay` (fallback `$HOME/.local/share/visual-relay`) as the user-data root, so the prescribed profile path `${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile` is byte-for-byte consistent with existing infrastructure. The flake devshell (dotnet-sdk_10, git, bash, shfmt, icu, imagemagick, openssl, zlib, nonoPinned, uv, python313) is the closure that must be pinned. Neither `nix develop --profile` nor `nix profile wipe-history` nor any GC-root-related code exists yet in the script. The guard suite (`ShellSizeGuard.FindViolations` using `ShellScriptLineCounter.CountLogicLines`) will enforce the line ceiling after edits; `ShellFormatGuard` via `shfmt --write` enforces formatting. The published/brew and no-nix paths in `_ensure_devshell` return early before the profile logic, so the change won't affect them.",
  "constraints": [
    "Must not exceed 100 logic lines in visual-relay (currently ~68, ~32 slots free); enforced by `./visual-relay guards`",
    "Profile path must be derived from `${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile` — no hardcoded absolute paths",
    "Must `mkdir -p` the parent directory of the profile before use",
    "Append `--profile \"$profile\"` to the existing `nix develop` command (line 78); no other flow changes to published/brew or no-nix paths",
    "Best-effort `\"$nix\" profile wipe-history --profile \"$profile\" || true` before entry to bound generation growth; failure must not block launch",
    "One-line comment documenting that a single shared profile protects only the most recently launched checkout (multiple checkouts on different flake.lock revisions are out of scope)",
    "Must pass `./visual-relay format` (shfmt) after edits — the bootstrap file uses tab indentation (shfmt default)",
    "Guard suite (`./visual-relay guards`) must stay green after changes",
    "The `_ensure_devshell` function must continue to use the `$nix` binary found by `_find_nix` for both the wipe-history and develop commands — same variable, no second lookup",
    "The profile exists only when nix is present; the wipe-history call must be guarded by the profile's existence check (or be idempotent via `|| true`)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The `_ensure_devshell` function in the `visual-relay` bootstrap script (line 78) invokes `nix develop --command bash \"$0\" …` without `--profile`, so no GC root is registered for the devshell's closure. A running process is not a GC root, meaning `nix store gc` can collect `nono`, `dotnet`, `git`, `shfmt`, and every other devshell store path while the app is running — the observed 2026-07-18 incident. The 100-logic-line ceiling (`ShellSizeGuard.BootstrapLimit = 100`) leaves ~32 lines of headroom for the ~4–5 line addition. `BackendPaths.cs` already resolves to `$XDG_DATA_HOME/visual-relay` (fallback `$HOME/.local/share/visual-relay`), so the prescribed profile path `${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile` is byte-for-byte consistent with existing infrastructure. The published/brew and no-nix early-return paths in `_ensure_devshell` (lines 69, 73–76) precede the insertion point, so the change does not affect them. No `--profile`, `nix profile wipe-history`, or any GC-root code exists in the script today.",
  "excerpts": [
    "visual-relay:78: exec env -u DOTNET_ROOT … \"$nix\" develop --command bash \"$0\" \"$cmd\" …  ← no --profile, no GC root registered",
    "visual-relay:69: if [[ -n \"${VISUAL_RELAY_NIX_REENTRY:-}\" ]] || ((HAS_PUBLISHED)); then return 0; fi  ← published/brew returns before profile logic",
    "visual-relay:73-76: no-nix path returns early when $nix is empty",
    "ShellSizeGuard.cs:16: public const int BootstrapLimit = 100;  ← ~32 logic-line slots free",
    "ShellSizeGuard.cs:60-62: effectiveLimit = path == \"visual-relay\" ? BootstrapLimit : limit  ← carve-out enforcement",
    "BackendPaths.cs:27: /// Root per-machine data directory: <c>$XDG_DATA_HOME/visual-relay</c>",
    "BackendPaths.cs:64-68: var xdg = GetEnv(\"XDG_DATA_HOME\"); var home = GetEnv(\"HOME\"); return new BackendPaths(Combine(xdg, home));",
    "BackendPaths.cs:84-96: Combine falls back through XDG_DATA_HOME → $HOME/.local/share → %LOCALAPPDATA%",
    "flake.nix:46-59: devshell packages = dotnet-sdk_10, git, bash, shfmt, icu, imagemagick, openssl, zlib, nonoPinned, uv, python313  ← closure to pin",
    "ShellFormatGuard.cs:11: runs shfmt --diff; exit 0 clean, exit 1 diff, missing binary failure  ← formatting enforcement"
  ],
  "repro": "On a nix machine: (1) Run `./visual-relay launch` to start the app. (2) From another shell, run `nix store gc --dry-run` — observe devshell store paths (nono, dotnet, etc.) listed as eligible for collection, because no GC root references them. (3) A real `nix store gc` at this point deletes those paths; subsequent sandbox spawns from the running app fail with a missing `nono` binary. (4) Run `nix-store --query --roots \"$(command -v nono)\"` (from within the devshell) — no visual-relay profile is listed. After the fix, the same query should list `~/.local/share/visual-relay/nix-dev-profile`, `gc --dry-run` should not include the devshell paths, and running `./visual-relay launch` twice should not accumulate generations beyond current + one."
}

## Stage 4 - Plan

{
  "plan": "## What changes\n\nIn `visual-relay`, inside `_ensure_devshell`, insert three logic lines (plus one comment line) between the echo (L77) and the exec (L78), and append `--profile \"$profile\"` to the `nix develop` invocation on L78.\n\n### Detailed steps\n\n1. **Compute profile path** — add `local profile=\"${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile\"` after the echo. Matches the existing `BackendPaths` data root.\n\n2. **Ensure parent directory** — add `mkdir -p \"$(dirname \"$profile\")\"`. Uses POSIX `dirname` (available on the host, not just inside the devshell).\n\n3. **Bound generation growth** — add `[[ -e \"$profile\" ]] && \"$nix\" profile wipe-history --profile \"$profile\" || true`. Only runs when the profile already exists; failure never blocks launch (`|| true`). Uses the same `$nix` binary found by `_find_nix`.\n\n4. **Pin the GC root** — insert `--profile \"$profile\"` into the existing `exec env … \"$nix\" develop …` line, between `develop` and `--command`, so it becomes `\"$nix\" develop --profile \"$profile\" --command bash …`.\n\n5. **Document the trade-off** — include a one-line comment above the profile logic: `# Profile pins the devshell closure as a GC root; single profile protects only the most recently launched checkout.`\n\n6. **Reformat** — run `./visual-relay format` (shfmt).\n\n7. **Verify guards** — run `./visual-relay guards` to confirm the shell-size gate still passes (3 new logic lines, staying well under the 100-line `BootstrapLimit`).\n\n### Impact\n\n- No flow changes to published/brew (L69 early-return) or no-nix (L73-76) paths — they return before the new code.\n- The `_ensure_devshell` function gains 3 logic lines: `local profile`, `mkdir -p`, and the wipe-history line. The comment is excluded from the logic-line count. Expected total: ~71 logic lines (under 100).\n- `nix-store --query --roots $(command -v nono)` will list the profile after launch; `nix store gc --dry-run` will exclude devshell paths.\n\n### Files changed\n\n| File | Action |\n|------|--------|\n| `visual-relay` | Edit: insert 4 lines (1 comment + 3 logic) + modify 1 line (add `--profile`) |",
  "manifest": ["visual-relay"]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/NixDevShellGcRootTests.cs"],
  "rationale": "Added 9 tests covering: (1) --profile flag presence in nix develop invocation, (2) XDG_DATA_HOME-derived profile path, (3) mkdir -p for profile parent, (4) profile wipe-history call, (5) || true guard on wipe-history, (6) comment documenting single-profile trade-off, (7) 100-line ceiling check, (8) end-to-end stub-nix test confirming --profile reaches the binary, (9) stub-nix test confirming wipe-history failure doesn't block launch. All 7 content-assertion tests are red (fail on current bootstrap with no --profile), 2 are green (line count under 100, and vacuously-passing wipe-failure-doesnt-block test)."
}

## Stage 6 - Implement

{ "summary": "Added --profile to the nix develop invocation in _ensure_devshell (visual-relay lines 78-82) to pin the devshell closure as a GC root. Changes: compute profile path from XDG_DATA_HOME (matching BackendPaths data root), mkdir -p its parent, best-effort wipe-history via || true to bound generations, and --profile flag on the develop exec. Added 3 logic lines (total 71, under 100-line ceiling). Comment documents single-profile trade-off. shfmt clean, all 3306 tests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Change is entirely in a bash bootstrap script (visual-relay) — adding --profile to nix develop, best-effort wipe-history, and mkdir -p. No UI, markup, frontend, image, chart, or document output to visually review. Verified by guard suite and tests._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Implementation complete. In `visual-relay`, added `--profile \"$profile\"` to the `nix develop` invocation in `_ensure_devshell` (line 82), where `profile` is derived from `${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile` (line 79). The parent directory is created with `mkdir -p` (line 80), old generations are pruned best-effort via `[[ -e \"$profile\" ]] && \"$nix\" profile wipe-history --profile \"$profile\" || true` (line 81), and a comment documents the single-profile trade-off across checkouts (line 78). All 3306 tests pass (0 failed, 115 skipped), including the 9 new `NixDevShellGcRootTests` and the 100-logic-line guard check. The guard suite passes.",
  "commitMessages": [
    "feat(bootstrap): pin nix devshell as GC root via --profile",
    "fix: prevent nix GC from stranding running app's devshell",
    "feat(bootstrap): prune nix profile generations before each entry",
    "feat(bootstrap): derive devshell profile path from XDG_DATA_HOME",
    "docs: document single-profile GC root trade-off in bootstrap"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

