using System.Reflection;
using System.Runtime.CompilerServices;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Reflection-based guard: production assemblies must not expose settable static
/// test seams.  After the harness-inject-seams-not-global-statics migration, every
/// test substitutes its doubles by passing them in (constructor / parameter / DI)
/// instead of mutating a process-global static.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    /// <summary>
    /// Escape hatch: add <c>"TypeFullName.MemberName"</c> entries here with a
    /// comment justifying why the settable static is unavoidable.  Expected empty
    /// after the injection-seams migration is complete.
    /// </summary>
    private static readonly string[] InjectionSeamAllowlist = [];

    /// <summary>
    /// Walk every type in the production assemblies (VisualRelay.Core and
    /// VisualRelay.App) and fail if any declares a settable public/internal
    /// static — a test-injection seam that must be deleted or converted to an
    /// injected dependency.
    /// </summary>
    [Fact]
    public void ProductionAssemblies_HaveNoSettableStaticTestSeams()
    {
        var assemblies = new[]
        {
            typeof(KeyEnvFile).Assembly,               // VisualRelay.Core
            typeof(MainWindowViewModel).Assembly,      // VisualRelay.App
        };

        var allowSet = new HashSet<string>(InjectionSeamAllowlist, StringComparer.Ordinal);
        var violations = new List<string>();

        // NOTE: deliberately omitting DeclaredOnly — in .NET 10 it
        // incorrectly hides most methods on static classes (abstract sealed),
        // returning only a single private method.  Static base types
        // (System.Object) carry no static members, so there are no false
        // positives from inheritance.
        var fieldFlags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static;

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                // Skip compiler-generated types (closures, lambdas, async state
                // machines, display classes, etc.).
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                    continue;
                if (type.Name.Contains('<'))
                    continue;

                // ── Static fields ──────────────────────────────────────
                foreach (var field in type.GetFields(fieldFlags))
                {
                    // Only public or internal (assembly) fields are seams.
                    if (field is { IsPublic: false, IsAssembly: false })
                        continue;
                    // Exclude readonly, const, [ThreadStatic], compiler-generated,
                    // and compiler-generated backing fields (names containing '<').
                    if (field.IsInitOnly || field.IsLiteral)
                        continue;
                    if (field.IsDefined(typeof(ThreadStaticAttribute), inherit: false))
                        continue;
                    if (field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                        continue;
                    if (field.Name.Contains('<'))
                        continue;

                    var key = $"{type.FullName}.{field.Name}";
                    if (allowSet.Contains(key))
                        continue;

                    violations.Add(key);
                }

                // ── Static property setters ─────────────────────────────
                // Use GetProperties with GetSetMethod(nonPublic:true) to
                // catch any setter (public or non-public) on a public/internal
                // static property.  Deliberately omit DeclaredOnly — in
                // .NET 10 it hides properties on static classes.
                foreach (var prop in type.GetProperties(fieldFlags))
                {
                    if (prop.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                        continue;
                    if (prop.Name.Contains('<'))
                        continue;
                    // Only public or internal properties.
                    var getter = prop.GetGetMethod(nonPublic: true);
                    var setter = prop.GetSetMethod(nonPublic: true);
                    var accessibility = getter ?? setter;
                    if (accessibility is null)
                        continue;
                    if (accessibility is { IsPublic: false, IsAssembly: false })
                        continue;
                    // Must have a setter (of any accessibility).
                    if (setter is null)
                        continue;

                    var key = $"{type.FullName}.{prop.Name}";
                    if (allowSet.Contains(key))
                        continue;

                    violations.Add(key);
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Production assemblies contain settable static test seams:\n" +
            string.Join("\n", violations.Select(v => $"  {v}")) +
            "\n\nThese static members exist only so tests can swap in a double, " +
            "causing cross-test races under xUnit parallel execution.  Inject the " +
            "dependency instead: pass an IEnvironmentAccessor to KeyEnvFile methods, " +
            "an IGitInvoker through RelayDriverDependencies / parameters, and delete " +
            "the settable statics.  See harness-inject-seams-not-global-statics.\n\n" +
            "If a settable static is genuinely unavoidable, add it to the " +
            "InjectionSeamAllowlist in SplitGuardVerificationTests.InjectionSeams.cs " +
            "with a comment justifying the exemption.");
    }

    /// <summary>
    /// Cheap string-scan tripwire: no test source file may assign to the three
    /// removed static seams.  Catches a re-introduction before the reflection
    /// guard above — the string literal is unambiguous and self-documenting.
    /// </summary>
    [Fact]
    public void NoTestFile_AssignsRemovedStaticSeam()
    {
        string[] forbidden =
        [
            "KeyEnvFile.EnvironmentAccessorOverride =",
            "GitInvoker.Override =",
            "GitCommitter.RawGitRunner =",
        ];

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")
                                  && !f.Contains("\\bin\\") && !f.Contains("\\obj\\")))
        {
            // This convention file itself contains the banned strings as
            // documentation — skip it so it cannot self-tripwire.
            if (Path.GetFileName(file) == "SplitGuardVerificationTests.InjectionSeams.cs")
                continue;

            var content = File.ReadAllText(file);
            foreach (var pattern in forbidden)
            {
                if (content.Contains(pattern, StringComparison.Ordinal))
                {
                    var lineNumber = FindLineNumber(content, pattern);
                    var relative = Path.GetRelativePath(RepoSetup.Root, file);
                    violations.Add(
                        $"{relative}:{lineNumber}: assigns '{pattern.TrimEnd('=').Trim()}' — " +
                        "this static test seam was removed. Inject the dependency instead. " +
                        "See harness-inject-seams-not-global-statics.");
                }
            }
        }

        Assert.Empty(violations);
    }

    private static int FindLineNumber(string content, string pattern)
    {
        var idx = content.IndexOf(pattern, StringComparison.Ordinal);
        if (idx < 0) return 0;
        // Count newlines up to the match position (1-based line number).
        var line = 1;
        for (int i = 0; i < idx; i++)
            if (content[i] == '\n')
                line++;
        return line;
    }
}
