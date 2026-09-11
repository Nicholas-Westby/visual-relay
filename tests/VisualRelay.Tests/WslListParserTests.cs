using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// <c>wsl -l -v</c> output parsing. The columns are located from the header
/// line's positions, never from the English words, so a localized Windows (whose
/// STATE values can contain spaces) parses the same; the default marker is the
/// leading <c>*</c>; and the text is tolerated whether it arrived as UTF-8
/// (<c>WSL_UTF8=1</c>) or was mis-decoded from wsl.exe's default UTF-16.
/// </summary>
public sealed class WslListParserTests
{
    private const string English =
        "  NAME              STATE           VERSION\n" +
        "* Ubuntu            Running         2\n" +
        "  Debian            Stopped         1\n" +
        "  docker-desktop    Running         2\n";

    [Fact]
    public void Parse_EnglishSample_ReadsNameStateVersionAndTheDefaultMarker()
    {
        var distros = WslListParser.Parse(English);

        Assert.Equal(3, distros.Count);
        Assert.Equal(new WslDistro("Ubuntu", 2, true, "Running"), distros[0]);
        Assert.Equal(new WslDistro("Debian", 1, false, "Stopped"), distros[1]);
        Assert.Equal(new WslDistro("docker-desktop", 2, false, "Running"), distros[2]);
    }

    [Fact]
    public void Parse_LocalizedHeaderAndAStateWithASpace_UsesColumnPositionsNotWords()
    {
        const string german =
            "  NAME      STATUS           VERSION\n" +
            "* Ubuntu    Wird ausgeführt  2\n" +
            "  Debian    Beendet          1\n";

        var distros = WslListParser.Parse(german);

        Assert.Equal(2, distros.Count);
        Assert.Equal(new WslDistro("Ubuntu", 2, true, "Wird ausgeführt"), distros[0]);
        Assert.Equal(new WslDistro("Debian", 1, false, "Beendet"), distros[1]);
    }

    [Fact]
    public void Parse_CrLf_LeavesNoCarriageReturnInAnyField()
    {
        var distros = WslListParser.Parse(English.Replace("\n", "\r\n"));

        Assert.Equal(3, distros.Count);
        Assert.DoesNotContain(distros, d => d.Name.Contains('\r') || d.State.Contains('\r'));
        Assert.Equal(new WslDistro("docker-desktop", 2, false, "Running"), distros[2]);
    }

    [Fact]
    public void Parse_Utf16MisdecodedAsUtf8_DoesNotThrowAndRecoversTheAsciiRows()
    {
        // Without WSL_UTF8=1 wsl.exe writes UTF-16LE: read as UTF-8, every ASCII
        // character is followed by a NUL and a BOM turns into replacement characters.
        var misdecoded = "��" + string.Concat(English.Select(c => c + "\0"));

        var distros = WslListParser.Parse(misdecoded);

        Assert.Equal(["Ubuntu", "Debian", "docker-desktop"], distros.Select(d => d.Name));
        Assert.True(distros[0].IsDefault);
        Assert.Equal(2, distros[0].Version);
        Assert.Equal(1, distros[1].Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("Windows Subsystem for Linux has no installed distributions.\n\n" +
                "Use 'wsl.exe --list --online' to list available distributions\n" +
                "and 'wsl.exe --install <Distro>' to install.\n")]
    public void Parse_NoDistros_ReturnsEmptyRatherThanThrowing(string text)
    {
        Assert.Empty(WslListParser.Parse(text));
    }

    [Fact]
    public void Parse_RowWithoutAnIntegerVersion_IsSkipped()
    {
        const string text =
            "  NAME      STATE     VERSION\n" +
            "* Ubuntu    Running   2\n" +
            "  Broken    Running   x\n";

        var distros = WslListParser.Parse(text);

        Assert.Single(distros);
        Assert.Equal("Ubuntu", distros[0].Name);
    }

    [Fact]
    public void Parse_NoDefaultMarker_ReportsNoDefault()
    {
        const string text =
            "  NAME      STATE     VERSION\n" +
            "  Ubuntu    Running   2\n";

        var distros = WslListParser.Parse(text);

        Assert.Single(distros);
        Assert.False(distros[0].IsDefault);
    }
}
