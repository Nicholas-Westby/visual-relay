## Stage 1 - Ideate

{
  "summary": "Add a `GET /` HTML index page to the control server. Create `ControlApi.CommandNames` (composed from existing IcommandNames + PropertyActions) and a `ControlRoutes` catalog (the single source of truth for routes). Replace RouteAsync's bare string literals with catalog references, add `WriteHtmlAsync` and a `ControlIndexPage.Render(routes, commands)` pure renderer that emits an accessible, CSS/JS-free HTML5 document. Write unit tests for the renderer (derivation, no-CSS/JS tripwires, structure) plus end-to-end server tests. Update AGENTS.md.",
  "options": [
    "Option A — Minimal-catalog approach. Create `ControlRoutes` as a small record/list alongside RouteAsync. RouteAsync continues using an if/else chain but references `ControlRoutes.All[i].Path` instead of raw `\"/health\"` strings. This is the closest to the spec: \"reference the catalog's path values.\" Lowest risk, touch the fewest lines in the router. `ControlIndexPage.Render` iterates the same catalog for the table. The test suite needs the fewest new fixtures.",
    "Option B — Catalog-driven dispatch. Create `ControlRoutes` that also carries a `Func<HttpListenerRequest, Task<bool>>` delegate or similar dispatch handler (or use a `Regex`-based route table with named groups). RouteAsync loops the catalog and invokes the matching handler. This eliminates the if/else chain entirely, making it impossible for a handler to exist outside the catalog. Higher churn in the router, but the strongest compile-time guarantee that the index page stays in sync with dispatch. Route dispatch logic moves into the catalog entries themselves.",
    "Option C — Hybrid with a helper dictionary. Create `ControlRoutes` and additionally a `Dictionary<(string Method, string Path), Func<...>>` within RouteAsync that is populated from the catalog. The if/else chain is replaced by a single dictionary lookup followed by a `not found` fallthrough. Keeps dispatch simple (no regex, no delegates in RouteInfo) while being more DRY than option A. The catalog remains a plain-data list, but the router collects it into a dictionary once at construction/start time for O(1) matching rather than O(n) if/else. `/command/{name}` and `/screenshot[?path=…]` still need a manual prefix check after the dictionary miss."
  ]
}

## Stage 2 - Research

{
  "findings": "Control server is pure System.Net.HttpListener (no ASP.NET/Kestrel). RouteAsync (ControlServer.Routing.cs:8-56) is a hand-written if/else chain with raw string literals for 4 routes + 404 fallthrough. GET / currently returns 404. WriteJsonAsync helper exists (lines 109-115); no HTML writer yet. Command names live in two private static arrays: IcommandNames (15 names, ControlApi.State.cs:100-105) and PropertyActions (7 names, ControlApi.cs:53-55). ControlApi is a sealed partial class with constructor taking MainWindowViewModel+Window. ControlServer is also sealed partial with constructor taking ControlApi+ControlServerOptions. Test conventions: [Collection(\"Headless\")] + [AvaloniaFact] for server tests, shared HttpClient, GetFreePort() helper, server.Stop() in finally. Pure logic tests use plain [Fact]. ~300-line-per-test-file guard. AGENTS.md (94 lines) documents the control API endpoints (needs GET / entry). TestDoubles.cs has DictionaryEnvironmentAccessor for isolated env vars. ControlJson.cs has Json.Object/Serialize/Read helpers. No existing ControlRoutes or ControlIndexPage files exist. The VisualRelay.Tests.csproj references both VisualRelay.App and VisualRelay.Core.",
  "constraints": [
    "No ASP.NET/Kestrel, no routing framework, no static-file middleware, no wwwroot — plain System.Net.HttpListener only",
    "RouteAsync must reference ControlRoutes.All[i].Path values instead of bare string literals",
    "ControlApi.CommandNames must compose from existing IcommandNames + PropertyArrays — no third hand-typed list",
    "ControlIndexPage must not contain any route path or command name as literal — everything comes from catalogs",
    "No CSS: no <style>, no <link rel=\"stylesheet\">, no style= attributes, no external stylesheets",
    "No JavaScript: no <script>, no on…= attributes, no javascript: URIs",
    "Accessibility: <!DOCTYPE html>, <html lang=\"en\">, <meta charset=\"utf-8\">, exactly one <h1>, ordered <h2> headings, <main> landmark, table with <caption> + <th scope=\"col\">",
    "Intro blurb must be verbatim 3-sentence text from spec",
    "HTML-encode every interpolated text/attribute value with System.Net.WebUtility.HtmlEncode",
    "Token gate must remain in place for GET / — no special-casing",
    "Non-GET on / and unknown paths must still 404",
    "Test files must stay under ~300-line guard; split if needed",
    "[Collection(\"Headless\")] and [AvaloniaFact] required for server round-trip tests",
    "Pure renderer tests use plain [Fact] (no Avalonia)",
    "GetFreePort() helper for ephemeral port in E2E tests",
    "Shared HttpClient across tests; per-request headers via HttpRequestMessage, not DefaultRequestHeaders",
    "server.Stop() in finally block for all server tests",
    "All existing tests must continue to pass (./test.sh or ./visual-relay test)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The `GET /` route at the control server root currently returns 404 because there is no handler for it. RouteAsync (ControlServer.Routing.cs:26-56) is a hand-written if/else chain that dispatches exactly four routes — /health, /state, /screenshot, /command/{name} — using bare string literals then falls through to a 404 fallthrough on line 54-55. There is no route catalog class, no HTML writer helper, no public CommandNames accessor, and no ControlIndexPage renderer anywhere in the source tree (confirmed by grep). The two arrays of command names — IcommandNames (15 names, ControlApi.State.cs:100-105) and PropertyActions (7 names, ControlApi.cs:53-55) — are both private static readonly, with no public accessor composing them. AGENTS.md (lines 48-67) documents four endpoints but omits GET /. The existing test suite (ControlServerTests.cs, ControlServerBodylessPostTests.cs, ControlApiTests.cs, etc.) establishes clear conventions: [Collection(\"Headless\")] + [AvaloniaFact] for server round-trips with shared HttpClient + GetFreePort() + server.Stop() in finally; plain [Fact] for pure-logic tests; ~300-line guard per test file. No build or test failures are present — this is a greenfield feature gap, not a regression.",
  "excerpts": [
    "ControlServer.Routing.cs:29 — `if (path == \"/health\" && method == \"GET\")` — bare string literal, no catalog reference",
    "ControlServer.Routing.cs:48 — `if (path.StartsWith(\"/command/\", StringComparison.Ordinal) && method == \"POST\")` — hardcoded prefix",
    "ControlServer.Routing.cs:54-55 — `context.Response.StatusCode = 404; await WriteJsonAsync(context, Json.Object((\"ok\", false), (\"error\", \"not found\")))` — the 404 fallthrough that catches GET /",
    "ControlServer.Routing.cs:109-115 — `WriteJsonAsync` sets `ContentType = \"application/json\"`; no `WriteHtmlAsync` with `text/html; charset=utf-8` exists",
    "ControlApi.State.cs:100-105 — `private static readonly string[] IcommandNames = [\"bootstrap\", \"run-all\", ...]` — 15 ICommand names, private",
    "ControlApi.cs:53-55 — `private static readonly string[] PropertyActions = [\"select-task\", \"boost-turns\", ...]` — 7 property-action names, private",
    "AGENTS.md:48-67 — endpoint docs list GET /health, GET /state, POST /command/{name}, GET /screenshot — no GET / entry",
    "ControlServerTests.cs:62-63 — `[Collection(\"Headless\")] public sealed class ControlServerEndToEndTests` — test conventions: Headless collection + AvaloniaFact",
    "ControlServerTests.cs:70 — `private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) }` — shared HttpClient pattern",
    "ControlServerTests.cs:99-126 — Token_WhenConfigured test: asserts 401 without header, 200 with matching X-VR-Token header — token gate pattern for new tests",
    "ControlServerTests.cs:226-233 — `GetFreePort()` helper using TcpListener on port 0 — ephemeral port pattern"
  ],
  "repro": "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8765/ would return 404 against the running app, confirming the gap. Alternatively, inspect RouteAsync in ControlServer.Routing.cs: no branch matches path == \"/\" with method == \"GET\", so the 404 fallthrough at line 54 always fires for GET /."
}

## Stage 4 - Plan

{
  "plan": "## Implementation plan\n\n### Step 1: Create `ControlRoutes` catalog (`+src/VisualRelay.App/Services/ControlRoutes.cs`)\n\nNew file ~35 lines. A static class `ControlRoutes` with:\n- A `RouteInfo` record: `string Method`, `string Path` (canonical path used for matching), `string DisplayPath` (human form for the page, e.g. `/screenshot[?path=…]`), `string Summary` (one-line purpose).\n- Five static readonly `RouteInfo` fields: `Index` (`GET`, `/`, `/`, purpose blurb), `Health`, `State`, `Screenshot` (`/screenshot[?path=…]`), `Command` (`POST`, `/command/`, `/command/{name}`).\n- `public static readonly IReadOnlyList<RouteInfo> All` referencing all five in order.\n\n### Step 2: Add `CommandNames` accessor on `ControlApi` (`src/VisualRelay.App/Services/ControlApi.cs`)\n\nAdd after `PropertyActions` (line ~55):\n```csharp\n/// <summary>\n/// Ordered list of every documented command name — ICommand-backed actions\n/// first (from IcommandNames), then property-backed actions (from PropertyActions).\n/// The index page renders this list; adding a command to either source array\n/// automatically flows here and onto the page.\n/// </summary>\npublic static IReadOnlyList<string> CommandNames { get; } = [..IcommandNames, ..PropertyActions];\n```\n\nBoth arrays are `private` in the same `partial class ControlApi`, so no visibility change needed. The spread expression concatenates at static-init time.\n\n### Step 3: Create `ControlIndexPage` renderer (`+src/VisualRelay.App/Services/ControlIndexPage.cs`)\n\nNew file ~70 lines. A static class with:\n```csharp\npublic static string Render(IReadOnlyList<RouteInfo> routes, IReadOnlyList<string> commandNames)\n```\n\nBuilds a `StringBuilder`-backed HTML5 document. Every interpolated value (route method, path, summary, command name) is passed through `System.Net.WebUtility.HtmlEncode`. The page structure:\n- `<!DOCTYPE html>`, `<html lang=\"en\">`, `<head>` with `<meta charset=\"utf-8\">` and `<title>`.\n- `<body>` with `<main>` containing: `<h1>`, intro `<p>` (verbatim 3-sentence blurb), `<h2>Endpoints</h2>` + `<table>` (with `<caption>`, `<thead>` with `scope=\"col\"`, `<tbody>` iterating `routes`), `<h2>Commands</h2>` + `<ul>` iterating `commandNames`.\n- No `<style>`, no `<script>`, no `style=` attributes, no `on…=` handlers, no external references.\n\n### Step 4: Update `RouteAsync` and add `WriteHtmlAsync` (`src/VisualRelay.App/Services/ControlServer.Routing.cs`)\n\n- Replace bare string literals in the `if`/`else` chain with `ControlRoutes.*.Path`/`.Method` references:\n  - `\"/health\"` → `ControlRoutes.Health.Path`\n  - `\"/state\"` → `ControlRoutes.State.Path`\n  - `\"/screenshot\"` → `ControlRoutes.Screenshot.Path`\n  - `\"/command/\"` → `ControlRoutes.Command.Path`\n- Add the index branch BEFORE the 404 fallthrough (but AFTER the token gate — no special-casing):\n  ```csharp\n  if (path == ControlRoutes.Index.Path && method == ControlRoutes.Index.Method)\n  {\n      var html = ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames);\n      await WriteHtmlAsync(context, html);\n      return;\n  }\n  ```\n- Add `WriteHtmlAsync` helper after `WriteJsonAsync` (~line 115): mirrors `WriteJsonAsync` exactly but sets `ContentType = \"text/html; charset=utf-8\"`.\n\n### Step 5: Write tests (`+tests/VisualRelay.Tests/ControlIndexPageTests.cs`)\n\nSingle file, estimated ~220 lines (fits under 300-line guard). Two sections:\n\n**A. Pure renderer tests** (`[Fact]`, no Avalonia):\n- `Render_ContainsEveryCommandName` — calls `ControlIndexPage.Render(ControlRoutes.All, ControlApi.CommandNames)`, asserts each name in `ControlApi.CommandNames` appears in the output.\n- `Render_ContainsEveryRoute` — asserts each `Method` + `DisplayPath` from `ControlRoutes.All` appears.\n- `Render_HasNoCss` — case-insensitive `Contains` checks for `<style`, `<link`, `stylesheet`, and `Regex.IsMatch(html, @\" style=\")` — all must be false.\n- `Render_HasNoJavaScript` — case-insensitive checks for `<script`, `javascript:`, and `Regex.IsMatch(html, @\" on[a-z]+=\")` — all must be false.\n- `Render_HasValidStructure` — `<!doctype html` (case-insensitive), `<html lang=`, a `<title>`, exactly one `<h1`, a `<main`, `<caption>`, `scope=\"col\"`.\n\n**B. End-to-end round-trip tests** (`[Collection(\"Headless\")]` + `[AvaloniaFact]`):\n- `IndexPage_Returns200_WithHtmlContentType` — start server on free port, `GET /`, assert `200`, `Content-Type` media type `text/html`, body starts with `<!doctype html`.\n- `IndexPage_WithToken_Returns401WithoutHeader` — start server with token `\"letmein\"`, `GET /` without header → `401`. Same pattern as `Token_WhenConfigured` in existing tests.\n\n### Step 6: Update `AGENTS.md`\n\nIn the endpoints list (lines 48-67), add before `GET /health`:\n```\n- `GET /` — HTML index page documenting the API surface (routes and commands).\n```\n\n### Verification\n\n- `./visual-relay test` passes all existing and new tests.\n- `./visual-relay check` passes (file-size guards, format verification, build).",
  "manifest": [
    "+src/VisualRelay.App/Services/ControlRoutes.cs",
    "src/VisualRelay.App/Services/ControlApi.cs",
    "+src/VisualRelay.App/Services/ControlIndexPage.cs",
    "src/VisualRelay.App/Services/ControlServer.Routing.cs",
    "+tests/VisualRelay.Tests/ControlIndexPageTests.cs",
    "AGENTS.md"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3, untracked deleted: 2.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/ControlIndexPageTests.cs"],
  "rationale": "Tests written first and verified to fail (17 compile errors for missing ControlIndexPage, ControlRoutes, ControlApi.CommandNames). After implementing all five code changes (ControlRoutes catalog, CommandNames accessor, ControlIndexPage renderer, RouteAsync GET / branch + WriteHtmlAsync, AGENTS.md), all 8 tests pass: 5 pure renderer tests (derivation, no-CSS tripwire, no-JS tripwire, structure) and 3 E2E round-trip tests (200 + text/html content type, 401 with token configured, 404 on non-GET /)."
}

## Stage 6 - Implement

{
  "summary": "Implemented the control API index page feature across 5 files. Created ControlRoutes.cs catalog (single source of truth for routes with RouteInfo record), added ControlApi.CommandNames accessor in ControlApi.State.cs (composing existing IcommandNames + PropertyActions arrays), created ControlIndexPage.cs pure HTML renderer (accessible, no CSS/JS, all values HTML-encoded and iterated from catalogs), updated ControlServer.Routing.cs (GET / branch before 404 fallthrough, replaced bare string literals with catalog references, added WriteHtmlAsync helper), and updated AGENTS.md. All 8 new tests pass; full suite: 2587 passed, 0 failed, 22 skipped (pre-existing platform skips)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{
  "summary": "Fixed test class naming mismatch in ControlIndexPageTests.cs. The test runner script filters by file stem (ControlIndexPageTests), but the classes were named ControlIndexPageRendererTests and ControlIndexPageEndToEndTests — neither matched. Renamed to ControlIndexPageTests (renderer) and ControlIndexPageTestsEndToEnd (E2E). Targeted run: 8 passed, 0 failed. Full suite: 2587 passed, 0 failed, 22 skipped (all pre-existing)."
}

## Stage 9 - Verify

{
  "summary": "Implemented GET / HTML index page at the control server root, documenting the full API surface (5 routes, 22 command names) from code-derived catalogs. Added ControlRoutes catalog (single source of truth), ControlApi.CommandNames accessor, ControlIndexPage pure renderer (accessible, no CSS/JS, HTML-encoded), updated RouteAsync to reference catalog paths and serve the index page with a new WriteHtmlAsync helper, and added 8 tests (5 pure renderer + 3 E2E round-trip). All 2587 tests passing.",
  "commitMessages": [
    "feat: add GET / HTML index page documenting the control API surface",
    "feat: introduce ControlRoutes catalog as single source of truth for route dispatch and documentation",
    "feat: add CommandNames accessor on ControlApi composing IcommandNames and PropertyActions for the index page",
    "refactor: replace bare string literals in RouteAsync with ControlRoutes catalog references",
    "test: add pure-renderer and end-to-end tests for control API index page"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

