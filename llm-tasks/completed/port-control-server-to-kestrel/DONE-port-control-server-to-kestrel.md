# Port the Control Server from HttpListener to Kestrel

The loopback control server currently rides on .NET's managed `HttpListener`, whose macOS/Linux
handling of a POST with no Content-Length is nondeterministic: the listener writes its own
`411 Length Required` to the client while ALSO dispatching the request context to the app
handler, with request properties that are not reliable at dispatch time. Observed on 2026-07-06
(one pipeline run, two flakes, two modes, both passing on rerun):

- `ControlServerBodylessPostTests.BodylessPost_NoContentLength_DoesNotExecuteCommand` failed in
  17ms with "PauseRequested should remain false but was flipped" — the client had already read a
  411 status line, yet `pause-toggle` executed. The command ran BEHIND an error response, the
  exact bug the guard exists to prevent, proving the guard is not airtight (the listener can
  present the handler a context indistinguishable from a valid empty-body request).
- `ControlServerBodylessPostTests.ContentLengthZero_Post_ExecutesCommandNormally` got no response
  at all; its untimed `reader.ReadLineAsync()` waited until the test gate's
  `--blame-hang-timeout 60s` killed the whole test host — the suite aborted at 2396 passed /
  0 failed with ~200 tests never run, reported as "test host process crashed". Autopsy:
  `.relay/remove-automated-tests-for-readme/stage9-attempt1.verify-output.txt`.

The app cannot observe or order a race against a second writer inside the framework, so neither
the 411 guard nor any socket-level test of it can be made deterministic on `HttpListener`.
Kestrel dissolves the problem instead of guarding it: per RFC 9112 a request with neither
Content-Length nor Transfer-Encoding has a zero-length body, and Kestrel delivers it as a normal
empty-body request — it never writes an unsolicited error behind the handler's back, so "a
command never executes while the client receives an error" holds by construction. Port the
server to Kestrel, delete the guard and the flaky raw-TCP tests, and make the control-surface
tests socketless and deterministic via the in-memory `TestServer`.

## Current state (researched)

- `src/VisualRelay.App/Services/ControlServer.cs` — `public sealed partial class
  ControlServer(ControlApi api, ControlServerOptions options) : IDisposable` on `HttpListener`.
  `Start()` never throws: binds `http://127.0.0.1:<port>/` (loopback ONLY — never `+`/`*`, so
  macOS shows no firewall prompt), prints exactly one Console.Error line
  (`vr-control: listening on http://127.0.0.1:<port>` on success, `vr-control: failed to start
  (<msg>); control API disabled` on bind failure, `vr-control: disabled via VR_CONTROL_DISABLE`
  when disabled). `Stop()` is idempotent, never throws, bounds accept-loop teardown at 5s.
  Requests are handled off the accept loop; handler exceptions become a 500 JSON
  (`TryWriteErrorAsync`); the response is always closed.
- `src/VisualRelay.App/Services/ControlServer.Routing.cs` — `RouteAsync`: optional token auth
  (`options.Token` set → require matching `X-VR-Token` header, else 401 JSON; `/health` gated
  too), then routes via `ControlRoutes`: `GET /` index HTML (`ControlIndexPage.Render`),
  `GET /health`, `GET /state` (`api.BuildStateJsonAsync`), `GET /screenshot` (PNG body,
  optional `X-Screenshot-Path` response header), `POST /command/<name>` (name is
  URL-unescaped), 404 JSON fallback. `HandleCommandAsync` contains THE guard to delete:
  `if (request.ContentLength64 < 0 && !request.HasEntityBody) { … 411 … }` with a comment
  documenting the HttpListener quirk. Content types are `application/json`,
  `text/html; charset=utf-8`, `image/png`.
- `src/VisualRelay.App/Services/ControlApi.cs` (+ partials) — transport-agnostic already; every
  VM/window touch marshals via `Dispatcher.UIThread`. Confirm-gated commands (`mark-done`,
  `rewrite-selected`) refuse with 409 unless the JSON body has `"confirm": true` — an empty-body
  POST therefore still cannot drive a destructive command after the 411 guard is gone.
- `src/VisualRelay.App/Services/ControlServerOptions.cs` — env contract to keep byte-identical:
  `VR_CONTROL_DISABLE=1`, `VR_CONTROL_PORT` (default 8765, invalid → default),
  `VR_CONTROL_TOKEN`. Wired in `App.axaml.cs` (`_controlServer = new ControlServer(new
  ControlApi(viewModel, window), options)` then `Start()`).
- Tests today: `tests/VisualRelay.Tests/ControlServerTests.cs` (234 lines: deterministic
  `ControlServerOptions` parsing tests, plus HttpListener round-trips over real sockets using a
  shared `HttpClient` with `Timeout = TimeSpan.FromSeconds(5)` and a `GetFreePort()`
  bind-release-rebind helper — a TOCTOU that exists only because HttpListener cannot bind
  port 0); `tests/VisualRelay.Tests/ControlIndexPageTests.cs` (three more `GetFreePort` socket
  round-trips plus pure render tests); `tests/VisualRelay.Tests/ControlServerBodylessPostTests.cs`
  (the two flaky raw-`TcpClient` tests — delete the file). UI-touching tests use
  `[AvaloniaFact]` in `[Collection("Headless")]`.
- Environment: ALL build/test/pipeline work happens in the nix devshell (`flake.nix`), and its
  `dotnet-sdk_10` already ships the `Microsoft.AspNetCore.App` shared framework (verified:
  `<store>/dotnet-sdk-10.0.301/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.9`), so the
  new `<FrameworkReference Include="Microsoft.AspNetCore.App" />` needs NO flake.nix change and
  nothing brew-related to implement or test. The brew formula (`packaging/visual-relay.rb`) is
  only the end-user distribution artifact: it runs a SELF-CONTAINED published bundle
  (`tools/VisualRelay.Packaging`), which bundles the framework automatically — expect a
  bundle-size increase, verify with the packaging end-to-end test.
- `README.md` and `AGENTS.md` mention the control API/`VR_CONTROL_*` — grep for `411` and
  bodyless-POST wording anywhere (docs, `ControlIndexPage`, comments) and update to the new
  semantics.

## What to build (TDD-first)

1. **Extract a transport-agnostic pipeline.** Turn the `RouteAsync` logic into a factory the
   host and tests share, e.g. `internal static RequestDelegate BuildHandler(ControlApi api,
   ControlServerOptions options)` operating on `HttpContext`. Same auth, routes, status codes,
   content types, 404/500 JSON shapes as today.
2. **Deterministic tests first** (new `ControlServerKestrelTests.cs` or repurposed existing
   files) hosting that handler in `Microsoft.AspNetCore.TestHost.TestServer` — in-memory, no
   sockets, no ports, no `GetFreePort`: health OK; token required → 401 without header, 200
   with; unknown route → 404 JSON; `POST /command/pause-toggle` with NO body → 200 and the
   command executes; with `Content-Length: 0` → 200 and executes (under Kestrel these are the
   same shape — assert both anyway as regression documentation); disabled command → 409;
   confirm-gated command without `"confirm": true` → 409 and no effect; screenshot returns
   `image/png` (+ header when a path is given); index HTML renders.
3. **Rehost `ControlServer` on Kestrel** behind the same public surface (ctor, `Start`, `Stop`,
   `Dispose`): a minimal builder (`WebApplication.CreateEmptyBuilder`/`CreateSlimBuilder`) with
   logging providers cleared (no ASP.NET console spam), listening on `127.0.0.1:<port>` only.
   Preserve: never-throw `Start()` with the three exact `vr-control:` Console.Error lines;
   idempotent bounded `Stop()`. Add a `BoundPort` (actual port after start) so port `0` becomes
   usable and the `GetFreePort` TOCTOU dies.
4. **Delete the dissolved surface:** the 411 guard in `HandleCommandAsync` (+ its quirk
   comment) and `ControlServerBodylessPostTests.cs`. Document the deliberate semantic change in
   the routing code: a bodyless POST is a valid empty-body request (RFC 9112) and executes.
5. **Convert the remaining socket round-trips** in `ControlServerTests.cs` and
   `ControlIndexPageTests.cs` to `TestServer`, keeping exactly ONE real-socket smoke test
   (server on port 0, 5s-timeout `HttpClient`, assert `/health` and the listening console line)
   to prove actual Kestrel binding.
6. **Project wiring:** `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in
   `src/VisualRelay.App/VisualRelay.App.csproj`; `Microsoft.AspNetCore.TestHost` package in the
   test project.

## Done when

- The control-surface test suite contains no `GetFreePort`, no raw `TcpClient`, and no untimed
  network awaits; everything except the single port-0 smoke test runs in-memory.
- An empty-body `POST /command/<name>` returns 200 and executes (deterministic test), and
  confirm-gated commands still 409 without a confirm body.
- `ControlServerBodylessPostTests.cs` and the 411 guard are gone; no stale `411` references
  remain in code or docs.
- App behavior is externally unchanged otherwise: same `vr-control:` lines, same
  `VR_CONTROL_*` contract, loopback-only binding, same route/status/content-type surface.
- Full suite passes via the authoritative gate (`dotnet test tests/VisualRelay.Tests/…
  --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none`); `./visual-relay check`
  passes, including the packaging end-to-end bundle test with the new framework reference.

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset); see
  `docs/commit-messages.md` and `AGENTS.md`.
- 300-line guard: `ControlServerTests.cs` is at 234 lines — split new test files rather than
  growing it; `ControlServer.cs`/`ControlServer.Routing.cs` (159/137) leave room but keep the
  handler-factory extraction in its own file if either nears the ceiling.
- Do not change `ControlApi`, `ControlRoutes`, `ControlIndexPage`, or `ControlServerOptions`
  semantics; the port is a transport swap, not an API redesign.
- Keep the dependency footprint to the framework reference + TestHost package — no MVC, no
  minimal-API route builders required; a single `RequestDelegate` suffices.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
