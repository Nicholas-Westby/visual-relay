using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// The detector reads a tracked-file list, never the filesystem, so every case
/// here is a synthetic <c>git ls-files</c> output.
/// </summary>
public sealed class TestLayoutDetectorTests
{
    private static TestLayoutDetection Detect(params string[] trackedPaths) =>
        TestLayoutDetector.Detect(trackedPaths, _ => null);

    private static TestLayoutDetection DetectReading(
        IReadOnlyDictionary<string, string> heads, params string[] trackedPaths) =>
        TestLayoutDetector.Detect(trackedPaths, heads.GetValueOrDefault);

    [Fact]
    public void Rust_only_is_inline_capable()
    {
        var detection = Detect("Cargo.toml", "src/lib.rs", "src/control.rs", "README.md");

        Assert.Equal(["rust"], detection.DetectedLanguages);
        Assert.Equal([".rs"], detection.InlineTestExtensions);
    }

    [Fact]
    public void Go_only_keeps_the_path_gate()
    {
        var detection = Detect("go.mod", "go.sum", "mux.go", "route.go", "tree.go");

        Assert.Equal(["go"], detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
    }

    [Fact]
    public void Rust_plus_python_marks_only_rust_inline()
    {
        var detection = Detect(
            "Cargo.toml", "src/lib.rs", "src/color.rs",
            "pyproject.toml", "pkg/a.py", "pkg/b.py", "pkg/c.py", "pkg/d.py", "pkg/e.py");

        // Ordered by counted files descending, then id.
        Assert.Equal(["python", "rust"], detection.DetectedLanguages);
        Assert.Equal([".rs"], detection.InlineTestExtensions);
    }

    [Fact]
    public void Markup_and_style_only_detect_nothing()
    {
        var detection = Detect("index.html", "about.html", "css/site.css", "css/print.css");

        Assert.Empty(detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
    }

    [Fact]
    public void A_manifest_three_directories_deep_still_counts()
    {
        var detection = Detect("packages/core/Cargo.toml", "packages/core/src/lib.rs");

        Assert.Equal(["rust"], detection.DetectedLanguages);
        Assert.Equal([".rs"], detection.InlineTestExtensions);
    }

    [Fact]
    public void A_manifest_deeper_than_three_directories_does_not_count()
    {
        var detection = Detect("a/b/c/d/Cargo.toml", "a/b/c/d/src/lib.rs");

        Assert.Empty(detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
    }

    [Fact]
    public void Vendored_and_generated_paths_are_ignored()
    {
        var detection = Detect(
            "go.mod", "mux.go", "route.go", "tree.go",
            "vendor/x/Cargo.toml", "vendor/x/src/lib.rs", "vendor/x/src/map.rs",
            "node_modules/pkg/index.js", "node_modules/pkg/util.js", "node_modules/pkg/api.js",
            "api/api.pb.go", "gen/model_pb2.py", "Cargo.lock", "package-lock.json");

        Assert.Equal(["go"], detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
        Assert.False(detection.CountsByExtension.ContainsKey(".js"));
    }

    [Fact]
    public void One_sample_file_in_a_python_repo_stays_below_the_floor()
    {
        var detection = Detect(
            "pyproject.toml", "src/click/core.py", "src/click/types.py", "src/click/utils.py",
            "docs/sample.rs");

        Assert.Equal(["python"], detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
        Assert.Equal(1, detection.CountsByExtension[".rs"]);
    }

    [Fact]
    public void Two_inline_files_without_a_manifest_reach_the_floor()
    {
        var detection = Detect("src/lib.rs", "src/color.rs", "README.md");

        Assert.Equal(["rust"], detection.DetectedLanguages);
        Assert.Equal([".rs"], detection.InlineTestExtensions);
    }

    [Fact]
    public void A_separate_language_needs_three_files_or_a_two_percent_share()
    {
        var pages = Enumerable.Range(0, 110).Select(i => $"docs/page{i}.md").ToArray();

        // 2 of 112 counted files is under two percent, and under three files.
        var thin = Detect([.. pages, "tools/one.rb", "tools/two.rb"]);
        Assert.DoesNotContain("ruby", thin.DetectedLanguages);

        // 2 of 50 is exactly two percent.
        var atShare = Detect([.. pages.Take(48), "tools/one.rb", "tools/two.rb"]);
        Assert.Contains("ruby", atShare.DetectedLanguages);

        // Three files clear the floor whatever the share.
        var threeFiles = Detect([.. pages, "tools/one.rb", "tools/two.rb", "tools/three.rb"]);
        Assert.Contains("ruby", threeFiles.DetectedLanguages);
    }

    [Fact]
    public void A_perl_repo_without_prolog_directives_is_not_inline_capable()
    {
        var detection = DetectReading(
            new Dictionary<string, string> { ["bin/tool.pl"] = "#!/usr/bin/perl\nuse strict;\n" },
            "cpanfile", "lib/Foo.pm", "lib/Bar.pm", "bin/tool.pl");

        Assert.Equal(["perl"], detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
    }

    [Fact]
    public void Prolog_directives_and_the_pack_marker_detect_prolog()
    {
        var detection = DetectReading(
            new Dictionary<string, string>
            {
                ["prolog/lists.pl"] = ":- module(lists, []).\n",
                ["prolog/sets.pl"] = "\n\n:- begin_tests(sets).\n",
            },
            "pack.pl", "prolog/lists.pl", "prolog/sets.pl");

        Assert.Contains("prolog", detection.DetectedLanguages);
        Assert.DoesNotContain("perl", detection.DetectedLanguages);
        Assert.Equal([".p", ".pl", ".plt", ".pro"], detection.InlineTestExtensions);
    }

    [Fact]
    public void A_pro_file_without_prolog_directives_is_skipped()
    {
        var detection = Detect("app/build.pro", "app/other.pro");

        Assert.Empty(detection.DetectedLanguages);
        Assert.Equal(0, detection.CountedFiles);
    }

    [Fact]
    public void A_depfile_beside_its_object_is_not_the_d_language()
    {
        var detection = Detect(
            "go.mod", "mux.go", "route.go", "tree.go",
            "src/foo.d", "src/foo.o", "src/bar.d", "src/bar.o");

        Assert.Equal(["go"], detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
        Assert.False(detection.CountsByExtension.ContainsKey(".d"));
    }

    [Fact]
    public void D_sources_without_objects_are_inline_capable()
    {
        var detection = Detect("src/foo.d", "src/bar.d");

        Assert.Equal(["d"], detection.DetectedLanguages);
        Assert.Equal([".d", ".di"], detection.InlineTestExtensions);
    }

    [Fact]
    public void Counted_files_skip_names_without_a_suffix()
    {
        var detection = Detect("Cargo.toml", "src/lib.rs", "LICENSE", "Makefile", ".gitignore");

        Assert.Equal(2, detection.CountedFiles);
        Assert.Equal(1, detection.CountsByExtension[".rs"]);
        Assert.Equal(1, detection.CountsByExtension[".toml"]);
    }

    [Fact]
    public void An_empty_tracked_list_detects_nothing()
    {
        var detection = Detect();

        Assert.Empty(detection.DetectedLanguages);
        Assert.Empty(detection.InlineTestExtensions);
        Assert.Empty(detection.CountsByExtension);
        Assert.Equal(0, detection.CountedFiles);
    }

    [Fact]
    public void An_eclipse_project_file_does_not_report_smalltalk()
    {
        var detection = Detect(".project", "pom.xml", "src/main/java/A.java", "src/main/java/B.java");

        Assert.DoesNotContain("smalltalk", detection.DetectedLanguages);
        Assert.Contains("java", detection.DetectedLanguages);
    }
}
