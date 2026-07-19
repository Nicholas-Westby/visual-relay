# Commit-message evidence for test-speed changes

Any commit that changes how long tests take must carry, in its commit-message
body, exactly one bullet of this shape, blanks filled with real measurements:

- test time dropped from <before> to <after>, saving <delta> (<scope>)

How to fill it in:

- `<before>` / `<after>`: numbers you measured yourself, on this machine, with
  the same command, once right before starting and once right after finishing.
  Never copy them from a task description, an estimate, or a prediction.
- `<delta>`: the difference between the two.
- `<scope>`: what the numbers cover: `full-suite wall time`, `single test`, or
  `<TestClass> file total`.
- A finished example:
  `- test time dropped from 80s to 60s, saving 20s (full-suite wall time)`

Rules:

- The filled-in bullet belongs in the commit message body, never in a task
  file. A task file states the requirement; only the commit records numbers.
- Exactly one evidence bullet per commit. The commit hook allows at most 3
  body bullets of at most 20 words each, all `- ` hyphen bullets, no em
  dashes; keep the evidence bullet well inside those limits.
- If you cannot measure a real improvement, say so in your run summary and do
  not invent numbers. A commit with no measured effect gets no evidence bullet.
