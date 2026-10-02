namespace Mediator.Tests
{
    /// <summary>
    /// Waits for background work instead of sleeping a fixed time. A fixed sleep fails randomly when the machine is busy
    /// (for example when several test projects run in parallel) and teaches people to ignore red builds; waiting for the
    /// condition with a generous timeout keeps the same assertions without the timing dependency.
    /// </summary>
    internal static class Eventually
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Polls until <paramref name="condition"/> is true or the timeout elapses. Callers assert afterwards, so a
        /// timeout produces the normal assertion failure message.
        /// </summary>
        public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }
}
