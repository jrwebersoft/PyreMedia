using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PyreMedia.Core.Books;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PyreMedia.App.Text;

/// <summary>
/// Reading the words off a comic page.
///
/// Windows has an OCR engine built in and has since Windows 10, so this needs
/// nothing installed, reaches no network, and sends no page anywhere. That last
/// part is why it was chosen over the cloud services that would read comic
/// lettering better: the alternative is uploading somebody's library a page at
/// a time.
///
/// It is not accurate on comics and is not meant to be. Comic lettering is
/// drawn, not typeset - a capital I has serifs that read as a slash, and O/0,
/// S/5 and T/7 swap constantly. Measured on a real issue: 23 pages in 2.8
/// seconds, and of the ten characters named in that issue's own summary, nine
/// could be found in the result. Good enough to find an issue, not good enough
/// to read one, which is exactly what it is offered as.
/// </summary>
public static class PageOcr
{
    /// <summary>
    /// True where Windows can do this at all. False on an install with no
    /// recognition language, which is rare but not impossible.
    /// </summary>
    public static bool Available => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    /// <summary>Which languages this machine can read, for saying so.</summary>
    public static string Languages =>
        string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));

    /// <summary>
    /// Every page of a comic, read.
    /// </summary>
    /// <summary>What came of reading a comic, including what did not.</summary>
    /// <param name="Laid">
    /// The same pages as placed lines, kept so a script can be built from them.
    /// Position is what turns a heap of lines into balloons in reading order,
    /// and it is gone the moment the text is flattened.
    /// </param>
    public sealed record Reading(
        List<TextPage> Pages,
        int Unreadable,
        string? LastTrouble,
        List<List<PlacedLine>> Laid)
    {
        public int WithWords => Pages.Count(p => !p.Empty);

        /// <summary>
        /// True when nothing worked at all, which is a different thing from a
        /// comic with no words in it and wants saying differently.
        /// </summary>
        public bool Broken => Pages.Count > 0 && Unreadable == Pages.Count;
    }

    /// <param name="onPage">Called after each page, for a progress bar.</param>
    public static async Task<Reading> ReadAsync(
        string comic,
        System.Action<int, int>? onPage = null,
        CancellationToken ct = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new System.InvalidOperationException(
                "Windows has no OCR language installed, so pages cannot be read.");

        var pages = ComicPages.Read(comic);
        var read = new List<TextPage>(pages.Count);
        var laid = new List<List<PlacedLine>>(pages.Count);

        var unreadable = 0;
        string? trouble = null;

        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var bytes = ComicPages.Page(comic, pages[i].Entry);

            if (bytes is null)
            {
                unreadable++;
                trouble ??= "the page could not be taken out of the archive";
                read.Add(new TextPage(i + 1, ""));
                laid.Add([]);
            }
            else
            {
                var (lines, why) = await LinesIn(engine, bytes, ct);

                if (why is not null) { unreadable++; trouble ??= why; }

                laid.Add(lines);

                // Written as a script rather than a heap: the engine returns
                // lines roughly top to bottom, which interleaves two balloons
                // sitting side by side and reads as nonsense.
                read.Add(new TextPage(i + 1, PageScript.Compose(lines)));
            }

            onPage?.Invoke(i + 1, pages.Count);
        }

        return new Reading(read, unreadable, trouble, laid);
    }

    /// <summary>
    /// The words on one page, and why there are none if there are none.
    ///
    /// The reason is carried out rather than swallowed. A page that will not
    /// decode should not end the job - but every page failing looks exactly
    /// like a comic with no words in it, and those want different answers.
    /// </summary>
    private static async Task<(List<PlacedLine> Lines, string? Trouble)> LinesIn(
        OcrEngine engine, byte[] image, CancellationToken ct)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();

            // Deliberately not disposed. The wrapper owns the stream it wraps,
            // so closing it closes the one the decoder is about to read - which
            // fails silently and returns an empty page for every page in the
            // comic.
            var writer = stream.AsStreamForWrite();

            await writer.WriteAsync(image, ct);
            await writer.FlushAsync(ct);

            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

            // The engine refuses anything larger, and a comic scan can exceed
            // it. Scaled rather than skipped, because a page that is too big is
            // usually the best-quality page in the file.
            var scale = System.Math.Min(
                1.0,
                (double)OcrEngine.MaxImageDimension
                    / System.Math.Max(decoder.PixelWidth, decoder.PixelHeight));

            using var bitmap = scale < 1.0
                ? await decoder.GetSoftwareBitmapAsync(
                      BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                      new BitmapTransform
                      {
                          ScaledWidth = (uint)(decoder.PixelWidth * scale),
                          ScaledHeight = (uint)(decoder.PixelHeight * scale)
                      },
                      ExifOrientationMode.RespectExifOrientation,
                      ColorManagementMode.DoNotColorManage).AsTask(ct)
                : await decoder.GetSoftwareBitmapAsync().AsTask(ct);

            var result = await engine.RecognizeAsync(bitmap).AsTask(ct);

            var lines = new List<PlacedLine>(result.Lines.Count);

            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;

                // A line has no rectangle of its own, only its words do, so its
                // extent is the box around them.
                var left = line.Words.Min(w => w.BoundingRect.Left);
                var top = line.Words.Min(w => w.BoundingRect.Top);
                var right = line.Words.Max(w => w.BoundingRect.Right);
                var bottom = line.Words.Max(w => w.BoundingRect.Bottom);

                lines.Add(new PlacedLine(line.Text, left, top, right - left, bottom - top));
            }

            return (lines, null);
        }
        catch (System.OperationCanceledException) { throw; }
        catch (System.Exception ex)
        {
            // A page that will not decode is a blank page in the result rather
            // than the end of the job - a comic with one bad image should still
            // be searchable on its other twenty-two. The reason goes back with
            // it so twenty-three bad pages can be reported as a fault.
            return ([], ex.Message);
        }
    }
}
