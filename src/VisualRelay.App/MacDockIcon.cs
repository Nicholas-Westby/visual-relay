using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace VisualRelay.App;

/// <summary>
/// Sets the macOS Dock tile to the brand artwork at runtime.
///
/// Avalonia 12.0.4 exposes no Dock-icon API, and the Dock tile is driven by the
/// running process — for <c>dotnet run</c> (dev) and the bare published exec the
/// process is not inside a .app bundle, so it shows the generic .NET icon. This
/// helper closes that gap by calling
/// <c>[[NSApplication sharedApplication] setApplicationIconImage: img]</c> via
/// the Objective-C runtime (<c>/usr/lib/libobjc.dylib</c>), where <c>img</c> is
/// an <see cref="System.Drawing"/>-free NSImage built from the committed brand
/// PNG asset (NSImage will not reliably read the .ico).
///
/// It is a complete no-op off macOS and is best-effort: any interop failure is
/// swallowed so it can never crash or block startup. Inside a proper bundle it
/// is harmless belt-and-suspenders.
/// </summary>
internal static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string BrandPngUri = "avares://VisualRelay.App/Assets/app-icon.png";

    /// <summary>
    /// Attempts to set the Dock tile to the brand artwork. Returns
    /// <see langword="true"/> only when the AppKit call was issued on macOS;
    /// returns <see langword="false"/> off macOS or on any failure (best-effort).
    /// Must be called after AppKit/Avalonia is initialized.
    /// </summary>
    public static bool TrySet()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            return SetIconFromBrandAsset();
        }
        catch
        {
            // Best-effort: never let Dock-icon interop crash or block startup.
            return false;
        }
    }

    private static bool SetIconFromBrandAsset()
    {
        var png = ReadBrandPng();
        if (png.Length == 0)
            return false;

        // NSData* data = [NSData dataWithBytes:length:]
        var nsData = ObjGetClass("NSData");
        if (nsData == IntPtr.Zero)
            return false;
        IntPtr data;
        var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
        try
        {
            data = SendMessage(nsData, Sel("dataWithBytes:length:"),
                handle.AddrOfPinnedObject(), (UIntPtr)png.Length);
        }
        finally
        {
            handle.Free();
        }

        if (data == IntPtr.Zero)
            return false;

        // NSImage* img = [[NSImage alloc] initWithData:data]
        var nsImage = ObjGetClass("NSImage");
        if (nsImage == IntPtr.Zero)
            return false;
        var alloc = SendMessage(nsImage, Sel("alloc"));
        if (alloc == IntPtr.Zero)
            return false;
        var image = SendMessage(alloc, Sel("initWithData:"), data);
        if (image == IntPtr.Zero)
            return false;

        // [[NSApplication sharedApplication] setApplicationIconImage: img]
        var nsApplication = ObjGetClass("NSApplication");
        if (nsApplication == IntPtr.Zero)
            return false;
        var app = SendMessage(nsApplication, Sel("sharedApplication"));
        if (app == IntPtr.Zero)
            return false;

        SendMessage(app, Sel("setApplicationIconImage:"), image);

        // Balance the alloc/initWithData: +1 retain. AppKit has already retained
        // (or copied) the image via setApplicationIconImage:, so the helper's own
        // reference is no longer needed.
        SendMessage(image, Sel("release"));

        return true;
    }

    private static byte[] ReadBrandPng()
    {
        using var stream = AssetLoader.Open(new Uri(BrandPngUri));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ── Objective-C runtime interop ──────────────────────────────────────

    private static IntPtr ObjGetClass(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport(ObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend overloads — one per call-site signature. The C ABI requires
    // the argument list to match the selector's signature exactly, so each shape
    // is declared with the precise EntryPoint.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector,
        IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage(IntPtr receiver, IntPtr selector,
        IntPtr arg1, UIntPtr arg2);
}
