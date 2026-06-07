# Troubleshooting

Operational notes for the dev loop. Add entries as you hit (and solve) things.

## A test run hangs / never finishes

`./visual-relay test` normally finishes in ~10s. If it sits at `Testing (NNNs)` with the
counter climbing and **no test ever completing**, a test has deadlocked — it's hung, not slow
(a slow test still eventually prints `Passed … [NNNNN ms]`).

Find the culprit — abort after 30s of inactivity and dump which test(s) were running:

```bash
./visual-relay test --blame-hang --blame-hang-timeout 30s
```

The output prints `The test running when the crash occurred:`. If **two or more** tests are
listed, they were running concurrently and likely deadlocked on shared global state — our
Avalonia headless UI tests each spin up a process-global Avalonia app via
`HeadlessUnitTestSession.StartNew`, and xUnit runs separate test classes in parallel by
default, so two headless classes overlapping can wedge each other.

Cleanup: the hang dump is multi-GB. `TestResults/` is gitignored — delete it when done:

```bash
rm -rf tests/VisualRelay.Tests/TestResults
```
