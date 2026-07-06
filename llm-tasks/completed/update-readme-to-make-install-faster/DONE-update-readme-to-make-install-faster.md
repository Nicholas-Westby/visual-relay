# Update README to Recommend Shallow Clones for Install

Make the README's install instructions faster by switching the full-history
`git clone` to a shallow clone (`--depth 1`) in both install sections that
currently do full clones, with a one-line note explaining the trade-off. This
is a docs-only change; no code or build behavior changes.

## Current state (researched)

`README.md` has two self-contained install sections inside the
`<!-- BEGIN install section … -->` / `<!-- END install section -->` block, each
doing a full clone with the identical line:

- `# Install (macOS)` — `git clone https://github.com/Nicholas-Westby/visual-relay.git`
- `# Install (Windows)` — `git clone https://github.com/Nicholas-Westby/visual-relay.git`

Each is followed by `cd visual-relay` then `./visual-relay launch`.

`tests/VisualRelay.Tests/Installer5DocsTests.cs` pins README install content as
guard-as-tests (xUnit `[Fact]`). Existing assertions that must keep passing:

- `Readme_InstallSection_LeadsWithSourceCheckout` — macOS section contains `./visual-relay`
- `Readme_InstallSection_DocumentsNixBootstrap` — macOS section contains `nix` (case-insensitive)
- `Readme_WindowsInstallSection_LeadsWithSourceCheckout` — Windows section contains the substring `git clone` (so `git clone --depth 1 …` still satisfies it)
- `Readme_WindowsInstallSection_DocumentsLaunchCommand` — `./visual-relay launch`
- `Readme_WindowsInstallSection_DocumentsGlobalInstall` — `installed globally`

The `ExtractSection(content, heading)` helper in that file bounds a section from
its heading to the next `\n# ` heading, so the macOS section runs to
`# Install (Windows)` and the Windows section to `# What Visual Relay Does`;
both clone lines fall inside their respective sections.

The file-size guard (`FileSizeGuard.Enumerate`) covers only `*.cs`/`*.axaml`
under `src`/`tests`/`tools`, so `README.md` (markdown) is unaffected.
`./visual-relay check` also renders README screenshots via `ScreenshotCommand`;
a text-only README edit does not break that.

Commit messages follow Conventional Commits (`docs/commit-messages.md`): `docs:`
type, lowercase description, ≤72-char subject, no trailing period, no em dashes.

## What to build

1. **(Red)** Add one `[Fact]` to `Installer5DocsTests` asserting both install
   sections mention the shallow-clone flag, e.g.
   `Assert.Contains("--depth", ExtractSection(content, "# Install (macOS)"))`
   and the same for `"# Install (Windows)"`. Run it and confirm it fails
   against the current README.
2. **(Green)** In `README.md`, change the clone line in **both** the macOS and
   Windows install sections to
   `git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git`,
   and add one short sentence in each section noting `--depth 1` is a shallow
   clone (latest commit only) for a faster, smaller download, and can be
   omitted to fetch the full history. Keep each section self-contained (the
   BEGIN/END comment notes sibling tasks may shorten the README). Do not alter
   anything outside the install-section block.
3. Confirm the new test plus all existing `Installer5DocsTests` assertions pass.

## Done when

- Both `# Install (macOS)` and `# Install (Windows)` sections use
  `git clone --depth 1 …` and each has a one-line shallow-clone note.
- The new `--depth` `[Fact]` passes; every existing `Installer5DocsTests`
  assertion still passes.
- `./visual-relay test` (or `dotnet test`) is green; `./visual-relay check`
  passes.
- The commit message uses a `docs:` prefix, lowercase, ≤72 chars, no trailing
  period, no em dashes (e.g. `docs: recommend shallow clone in install steps`).
- No changes outside `README.md` and the one new `[Fact]` in
  `Installer5DocsTests.cs`.
