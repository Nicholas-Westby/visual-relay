using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A snapshot of a checkout imports its own copy of an editable Python project. Measured on
/// openai-agents-python: the .venv overlaid into the verify snapshot kept uv's .pth line naming
/// the checkout's src, so the snapshot's run imported the checkout, and 35 tests that check
/// traceback frames lie under their own src/agents failed there while passing in the checkout.
/// </summary>
public sealed class PythonEditableImportsTests : IDisposable
{
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "vr-pyimports-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_checkout);

    [Fact]
    public void AnAbsoluteEntryIntoTheCheckout_IsReRootedAtTheSnapshot()
    {
        WriteVenv(".venv", "_editable_impl_agents.pth", "/work/repo/src\n");

        var roots = PythonEditableImports.SnapshotRoots(_checkout, [".venv"], "/work/repo", "/tmp/snap");

        Assert.Equal(["/tmp/snap/src"], roots);
    }

    [Fact]
    public void EveryEditableEntryOfEveryVirtualenv_IsReRootedOnceInImportOrder()
    {
        WriteVenv(".venv", "b.pth", "/work/repo/packages/b/src\r\n/work/repo/packages/a/src\r\n");
        WriteVenv(".venv", "a.pth", "/work/repo/packages/a/src\n");
        WriteVenv("tools/lint/.venv", "lint.pth", "/work/repo/tools/lint\n");

        var roots = PythonEditableImports.SnapshotRoots(
            _checkout, [".venv", "tools/lint/.venv"], "/work/repo/", "/tmp/snap");

        Assert.Equal(["/tmp/snap/packages/a/src", "/tmp/snap/packages/b/src", "/tmp/snap/tools/lint"], roots);
    }

    [Theory]
    [InlineData("../../../../src\n")]
    [InlineData("/work/repo-fork/src\n")]
    [InlineData("/opt/shared/lib\n")]
    [InlineData("import _virtualenv\n")]
    [InlineData("# /work/repo/src\n")]
    public void AnEntryThatDoesNotNameTheCheckout_IsLeftAsItIs(string content)
    {
        WriteVenv(".venv", "entry.pth", content);

        Assert.Empty(PythonEditableImports.SnapshotRoots(_checkout, [".venv"], "/work/repo", "/tmp/snap"));
    }

    [Fact]
    public void AnIgnoredDirectoryThatIsNotAVirtualenv_IsNotRead()
    {
        WriteVenv("vendor", "entry.pth", "/work/repo/src\n");
        File.Delete(Path.Combine(_checkout, "vendor", "pyvenv.cfg"));

        Assert.Empty(PythonEditableImports.SnapshotRoots(_checkout, ["vendor"], "/work/repo", "/tmp/snap"));
    }

    [Fact]
    public void TheSearchPaths_PutTheRootsOnPythonPathInOrder()
    {
        var searchPaths = PythonEditableImports.SearchPaths(["/tmp/snap/src", "/tmp/snap/tools"]);

        Assert.Equal("/tmp/snap/src:/tmp/snap/tools", searchPaths["PYTHONPATH"]);
        Assert.Empty(PythonEditableImports.SearchPaths([]));
    }

    private void WriteVenv(string name, string pthName, string content)
    {
        var venv = Path.Combine(_checkout, name);
        var sitePackages = Path.Combine(venv, "lib", "python3.14", "site-packages");
        Directory.CreateDirectory(sitePackages);
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = /usr/bin\n");
        File.WriteAllText(Path.Combine(sitePackages, pthName), content);
    }
}
