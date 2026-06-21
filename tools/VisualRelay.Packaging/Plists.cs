using System.Xml;

namespace VisualRelay.Packaging;

/// <summary>
/// Immutable bag of values written into the macOS <c>Info.plist</c>.
/// </summary>
/// <param name="BundleName"><c>CFBundleName</c> — short name of the bundle.</param>
/// <param name="BundleDisplayName"><c>CFBundleDisplayName</c> — user-visible name.</param>
/// <param name="BundleIdentifier"><c>CFBundleIdentifier</c> — reverse-DNS id.</param>
/// <param name="ExecutableName"><c>CFBundleExecutable</c> — inner binary name.</param>
/// <param name="IconFileName"><c>CFBundleIconFile</c> — icon name without extension.</param>
/// <param name="PackageType"><c>CFBundlePackageType</c> — four-character code.</param>
/// <param name="ShortVersionString"><c>CFBundleShortVersionString</c> — release version.</param>
/// <param name="BundleVersion"><c>CFBundleVersion</c> — build version.</param>
/// <param name="MinMacOsVersion"><c>LSMinimumSystemVersion</c> — minimum macOS version.</param>
public record PlistInfo(
    string BundleName,
    string BundleDisplayName,
    string BundleIdentifier,
    string ExecutableName,
    string IconFileName,
    string PackageType,
    string ShortVersionString,
    string BundleVersion,
    string MinMacOsVersion);

/// <summary>
/// macOS <c>Info.plist</c> writer with the same identifiers, version knobs, and
/// env-var overrides as the original <c>build-app-bundle.sh</c>.
/// </summary>
public static class Plists
{
    /// <summary>
    /// Resolves the plist values from environment variables, falling back to
    /// the same defaults as the bash script.
    /// </summary>
    /// <param name="exeName">Inner executable name (default <c>VisualRelay.App</c>).</param>
    /// <param name="getEnv">Optional env-var reader (defaults to <see cref="Environment.GetEnvironmentVariable(string?)"/>).</param>
    public static PlistInfo ResolveInfo(string exeName, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var version = getEnv("VISUAL_RELAY_VERSION") ?? "0.1.0";
        var bundleVersion = getEnv("VISUAL_RELAY_BUNDLE_VERSION") ?? version;
        var minMacOs = getEnv("VISUAL_RELAY_MIN_MACOS") ?? "11.0";

        return new PlistInfo(
            BundleName: "Visual Relay",
            BundleDisplayName: "Visual Relay",
            BundleIdentifier: "org.minify.VisualRelay",
            ExecutableName: exeName,
            IconFileName: "VisualRelay",
            PackageType: "APPL",
            ShortVersionString: version,
            BundleVersion: bundleVersion,
            MinMacOsVersion: minMacOs);
    }

    /// <summary>
    /// Writes a valid macOS <c>Info.plist</c> to <paramref name="path"/>.
    /// </summary>
    public static void Write(string path, PlistInfo info)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var writer = XmlWriter.Create(path, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = System.Text.Encoding.UTF8,
            OmitXmlDeclaration = false,
        });

        // <?xml version="1.0" encoding="UTF-8"?>
        // <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        writer.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN",
            "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);

        writer.WriteStartElement("plist");
        writer.WriteAttributeString("version", "1.0");
        writer.WriteStartElement("dict");

        WriteKeyString(writer, "CFBundleName", info.BundleName);
        WriteKeyString(writer, "CFBundleDisplayName", info.BundleDisplayName);
        WriteKeyString(writer, "CFBundleIdentifier", info.BundleIdentifier);
        WriteKeyString(writer, "CFBundleExecutable", info.ExecutableName);
        WriteKeyString(writer, "CFBundleIconFile", info.IconFileName);
        WriteKeyString(writer, "CFBundlePackageType", info.PackageType);
        WriteKeyString(writer, "CFBundleShortVersionString", info.ShortVersionString);
        WriteKeyString(writer, "CFBundleVersion", info.BundleVersion);

        // NSHighResolutionCapable — self-closing <true/>
        writer.WriteElementString("key", "NSHighResolutionCapable");
        writer.WriteElementString("true", null);

        WriteKeyString(writer, "LSMinimumSystemVersion", info.MinMacOsVersion);

        writer.WriteEndElement(); // </dict>
        writer.WriteEndElement(); // </plist>
    }

    private static void WriteKeyString(XmlWriter writer, string key, string value)
    {
        writer.WriteElementString("key", key);
        writer.WriteElementString("string", value);
    }
}
