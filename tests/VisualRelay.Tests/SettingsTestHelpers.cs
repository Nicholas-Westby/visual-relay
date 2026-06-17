using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Shared headless-UI helpers for the settings/key tests. Kept in one place so
/// the per-test-class files stay under the source-size guard and so the
/// "open settings via the cog" mechanics live in a single spot after the move
/// from a flyout to a modal <see cref="SettingsWindow"/>.
/// </summary>
internal static class SettingsTestHelpers
{
    private static Button FindButton(Control root, string name)
    {
        var btn = root.FindControl<Button>(name);
        Assert.NotNull(btn);
        return btn;
    }

    private static void Click(Control target, TopLevel root)
    {
        var c = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        var pt = target.TranslatePoint(c, root) ?? c;
        root.MouseDown(pt, MouseButton.Left);
        root.MouseUp(pt, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static TopBar GetTopBar(Visual window) =>
        window.GetVisualDescendants().OfType<TopBar>().Single();

    /// <summary>
    /// Clicks the Settings cog on <paramref name="window"/> and returns the modal
    /// <see cref="SettingsWindow"/> it opens (an owned window rather than a flyout
    /// in the main window's visual tree). Callers should <see cref="Window.Close()"/>
    /// it to avoid leaking an open dialog into the shared headless dispatcher.
    /// </summary>
    public static SettingsWindow OpenSettings(MainWindow window)
    {
        Click(FindButton(GetTopBar(window), "SettingsButton"), window);
        return window.OwnedWindows.OfType<SettingsWindow>().Single();
    }

    /// <summary>
    /// The layout (content) scroll regions under <paramref name="root"/>, i.e.
    /// ScrollViewers that are NOT the internal scroll part of a TextBox template.
    /// The settings dialog must have exactly one of these — the old flyout added
    /// a second (FlyoutPresenter) layout scroll that clipped "Live Tiers".
    /// </summary>
    public static List<ScrollViewer> LayoutScrollViewers(Visual root) =>
        root.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .Where(sv => sv.GetVisualAncestors().OfType<TextBox>().FirstOrDefault() is null)
            .ToList();

    /// <summary>Clears XDG_CONFIG_HOME from the fake accessor.</summary>
    public static void EnsureNoUserEnv(DictionaryEnvironmentAccessor env) =>
        env["XDG_CONFIG_HOME"] = null;

    /// <summary>
    /// Writes a valid <c>.relay/config.json</c> with the given
    /// <paramref name="commitProofArtifacts"/> value (or omits the key when null)
    /// so the loader treats it as Loaded.
    /// </summary>
    public static void WriteCommitConfig(TestRepository repo, bool? commitProofArtifacts)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var json = new JsonObject
        {
            ["testCmd"] = "dotnet test",
            ["logSources"] = new JsonArray()
        };
        if (commitProofArtifacts is { } value)
        {
            json["commitProofArtifacts"] = value;
        }
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        File.WriteAllText(
            configPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// Seeds <paramref name="env"/> with XDG_CONFIG_HOME pointing to
    /// <paramref name="repo"/>.Root, writes <paramref name="content"/> to the
    /// <c>.env</c> file under <c>visual-relay/</c>, and returns a disposable that
    /// clears XDG_CONFIG_HOME from the accessor.
    /// </summary>
    public static IDisposable SeedUserEnv(
        DictionaryEnvironmentAccessor env, TestRepository repo, string content)
    {
        var dir = Path.Combine(repo.Root, "visual-relay");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".env"), content);
        env["XDG_CONFIG_HOME"] = repo.Root;
        return new EnvVarRestore("XDG_CONFIG_HOME", env);
    }

    private sealed class EnvVarRestore(string name, DictionaryEnvironmentAccessor env) : IDisposable
    {
        public void Dispose() => env[name] = null;
    }
}
