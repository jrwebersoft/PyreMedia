namespace PyreMedia.App;

/// <summary>What a turn of the wheel over a stacked section should move.</summary>
public enum ScrollTarget
{
    /// <summary>Move the page, to bring the section into view or move past it.</summary>
    Page,

    /// <summary>Move the section's own contents.</summary>
    Inside
}

public readonly record struct ScrollMove(ScrollTarget Target, double By)
{
    public static readonly ScrollMove None = new(ScrollTarget.Page, 0);
}

/// <summary>
/// The rule the stacked layout scrolls by, kept apart from the window so it can
/// be reasoned about and tested without laying out a real one.
/// <para>
/// The wheel follows the pointer: whichever section it is over is the one that
/// moves. A section that isn't fully on screen is lined up first, so one turn of
/// the wheel over Match brings Match into place rather than nudging the page by
/// an arbitrary amount. Once it is in view the wheel scrolls inside it, and when
/// it has no further to go the page takes over - otherwise the pointer gets
/// stuck in a section it has already read to the end of.
/// </para>
/// </summary>
public static class SectionScroll
{
    /// <summary>Sub-pixel layout noise, not a real overhang.</summary>
    private const double Slack = 2;

    /// <param name="top">Section's top edge relative to the page viewport.</param>
    /// <param name="height">Section's height.</param>
    /// <param name="viewport">Visible height of the page.</param>
    /// <param name="innerOffset">How far the section is already scrolled inside.</param>
    /// <param name="innerScrollable">How far it can scroll inside, 0 if it can't.</param>
    /// <param name="delta">Wheel delta: positive is up, as WPF reports it.</param>
    public static ScrollMove Decide(
        double top, double height, double viewport,
        double innerOffset, double innerScrollable, double delta)
    {
        if (viewport <= 0 || delta == 0) return ScrollMove.None;

        // A section taller than the screen can never be fully on it, so for those
        // "in place" means its top is lined up and the rest is read by scrolling
        // inside. Demanding that it fit would leave it permanently off the bottom
        // and the wheel doing nothing at all.
        var tallerThanScreen = height > viewport;

        var inPlace = tallerThanScreen
            ? top <= Slack
            : top >= -Slack && top + height <= viewport + Slack;

        if (!inPlace)
        {
            // Show its bottom if the whole thing fits; otherwise line its top up.
            var by = tallerThanScreen || top < -Slack ? top : top + height - viewport;
            return new ScrollMove(ScrollTarget.Page, by);
        }

        // In place, so scroll within it - unless it has nowhere left to go.
        if (innerScrollable > Slack)
        {
            var goingUp = delta > 0;
            var atTop = innerOffset <= Slack;
            var atEnd = innerOffset >= innerScrollable - Slack;

            if (!(goingUp && atTop) && !(!goingUp && atEnd))
                return new ScrollMove(ScrollTarget.Inside, -delta);
        }

        // Nothing left inside: move the page on to the next section.
        return new ScrollMove(ScrollTarget.Page, -delta);
    }

    /// <summary>
    /// The move to hand a section's own scroller, in the units it speaks.
    ///
    /// ScrollToVerticalOffset takes the scroller's units, and anything built on
    /// ItemsControl counts items rather than pixels unless told otherwise -
    /// CanContentScroll defaults to true. Handing one the pixel move
    /// <see cref="Decide"/> asks for moved the library list 120 rows a notch,
    /// which on a library of ten items is "jump to the end".
    /// </summary>
    /// <param name="byItem">Whether the scroller counts items rather than pixels.</param>
    /// <param name="by">The pixel move <see cref="Decide"/> asked for.</param>
    /// <param name="linesPerNotch">What Windows says a notch is worth.</param>
    public static double InnerStep(bool byItem, double by, int linesPerNotch) =>
        byItem ? System.Math.Sign(by) * System.Math.Max(1, linesPerNotch) : by;
}

