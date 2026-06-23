# Make the Obsidian "HOME unset" settings test hermetic

`ObsidianBridgeSettingsTests.Load_WhenHomeIsUnset_ReturnsDisabledAndKeepsDefaults` reads the real
machine's `$HOME` and real user config instead of the injected fake, so it passes or fails
depending on whose machine runs it. On a host where the Obsidian bridge is actually enabled it
fails; on a clean machine it passes.

## Root cause

The test sets `_env["HOME"] = null; _env["XDG_CONFIG_HOME"] = null;` on an injected
`DictionaryEnvironmentAccessor` (`tests/VisualRelay.Tests/TestDoubles.cs`) intending to simulate
"HOME unset", then asserts `Assert.False(config.Enabled)`. But:

- `DictionaryEnvironmentAccessor` **removes** a key when it is set to null, so
  `GetEnvironmentVariable("HOME")` returns null.
- The loader reads HOME via `KeyEnvFile.GetEnv("HOME", accessor)`
  (`src/VisualRelay.Core/Configuration/KeyEnvFile.cs`), which is
  `accessor?.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name)` — when the
  accessor returns null it **falls back to the real process `HOME`**.
- So "unset" is not unset: `ObsidianBridgeSettings.Load`
  (`src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`) resolves the real
  `~/.config/visual-relay/.env` and reads whatever is there. On a machine whose real
  `~/.config/visual-relay/.env` contains `VR_OBSIDIAN_ENABLED=true`, `config.Enabled` is `true`
  and `Assert.False` fails; on a machine without that file it passes.

The test's outcome depends on the host's real home and config — it is not hermetic.

## What to build

Make the test isolate from the real environment so it passes regardless of the real `$HOME` and
regardless of whether a real `~/.config/visual-relay/.env` exists or sets `VR_OBSIDIAN_ENABLED`:

- The seam is `KeyEnvFile.GetEnv`'s real-env fallback, which production (the settings panel) relies
  on — so **do not change `GetEnv`'s production contract**. Instead express explicit-unset in a way
  that survives the fallback. The loader treats `string.IsNullOrWhiteSpace(home)` as unset and
  returns the disabled defaults, so e.g. returning an empty string for `HOME`/`XDG_CONFIG_HOME`
  (rather than null) makes the accessor authoritative and still exercises the degradation path; or
  use a test accessor that does not fall back. Pick whichever keeps the test's intent (the
  "HOME unset → disabled, defaults kept" path) while removing the real-env dependency.
- While here, scan `ObsidianBridgeSettingsTests` and sibling settings tests that use
  `DictionaryEnvironmentAccessor` for the same `= null`-means-unset pattern, and fix any others
  that leak the real environment.

Relevant anchors: `IEnvironmentAccessor`
(`src/VisualRelay.Core/Configuration/IEnvironmentAccessor.cs`, "returns null when unset"),
`ObsidianBridgeSettings.Load` (`var home = KeyEnvFile.GetEnv("HOME", accessor);` then
`if (string.IsNullOrWhiteSpace(home)) return defaults`).

## Environment notes (Tart VM vs host) — read this

- This test currently **passes on the Tart VM** and **fails on the host**. The VM's real home has
  no `~/.config/visual-relay/.env`, so the leaked fallback finds no enabled flag → defaults →
  pass. The host's real home has `~/.config/visual-relay/.env` with `VR_OBSIDIAN_ENABLED=true` →
  leaked → `Enabled == true` → fail.
- So you will **not** see this fail by default on the VM. To prove the bug before fixing,
  temporarily create `~/.config/visual-relay/.env` containing `VR_OBSIDIAN_ENABLED=true` on the VM
  (or point a real `HOME` env var at a directory containing that file) and run `./test.sh` — the
  test goes red. After your fix it must be **green both with and without** that file present.
  Delete the temp file when done.
- The fix is env-independent C#; nono is not involved.

> **Sequencing — third (1 → 2 → 3), independent.** This is a separate subsystem from
> `test-harness-1`/`-2` and can be implemented on its own; it is ordered last only so the group
> lands coherently. Its gate (`./visual-relay check`) needs a test run that does not hang, which
> `test-harness-1`/`-2` ensure. The implementer sees one task at a time; this file is
> self-contained.

## Done when

- The test passes regardless of the real `$HOME` and regardless of whether a real
  `~/.config/visual-relay/.env` exists or sets `VR_OBSIDIAN_ENABLED=true` (verify by running it
  both with and without that file present).
- It still exercises the intended "HOME unset → disabled, defaults kept" degradation path.
- `KeyEnvFile.GetEnv`'s production real-env fallback is unchanged.
- Any sibling settings tests with the same real-env leak are fixed (or confirmed none).
- `./visual-relay check` green; files under 300 lines; Conventional Commit (e.g.
  `test(obsidian): make HOME-unset bridge test hermetic`).
