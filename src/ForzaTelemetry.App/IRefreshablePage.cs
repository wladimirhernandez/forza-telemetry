namespace ForzaTelemetry.App;

/// <summary>
/// A page whose values are expensive enough that it is refreshed only while it is the one on screen.
/// </summary>
/// <remarks>
/// The shell ticks ~20 times a second and always feeds the cheap pages (the dashboard, Performance, Laps: a few property writes
/// each, and some of them must keep saving results while hidden). A live chart, the track map or the dyno do real work per
/// refresh (copying thousands of samples, recomputing geometry), and nobody sees that work while another tab is open. Such a page
/// implements this interface and the shell calls <see cref="Refresh"/> only while the page is <c>CurrentPage</c>; on returning to
/// the tab the first tick brings it up to date, because it reads the engines' current state, not "what changed since last time".
/// </remarks>
public interface IRefreshablePage
{
    /// <summary>Pulls the current state of the engines into the page's bound values. Called on the UI thread.</summary>
    void Refresh();
}
