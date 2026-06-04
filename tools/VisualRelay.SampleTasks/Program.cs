var target = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine("..", "sample-tasks"));

ResetDirectory(Path.Combine(target, ".relay"));
ResetDirectory(Path.Combine(target, "llm-tasks"));
ResetDirectory(Path.Combine(target, "logs"));
ResetDirectory(Path.Combine(target, "src"));
ResetDirectory(Path.Combine(target, "tests"));
ResetDirectory(Path.Combine(target, "__pycache__"));
ResetDirectory(Path.Combine(target, ".swival"));
DeleteFile(Path.Combine(target, ".DS_Store"));

Write(".gitignore", ".DS_Store\n.swival/\n.relay/*/stage*-attempt*/\n.relay/*/*.report.json\n.relay/*/*.log\n__pycache__/\n*.pyc\n");
Write(
    "README.md",
    """
    # Sample Relay Tasks

    This is a tiny Python project for exercising Visual Relay. Open this folder in the app and run one pending task at a time.

    ```bash
    python3 -m unittest discover -s tests
    ```

    The tasks are deliberately small so an LLM agent can add tests, make a focused code change, verify, and commit.

    Reset this repository back to three pending tasks after a run:

    ```bash
    ./scripts/reset-sample.sh
    ```
    """);
Write(
    "scripts/reset-sample.sh",
    """
    #!/usr/bin/env bash
    set -euo pipefail

    repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    visual_relay_root="${VISUAL_RELAY_ROOT:-}"

    if [[ -z "$visual_relay_root" ]]; then
      visual_relay_root="$(cd "$repo_root/../visual-relay" 2>/dev/null && pwd || true)"
    fi

    if [[ ! -x "$visual_relay_root/visual-relay" ]]; then
      echo "Set VISUAL_RELAY_ROOT to the Visual Relay checkout, or keep it beside sample-tasks." >&2
      exit 1
    fi

    (cd "$visual_relay_root" && ./visual-relay sample-reset "$repo_root")

    if git -C "$repo_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
      git -C "$repo_root" add -A
      if git -C "$repo_root" diff --cached --quiet; then
        echo "sample-tasks already matches the reset state."
      else
        git -C "$repo_root" commit -m "chore: reset sample relay project"
      fi
    fi

    echo "sample-tasks reset to three pending tasks."
    """);
MakeExecutable("scripts/reset-sample.sh");
Write(
    ".relay/config.json",
    """
    {
      "testCmd": "python3 -m unittest discover -s tests",
      "testFileCmd": "python3 -m unittest {files}",
      "logSources": ["logs/app.log"],
      "tierProfiles": {
        "cheap": "cheap",
        "balanced": "balanced",
        "frontier": "frontier",
        "vision": "vision"
      },
      "baselineVerify": true,
      "archiveOnDone": true,
      "maxTurns": 24
    }
    """);
Write("logs/app.log", "12:04:41 sample project ready\n");
Write(
    "src/calculator.py",
    """
    def add(left: int, right: int) -> int:
        return left + right


    def subtract(left: int, right: int) -> int:
        return left - right


    def divide(left: int, right: int) -> float:
        if right == 0:
            raise ValueError("right must not be zero")
        return left / right
    """);
Write(
    "src/text_tools.py",
    """
    def slugify(value: str) -> str:
        return value.strip().lower().replace(" ", "-")


    def title_case(value: str) -> str:
        return " ".join(word.capitalize() for word in value.split())
    """);
Write(
    "src/todo.py",
    """
    def summarize(items: list[str]) -> str:
        if not items:
            return "No tasks"
        return f"{len(items)} tasks"
    """);
Write(
    "tests/test_calculator.py",
    """
    import unittest

    from src.calculator import add, divide, subtract


    class CalculatorTests(unittest.TestCase):
        def test_add(self) -> None:
            self.assertEqual(add(2, 3), 5)

        def test_subtract(self) -> None:
            self.assertEqual(subtract(7, 4), 3)

        def test_divide(self) -> None:
            self.assertEqual(divide(8, 2), 4)

        def test_divide_by_zero(self) -> None:
            with self.assertRaises(ValueError):
                divide(1, 0)


    if __name__ == "__main__":
        unittest.main()
    """);
Write(
    "tests/test_text_tools.py",
    """
    import unittest

    from src.text_tools import slugify, title_case
    from src.todo import summarize


    class TextToolsTests(unittest.TestCase):
        def test_slugify_basic_spaces(self) -> None:
            self.assertEqual(slugify("Hello World"), "hello-world")

        def test_title_case(self) -> None:
            self.assertEqual(title_case("visual relay"), "Visual Relay")

        def test_summarize_empty(self) -> None:
            self.assertEqual(summarize([]), "No tasks")

        def test_summarize_count(self) -> None:
            self.assertEqual(summarize(["one", "two"]), "2 tasks")


    if __name__ == "__main__":
        unittest.main()
    """);
Write(
    "llm-tasks/add-multiply.md",
    """
    batch: 1

    # Add multiplication support

    Add a `multiply(left: int, right: int) -> int` function to `src/calculator.py`.

    Requirements:
    - Write a failing unittest first.
    - Keep the existing calculator tests green.
    - Update only the calculator module and its tests.
    """);
Write(
    "llm-tasks/improve-slugify.md",
    """
    batch: 1

    # Improve slug generation

    Make `slugify` in `src/text_tools.py` suitable for titles with punctuation and repeated whitespace.

    Expected behavior:
    - `"Hello, Visual Relay!"` becomes `"hello-visual-relay"`.
    - Multiple spaces collapse to one dash.
    - Leading and trailing punctuation do not create leading or trailing dashes.

    Add focused tests before changing the implementation.
    """);
Write(
    "llm-tasks/nested-todo-summary/nested-todo-summary.md",
    """
    batch: 1

    # Improve todo summary wording

    Update `src/todo.py` so `summarize(["ship"])` returns `"1 task"` while `summarize(["a", "b"])` still returns `"2 tasks"`.

    The adjacent JSON file shows the wording examples to preserve.
    """);
Write(
    "llm-tasks/nested-todo-summary/examples.json",
    """
    {
      "empty": "No tasks",
      "one": "1 task",
      "many": "2 tasks"
    }
    """);

Console.WriteLine(target);

void Write(string relativePath, string content)
{
    var path = Path.Combine(target, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content.TrimEnd() + Environment.NewLine);
}

void MakeExecutable(string relativePath)
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    var path = Path.Combine(target, relativePath);
    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
}

static void ResetDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }
}

static void DeleteFile(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}
