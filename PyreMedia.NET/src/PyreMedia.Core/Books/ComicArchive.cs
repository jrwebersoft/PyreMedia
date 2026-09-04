using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;

namespace PyreMedia.Core.Books;

/// <summary>What a comic file actually turned out to be, whatever it is called.</summary>
public enum ArchiveKind
{
    /// <summary>Not an archive this can read.</summary>
    Unknown,

    Zip,
    Rar
}

/// <summary>One thing inside a comic.</summary>
public sealed record ArchiveEntry(string Name, long Length);

/// <summary>
/// Opening a comic, whatever it was packed with.
///
/// A .cbz is a zip and a .cbr is a RAR, and the difference is the whole reason
/// a fifth of a library can be invisible. Measured against a real one: 8,342 of
/// 37,425 comics were .cbr, every one genuinely RAR, none solid, none encrypted
/// - and none of them could be opened at all.
///
/// The kind is decided by the first few bytes rather than by the extension.
/// Files are misnamed often enough to matter, and a header is cheap and cannot
/// be wrong about what it is.
///
/// Reading only. RAR is not written here and never will be: rewriting a comic
/// means removing pages, and doing that to a format this cannot safely rebuild
/// would risk the file to save an extension.
/// </summary>
public static class ComicArchive
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    /// <summary>What this file is, from its first bytes.</summary>
    public static ArchiveKind Kind(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            Span<byte> head = stackalloc byte[8];

            if (stream.Read(head) < 8) return ArchiveKind.Unknown;

            if (head[..4].SequenceEqual(ZipMagic)) return ArchiveKind.Zip;
            if (head[..6].SequenceEqual(RarMagic)) return ArchiveKind.Rar;

            return ArchiveKind.Unknown;
        }
        catch (Exception)
        {
            return ArchiveKind.Unknown;
        }
    }

    /// <summary>
    /// True where pages can be taken out or marks written in.
    ///
    /// Zip only. Everything else can be read and looked through, and the reader
    /// says so rather than offering a button that would fail.
    /// </summary>
    public static bool CanEdit(string path) => Kind(path) == ArchiveKind.Zip;

    /// <summary>Everything inside, folders excluded.</summary>
    public static List<ArchiveEntry> Entries(string path)
    {
        switch (Kind(path))
        {
            case ArchiveKind.Zip:
            {
                using var zip = ZipFile.OpenRead(path);

                return [.. zip.Entries
                    .Where(e => !e.FullName.EndsWith('/'))
                    .Select(e => new ArchiveEntry(e.FullName, e.Length))];
            }

            case ArchiveKind.Rar:
            {
                using var rar = RarArchive.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());

                return [.. rar.Entries
                    .Where(e => !e.IsDirectory && e.Key is not null)
                    .Select(e => new ArchiveEntry(Normalise(e.Key!), e.Size))];
            }

            default:
                return [];
        }
    }

    /// <summary>One entry's bytes, or null where it is not there.</summary>
    public static byte[]? Read(string path, string entry)
    {
        switch (Kind(path))
        {
            case ArchiveKind.Zip:
            {
                using var zip = ZipFile.OpenRead(path);

                var found = zip.GetEntry(entry)
                            ?? zip.Entries.FirstOrDefault(e =>
                                   string.Equals(e.FullName, entry, StringComparison.OrdinalIgnoreCase));

                if (found is null) return null;

                using var stream = found.Open();

                return Drain(stream, found.Length);
            }

            case ArchiveKind.Rar:
            {
                using var rar = RarArchive.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());

                var found = rar.Entries.FirstOrDefault(e =>
                    e.Key is not null
                    && string.Equals(Normalise(e.Key), entry, StringComparison.OrdinalIgnoreCase));

                if (found is null) return null;

                using var stream = found.OpenEntryStream();

                return Drain(stream, found.Size);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// RAR writes backslashes on Windows-made archives and forward slashes
    /// elsewhere. One form, so an entry name means the same thing whichever
    /// machine packed it.
    /// </summary>
    private static string Normalise(string key) => key.Replace('\\', '/');

    /// <summary>
    /// A RAR entry's stream cannot be seeked and does not always know its own
    /// length, so it is read until it stops rather than in one sized go.
    /// </summary>
    private static byte[] Drain(Stream stream, long expected)
    {
        using var memory = expected is > 0 and < int.MaxValue
            ? new MemoryStream((int)expected)
            : new MemoryStream();

        stream.CopyTo(memory);

        return memory.ToArray();
    }
}
