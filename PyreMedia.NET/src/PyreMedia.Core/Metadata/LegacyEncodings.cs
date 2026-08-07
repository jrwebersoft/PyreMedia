using System.Text;

namespace PyreMedia.Core.Metadata;

/// <summary>
/// .NET only knows UTF-8, UTF-16 and ASCII out of the box. Plenty of .nfo files
/// in the wild declare <c>windows-1252</c> or <c>iso-8859-1</c> - older scrapers
/// and anything written by a Windows tool - and Kodi reads them happily. Without
/// this, parsing one fails outright with "System does not support 'windows-1252'
/// encoding" and the file looks corrupt when it isn't.
/// </summary>
internal static class LegacyEncodings
{
    private static bool _registered;

    /// <summary>
    /// Call before parsing anything that might carry a legacy declaration.
    /// Registering twice would be harmless anyway, so no locking.
    /// </summary>
    internal static void Ensure()
    {
        if (_registered) return;
        _registered = true;

        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* nothing to do about it; UTF-8 files still work */ }
    }
}
