using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// PHP projects get PHPUnit candidates. Found on the Windows arm with FreshRSS/FreshRSS: nothing in
/// the detector knew composer.json, so bootstrap left the placeholder test command and the run was
/// configured by hand with vendor/bin/phpunit.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    [Fact]
    public void DetectCandidates_ComposerTestScriptAndPhpunitConfig_OfferTheScriptThenPhpunit()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "composer.json"), """{ "scripts": { "test": "phpunit", "lint": "phpcs" } }""");
        File.WriteAllText(Path.Combine(repo.Root, "phpunit.xml.dist"), "<phpunit/>");

        Assert.Equal(["composer test", "vendor/bin/phpunit"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    [Fact]
    public void DetectCandidates_ComposerPhpunitScript_OffersRunningThatScript()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "composer.json"),
            """{ "scripts": { "phpunit": "phpunit --bootstrap ./tests/bootstrap.php ./tests" } }""");

        Assert.Equal(["composer run-script phpunit"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    [Theory]
    [InlineData("phpunit.xml")]
    [InlineData("phpunit.dist.xml")]
    public void DetectCandidates_ComposerProjectWithAPhpunitConfig_OffersPhpunit(string config)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "composer.json"), """{ "name": "acme/demo" }""");
        File.WriteAllText(Path.Combine(repo.Root, config), "<phpunit/>");

        Assert.Equal(["vendor/bin/phpunit"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    [Fact]
    public void DetectCandidates_ComposerProjectWithNoTestSignal_OffersNoPhpCandidate()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "composer.json"), "{ not json");

        Assert.Empty(TestCommandDetector.DetectCandidates(repo.Root));
    }
}
