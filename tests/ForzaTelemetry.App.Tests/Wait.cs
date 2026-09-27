namespace ForzaTelemetry.App.Tests;

/// <summary>
/// Polls <paramref name="condition"/> until it is true or a deadline passes, instead of a fixed
/// <c>Thread.Sleep</c>. This exists because <c>TelemetryService</c>'s receive loop runs on a
/// background <c>Task.Run</c> (see its remarks) — after a test publishes a packet, there is a real
/// (if usually tiny) delay before that loop actually observes and processes it. A bounded poll like
/// this asserts "it happened, eventually, within a sane timeout" without ever hanging a test
/// indefinitely if something is actually broken.
/// </summary>
internal static class Wait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            if (condition())
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return condition(); // One last check, in case it flipped true right at the deadline.
            }

            await Task.Delay(PollInterval);
        }
    }
}
