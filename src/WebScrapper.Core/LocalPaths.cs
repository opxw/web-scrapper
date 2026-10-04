using System.Security.Cryptography;
using System.Text;

namespace WebScrapper.Core;

public static class LocalPaths
{
    public static string ForUri(Uri uri, string? mediaType)
    {
        var host = Safe(uri.IdnHost + (uri.IsDefaultPort ? "" : "_" + uri.Port));
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Safe).ToList();
        if (segments.Count == 0 || uri.AbsolutePath.EndsWith('/')) segments.Add("index.html");
        else if (string.IsNullOrEmpty(Path.GetExtension(segments[^1])))
        {
            if (mediaType is "text/html" or "application/xhtml+xml") segments.Add("index.html");
            else segments[^1] += mediaType switch { "text/css" => ".css", "application/javascript" or "text/javascript" => ".js", _ => ".bin" };
        }
        var originalExtension = Path.GetExtension(segments[^1]);
        if (mediaType is "text/html" or "application/xhtml+xml" && !new[] { ".html", ".htm", ".xhtml" }.Contains(originalExtension, StringComparer.OrdinalIgnoreCase)) segments[^1] += ".html";
        if (mediaType == "text/css" && !originalExtension.Equals(".css", StringComparison.OrdinalIgnoreCase)) segments[^1] += ".css";
        if (mediaType is "application/javascript" or "text/javascript" or "application/x-javascript" or "application/ecmascript" or "text/ecmascript"
            && !new[] { ".js", ".mjs", ".cjs" }.Contains(originalExtension, StringComparer.OrdinalIgnoreCase)) segments[^1] += ".js";
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var ext = Path.GetExtension(segments[^1]);
            segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]) + "__q_" + Hash(uri.Query) + ext;
        }
        // A URL with .html and one without an extension can otherwise map to the same index.html.
        // All files have an identity suffix, also preventing case-insensitive Windows collisions.
        var extension = Path.GetExtension(segments[^1]);
        segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]) + "__u_" + Hash(uri.GetLeftPart(UriPartial.Path)) + extension;
        return Path.Combine([host, .. segments]);
    }

    public static string UnderRoot(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path di luar folder hasil.");
        // Refuse symlinks/junctions in the output ancestry.
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Folder hasil tidak boleh melalui symlink/junction.");
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("File hasil berupa symlink.");
        return full;
    }

    private static string Safe(string segment)
    {
        // Keep escaped separators escaped; never turn URL-encoded slash into a directory separator.
        var clean = new string(segment.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c < 32 ? '_' : c).ToArray()).TrimEnd('.', ' ');
        var stem = clean.Split('.')[0];
        if (clean.Length == 0 || clean is "." or ".." || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) clean = "_" + clean;
        if (clean != segment || clean.Length > 80) clean = clean[..Math.Min(clean.Length, 60)] + "_" + Hash(segment);
        return clean;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
