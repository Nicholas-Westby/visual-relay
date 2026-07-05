# Control API index page

Serve a plain HTML index at the control server's root (`GET /`) that documents the
API surface — a short purpose blurb, the list of routes, and the list of command
names. The routes and command names are enumerated from the code that already
defines them, never re-typed as literals in the page. The page uses no CSS and no
JavaScript, and is written to be accessible (WCAG).

## Current state

The app (`VisualRelay.App`, an Avalonia desktop app on .NET 10) runs an embedded
localhost HTTP control server so scripts/agents can drive the GUI. Relevant code:

- **`ControlServer.cs`** (`src/VisualRelay.App/Services/`) — binds a `System.Net.HttpListener`
  to `http://127.0.0.1:<port>/` (loopback only; default port `8765`, see
  `ControlServerOptions.FromEnvironment`). It is the built-in .NET HTTP listener — there is
  **no ASP.NET/Kestrel, no routing framework, no static-file middleware, no `wwwroot`**.
- **`ControlServer.Routing.cs`** → `RouteAsync` — the entire request surface. It is a hand-written
  `if`/`else` chain. In order:
  - an optional token gate: when `options.Token` is set, a request without a matching
    `X-VR-Token` header gets `401` **before any path dispatch** (so every path, including a new
    one, is gated when a token is configured);
  - `GET /health`, `GET /state`, `GET /screenshot` (optional `?path=`), `POST /command/{name}`;
  - a final fallthrough that returns `404 {"ok":false,"error":"not found"}`. **`GET /` hits this
    404 today** — the root is free.
  - Responses are written by two helpers in this same file: `WriteJsonAsync` (sets
    `ContentType = "application/json"`, writes UTF-8 bytes, sets `ContentLength64`) and an inline
    `image/png` write for screenshots. There is **no HTML writer yet**.
- **Command names already live in code, in two places** the page must reuse (do not re-type them):
  - `ControlApi.State.cs` → the `IcommandNames` array — the 15 `ICommand`-backed command names
    (`bootstrap`, `run-all`, `run-selected`, `resume`, `refresh`, `pause-toggle`, `archive-toggle`,
    `new-task`, `follow-running`, `start-backend`, `edit`, `rewrite-selected`, `cancel-rewrite`,
    `revert-rewrite`, `mark-done`). This same array already backs `BuildCommandsMap`.
  - `ControlApi.cs` → the `PropertyActions` array — the 7 property-backed actions (`select-task`,
    `boost-turns`, `open-folder`, `obsidian-scan`, `obsidian-bridge`, `select-activity-tab`,
    `select-detail-tab`). `ResolveCommand` (the `switch` in the same file) resolves the ICommand
    names; `InvokePropertyAction` handles the property actions.
- **The four route paths are string literals inside `RouteAsync`** (`"/health"`, `"/state"`,
  `"/screenshot"`, the `"/command/"` prefix). There is no route table.

Test conventions (`tests/VisualRelay.Tests/`, see `ControlServerTests.cs` and
`ControlServerBodylessPostTests.cs`):

- End-to-end server tests use `[Collection("Headless")]` on the class and `[AvaloniaFact]` on each
  test; they `new ControlServer(new ControlApi(vm, window), new ControlServerOptions(true, port, null))`
  on a free port (`GetFreePort()`), hit it with a shared `HttpClient`, assert status / content-type /
  body, and `server.Stop()` in a `finally`.
- Pure logic (no UI thread, no listener) is tested with plain `[Fact]` (e.g. `ControlServerOptionsTests`).
- There is a **~300-line-per-test-file guard** — keep new test files under it; split if needed.

## What to build

### 1. One source of truth for the surface (the "derive from code" requirement)

- **Command names.** Add a public, ordered accessor on `ControlApi`, e.g.
  `public static IReadOnlyList<string> CommandNames`, built by concatenating the **existing**
  `IcommandNames` and `PropertyActions` arrays (ICommand names first, then property actions). Do
  **not** introduce a third hand-typed list of command names anywhere. Adding a command to those
  arrays must automatically flow into this accessor (and therefore into the page).
- **Routes.** Add one declarative route catalog — the single place the top-level surface is
  described — e.g. a `ControlRoutes` static class exposing `public static IReadOnlyList<RouteInfo> All`,
  where `RouteInfo` carries: the HTTP method, the canonical path used for matching (`/`, `/health`,
  `/state`, `/screenshot`, `/command/`), a human display form for parameterized routes
  (`/screenshot[?path=…]`, `/command/{name}`), and a one-line purpose summary. `RouteAsync` must
  **match against these catalog paths** (reference the catalog's path values instead of the bare
  string literals it uses today) so a route cannot exist in the router but be missing from the page,
  or vice versa. The catalog is what the page renders.

### 2. Root route, HTML writer, and renderer

- In `RouteAsync`, add a `GET /` branch (before the `404` fallthrough) that renders and writes the
  index page. **Keep the token gate exactly where it is** — do not special-case `/`. Rationale:
  secure-by-default (a configured token protects the index too); the default no-token config still
  serves it to a browser. Any non-`GET` method on `/`, and any unknown path, keep falling through to
  the existing `404`.
- Add a `WriteHtmlAsync` helper next to `WriteJsonAsync` in `ControlServer.Routing.cs`, mirroring it
  exactly but with `ContentType = "text/html; charset=utf-8"`.
- Put the HTML generation in its own pure, static, UI-thread-free renderer so it can be unit-tested
  directly without spinning up a listener — e.g.
  `src/VisualRelay.App/Services/ControlIndexPage.cs` →
  `public static string Render(IReadOnlyList<RouteInfo> routes, IReadOnlyList<string> commandNames)`.
  The root branch calls `ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames)`.
  HTML-encode every interpolated text/attribute value with `System.Net.WebUtility.HtmlEncode`.

### 3. Page content and structure (accessible, no CSS, no JavaScript)

Emit a complete, valid HTML5 document. Target shape (structure fixed; the rows and list items are
**generated by iterating the catalogs**, never hand-listed):

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Visual Relay — Control API</title>
</head>
<body>
  <main>
    <h1>Visual Relay Control API</h1>
    <p>The Visual Relay control API is a localhost-only HTTP surface for driving the running app the
       same way its on-screen buttons do: read live state, invoke commands, and capture screenshots.
       It exists so scripts and agents can automate and observe the GUI with no human at the keyboard.
       Every command maps to a real UI action and honors the same enabled/disabled gating as the
       corresponding button.</p>

    <h2>Endpoints</h2>
    <table>
      <caption>HTTP endpoints exposed by the control API</caption>
      <thead>
        <tr><th scope="col">Method</th><th scope="col">Path</th><th scope="col">Purpose</th></tr>
      </thead>
      <tbody>
        <!-- one <tr> per ControlRoutes.All entry: <td><code>METHOD</code></td>
             <td><code>display-path</code></td><td>summary</td> -->
      </tbody>
    </table>

    <h2>Commands</h2>
    <p>Invoke with <code>POST /command/{name}</code>. Available names:</p>
    <ul>
      <!-- one <li><code>{name}</code></li> per ControlApi.CommandNames entry -->
    </ul>
  </main>
</body>
</html>
```

Use the intro copy above **verbatim** (it is the purpose blurb — 3 sentences, no more). Accessibility
requirements to satisfy:

- `<!DOCTYPE html>`; `<html lang="en">`; `<meta charset="utf-8">`; a descriptive `<title>`.
- Exactly one `<h1>`; section headings are `<h2>` with no skipped levels; content lives inside a
  `<main>` landmark; DOM order is the reading order.
- The endpoints table has a `<caption>` and `<th scope="col">` column headers.
- No color-conveyed meaning and no images; default black-on-white text meets contrast with no styling.

### 4. Hard constraints

- **No CSS**: no `<style>`, no `<link rel="stylesheet">`, no `style=` attributes, no external
  stylesheet of any kind.
- **No JavaScript**: no `<script>`, no `on…=` inline event-handler attributes, no `javascript:` URIs.

### 5. Docs

- Update the control-API section of `AGENTS.md` (the block that lists `GET /health`, `GET /state`,
  `POST /command/{name}`, `GET /screenshot`) to add `GET /` → the HTML index page.

## Done when

- `GET http://127.0.0.1:8765/` returns `200` with `Content-Type: text/html; charset=utf-8` and a valid
  HTML5 document; every other path still returns the existing `404`, and non-`GET` on `/` still `404`s.
- When a token is configured, `GET /` without a matching `X-VR-Token` header returns `401` (it stays
  behind the existing gate); with the default no-token config it serves the page.
- The page's endpoint rows are generated from `ControlRoutes.All` and its command list from
  `ControlApi.CommandNames`; **no route path or command name is hand-typed in `ControlIndexPage`.**
  `ControlApi.CommandNames` is composed from the existing `IcommandNames` + `PropertyActions` arrays.
- The intro is the exact 3-sentence blurb above.
- Accessibility: the document has `lang`, `charset`, a descriptive `<title>`, a single `<h1>` with
  ordered headings, a `<main>` landmark, and a table with `<caption>` + `scope="col"` headers.
- Automated tests are added (new file `tests/VisualRelay.Tests/ControlIndexPageTests.cs`; if it would
  exceed the ~300-line guard, split server round-trip vs. renderer into two files):
  - **Pure renderer tests** (`[Fact]`, no Avalonia) over `ControlIndexPage.Render(...)`:
    - Derivation: the output contains every name in `ControlApi.CommandNames`, and every method +
      display path in `ControlRoutes.All`. (Adding a command/route without it appearing on the page
      fails the build.)
    - **No-CSS tripwire**: the rendered HTML contains none of these (case-insensitive) — `<style`,
      `<link`, `stylesheet`, and the attribute form ` style=` (a simple substring/`Regex ` style=``
      check). Fails if CSS is introduced by tag or attribute.
    - **No-JavaScript tripwire**: the rendered HTML contains none of these (case-insensitive) —
      `<script`, `javascript:`, and any inline event-handler attribute (a simple ` on[a-z]+=` regex
      catches `onclick=`, `onload=`, etc.). Fails if JS is introduced by tag or attribute.
    - (These tripwires are deliberately simple regression guards, not exhaustive sanitizers.)
    - Structure: output contains `<!doctype html` (case-insensitive), `<html lang=`, a `<title>`,
      exactly one `<h1`, a `<main`, and the table's `<caption>` + `scope="col"`.
  - **End-to-end round-trip** (`[Collection("Headless")]` + `[AvaloniaFact]`, mirroring
    `ControlServerEndToEndTests`): start a `ControlServer` on a free port, `GET /`, assert `200`,
    `Content-Type` media type `text/html`, and a body beginning with `<!doctype html`. Add a
    token-configured variant asserting `GET /` without the header returns `401`.
- The full test suite passes (`./test.sh`), including the existing `ControlServer*`/`ControlApi*` tests.
