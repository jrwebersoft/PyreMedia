namespace PyreMedia.Core.Books;

/// <summary>One line of text and where it sat on the page.</summary>
public readonly record struct PlacedLine(string Text, double X, double Y, double Width, double Height)
{
    public double Bottom => Y + Height;
    public double Right => X + Width;
    public double MiddleY => Y + (Height / 2);
}

/// <summary>Lines that belong together - one balloon, one caption box, one credit block.</summary>
public sealed record Block(IReadOnlyList<PlacedLine> Lines)
{
    public double Top => Lines.Min(l => l.Y);
    public double Left => Lines.Min(l => l.X);
    public double Bottom => Lines.Max(l => l.Bottom);

    public string Text => string.Join(' ', Lines.Select(l => l.Text.Trim()));
}

/// <summary>
/// Turning loose OCR lines into something that reads like a script.
///
/// The engine returns lines roughly top to bottom, which is not the same as
/// reading order and not the same as knowing which lines belong to each other.
/// Two balloons side by side come back interleaved, so a flat dump reads as
/// nonsense even when every word is right.
///
/// Lines are grouped into blocks by sitting close together and overlapping
/// horizontally, which is what the lines inside one balloon do. Blocks are then
/// ordered the way a page is read: down the page, and left to right among
/// blocks at the same height. Right-to-left languages are not handled, and it
/// is better to say so than to quietly get manga backwards.
/// </summary>
public static class PageScript
{
    /// <summary>
    /// Group lines into blocks.
    /// </summary>
    /// <param name="gap">
    /// How far apart two lines can sit and still belong to the same balloon, as
    /// a multiple of line height. Comic lettering is tightly leaded, so a gap of
    /// much more than a line's own height is a different balloon.
    /// </param>
    public static List<Block> Blocks(IReadOnlyList<PlacedLine> lines, double gap = 1.1)
    {
        if (lines.Count == 0) return [];

        // Down the page first, so a block is built from its own top downwards.
        var ordered = lines
            .OrderBy(l => l.Y)
            .ThenBy(l => l.X)
            .ToList();

        var blocks = new List<List<PlacedLine>>();

        foreach (var line in ordered)
        {
            var joined = false;

            foreach (var block in blocks)
            {
                var last = block[^1];

                // Close underneath, and sharing horizontal space with it. Both
                // are needed: close-underneath alone joins two balloons stacked
                // in different columns, and overlap alone joins the top and
                // bottom of a page.
                var near = line.Y - last.Bottom <= System.Math.Max(last.Height, line.Height) * gap
                           && line.Y >= last.Y;

                if (near && Overlaps(line, last)) { block.Add(line); joined = true; break; }
            }

            if (!joined) blocks.Add([line]);
        }

        // Reading order: by band down the page, then left to right inside a
        // band. Sorting purely by Y puts a block whose top is two pixels higher
        // ahead of the one to its left, which reads as jumping between panels.
        var height = lines.Average(l => l.Height);
        var band = System.Math.Max(height * 3, 1);

        return [.. blocks
            .Select(b => new Block(b))
            .OrderBy(b => System.Math.Floor(b.Top / band))
            .ThenBy(b => b.Left)];
    }

    /// <summary>Do two lines share horizontal space, allowing for ragged edges?</summary>
    private static bool Overlaps(PlacedLine a, PlacedLine b)
    {
        var left = System.Math.Max(a.X, b.X);
        var right = System.Math.Min(a.Right, b.Right);

        var shared = right - left;
        if (shared <= 0) return false;

        // Against the narrower of the two, so a one-word line under a long one
        // still counts as belonging to it.
        return shared >= System.Math.Min(a.Width, b.Width) * 0.35;
    }

    /// <summary>
    /// One page as script: each block on its own, numbered, in reading order.
    /// </summary>
    public static string Compose(IReadOnlyList<PlacedLine> lines)
    {
        var blocks = Blocks(lines);

        if (blocks.Count == 0) return "";

        var sb = new System.Text.StringBuilder();

        for (var i = 0; i < blocks.Count; i++)
        {
            sb.Append('[').Append(i + 1).Append("] ");

            // Wrapped to keep a long caption readable, indented so the numbers
            // stay findable down the left.
            var text = blocks[i].Text;

            sb.AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }
}
