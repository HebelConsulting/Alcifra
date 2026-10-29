namespace Alcifra.Presentation;

/// <summary>
/// Whether anything that shows a secret is on screen (ADR 0964; Mobile ADR 0037, both native clients since ADR 0968):
/// COUNTED, so an entry and the edit form beside it each say so and the platform's protection (a secure window, a window
/// kept out of screen capture) lifts only when the last one went.
/// </summary>
public sealed class SecretScreens(Action<bool> apply)
{
    private readonly Action<bool> _apply = apply;
    private int _showing;

    public bool Protected => _showing > 0;

    /// <summary>A view showing secrets appeared; dispose the result when it goes.</summary>
    public IDisposable Showing()
    {
        if (_showing++ == 0)
        {
            _apply(true);
        }

        return new Release(this);
    }

    private sealed class Release(SecretScreens screens) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (--screens._showing == 0)
            {
                screens._apply(false);
            }
        }
    }
}
