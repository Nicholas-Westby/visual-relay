# (Suggested) No at-a-glance signal that the model backend is reachable

This is an additional idea surfaced while diagnosing the "backend down" failure class — propose
before implementing if scope is a concern.

The whole pipeline depends on the model backend at `http://127.0.0.1:4000`
(`src/VisualRelay.Core/Execution/SwivalProfileSession.cs`), but the UI gives no standing signal
of whether it's reachable. The operator only finds out by running a task and watching it fail.
A persistent indicator turns the most common environmental problem into something you see
before you press Run.

The top bar (`src/VisualRelay.App/Views/Controls/TopBar.axaml`) already hosts global affordances
(Refresh, Pause, Run All) and is the natural home for a global status dot.

## Recommended fix

- Add a small reachability indicator to the top bar: a green/red dot + label (e.g. "backend:
  127.0.0.1:4000") reflecting the latest probe result. Reuse the readiness probe from
  `preflight-model-backend-readiness.md` (don't add a second probe implementation).
- Refresh it on a light interval and on the existing Refresh action; keep it non-blocking.
- When down, offer a one-click recovery (start the backend, or at least a tooltip pointing at
  the start path from `autostart-model-backend-on-launch.md`).

This complements the one-shot pre-flight check (which fires at startup / before a run) with
continuous visibility.

## Sequencing

- **Land after `preflight-model-backend-readiness.md`** and reuse its readiness probe — do not
  add a second probe. This task is purely the top-bar surface over that probe.
- The "start the backend" recovery action depends on `autostart-model-backend-on-launch.md`; wire
  it once that exists, otherwise show a tooltip pointing at the manual start path.

## Done when

- The top bar shows backend-reachable vs. unreachable at a glance, updating on refresh and on a
  light interval.
- The indicator reuses the shared readiness probe rather than duplicating it.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
