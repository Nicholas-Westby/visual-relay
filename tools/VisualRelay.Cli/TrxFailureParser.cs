using System.Xml.Linq;

namespace VisualRelay.Cli;

/// <summary>
/// Extracts the names of failed tests from a VSTest TRX document. Replaces the
/// grep/sed pipeline that <c>test.sh</c> ran over the TRX on a failing run.
/// </summary>
public static class TrxFailureParser
{
    /// <summary>
    /// Returns the de-duplicated, ordinally-sorted set of <c>testName</c>s whose
    /// <c>outcome</c> is <c>Failed</c>. Returns an empty list for unparseable
    /// input so a missing/garbled TRX never throws on the failure path.
    /// </summary>
    public static IReadOnlyList<string> ExtractFailedTestNames(string trxContent)
    {
        if (string.IsNullOrWhiteSpace(trxContent))
            return [];

        XDocument doc;
        try
        {
            doc = XDocument.Parse(trxContent);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "UnitTestResult"
                && string.Equals((string?)e.Attribute("outcome"), "Failed", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("testName"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
