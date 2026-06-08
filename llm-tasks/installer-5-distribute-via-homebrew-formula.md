# Ship Visual Relay via a Homebrew formula (self-contained, ad-hoc signed, no notarization)

Visual Relay is source-only today: `./visual-relay launch` runs
`dotnet run --project src/VisualRelay.App/VisualRelay.App.csproj` (the `launch|run`
case in the `visual-relay` script), which needs the .NET 10 SDK (or nix) and a
full from-source build. A new macOS user must clone the repo, install the SDK,
install `uv`, and build — and there's no app to double-click. We want a
developer-friendly `brew install` that needs **no SDK and no Apple Developer
account**.

The packaging choice is load-bearing. Homebrew 5.0 (Nov 2025) deprecated
`--no-quarantine` and is removing unsigned/un-notarized **casks** from the
official tap (Sept 2026), so a cask would force notarization. A **formula**, by
contrast, installs via `curl` + `tar` — neither sets the `com.apple.quarantine`
attribute — so Gatekeeper never assesses the binary and a terminal launch of the
self-contained build just runs, **no notarization required**. (Apple Silicon
requires at least an *ad-hoc* signature to execute; that is free and `dotnet
publish` applies it to the `osx-arm64` apphost automatically.)

## Goal

`brew install nicholas-westby/tap/visual-relay` on a clean Mac installs a
self-contained Visual Relay (no .NET SDK/runtime prereq) plus the `visual-relay`
CLI, with `uv` pulled as a dependency. `visual-relay launch` opens the app with
**no Gatekeeper prompt and no notarization**. The shipped surface is
`launch` / `init` only — sample tooling is dev-only and excluded.

## Approach (suggested)

- **Self-contained publish**: `dotnet publish src/VisualRelay.App -c Release -r
  osx-arm64 --self-contained true` (and `osx-x64`), runtime bundled. Non-AOT,
  non-trimmed for Avalonia safety (trimming/AOT is a future size optimization).
  Verify the `osx-arm64` apphost is ad-hoc signed; add a belt-and-suspenders
  `codesign --force -s -` in CI.
- **Launcher works in both worlds**: make `visual-relay launch` **prefer a
  published binary** when present (exec the self-contained app) and fall back to
  `dotnet run` for source checkouts — same command, dev and brew. The backend
  bootstrap (`tools/backend/backend.sh start`) and the tier-config generator
  (`installer-3-generate-backend-config-from-present-keys.md`) must be reachable from the
  installed layout, so ship `tools/backend/{backend.sh,litellm-config.yaml}` and
  the generator invoker alongside the app.
- **Release pipeline**: a new `.github/workflows/release.yml` on tag push builds
  the arm64/x64 self-contained bundles (app + `visual-relay` + `tools/backend/`),
  tars them, computes `sha256`, and creates a GitHub Release with the artifacts.
- **Homebrew formula** in tap `Nicholas-Westby/homebrew-tap` (template kept
  in-repo at `packaging/visual-relay.rb`): `depends_on "uv"`; per-arch `url` +
  `sha256` to the release tarballs; install into `libexec`; symlink
  `bin/visual-relay`. **Formula, not cask** (no quarantine).
- **Exclude sample tooling**: drop `sample-reset` from the shipped launcher's
  dispatch/usage (or guard it to source checkouts); do not bundle
  `tools/VisualRelay.SampleTasks`; move the `/Users/admin/Dev/sample-tasks`
  references out of `README.md` into a contributor doc (e.g. `AGENTS.md` /
  `docs/`). Keep `init` and `launch` documented for users.
- **README**: a user "Install" section (`brew install …`; **install via brew/curl,
  never a browser zip** — a browser download re-quarantines); document the
  formula-not-cask + no-notarization rationale.

## Files

- New `.github/workflows/release.yml`.
- New `packaging/visual-relay.rb` (formula template; lives in the tap when published).
- `visual-relay` (launcher: prefer published binary; drop/guard `sample-reset`).
- `Directory.Build.props` / `Directory.Build.targets` (RID / self-contained
  publish settings, if needed).
- `README.md` + a contributor doc for the dev-only sample workflow.

## Acceptance (this is packaging/devops — verified by a real install, not `./visual-relay check`)

- On a clean Mac with **no .NET SDK**, `brew install
  nicholas-westby/tap/visual-relay` succeeds, pulls `uv`, and `visual-relay launch`
  opens the app with **no Gatekeeper "unidentified developer" prompt**.
- The published bundle contains **no** SampleTasks tool and the launcher exposes
  **no** `sample-reset`.
- A source checkout still builds/runs via `./visual-relay launch` (the dotnet
  path) unchanged.
- A CI smoke step publishes the self-contained build and asserts the apphost is
  signed (`codesign -dv`) and launches (e.g. `--help` / headless).
- Where code changes (launcher, props), `./visual-relay check` stays green; files
  under 300 lines; Conventional Commit subjects.

## Sequencing

Independent of the key/routing tasks to build, but the installed **first run**
depends on them: keys live in the user-level `.env`
(`installer-1-relocate-provider-keys-to-user-config.md`), the backend generates its config
from present keys (`installer-3-generate-backend-config-from-present-keys.md`), and the
freshly-installed user first hits the in-app HF gate
(`installer-4-require-hugging-face-and-add-key-setup-panel.md`). Land those so a brew user has
a working keyless→HF onboarding. `init` (the in-app "Set it up for Relay" and
`visual-relay init`, logic in `VisualRelay.Core.Init`) is the fresh-install path
for the user's **own** repo — there is no sample repo for installed users.

## Notes

No Apple Developer account, no notarization, no Finder/Dock double-click `.app`
(terminal launch only — the audience is developers, the choice made in the
installer brainstorm). The one rule to communicate to users: install via
`brew` / `curl`, **not** a browser download. Ad-hoc signing satisfies Apple
Silicon's execute requirement and is free; it is **not** notarization and does
not need to be.
