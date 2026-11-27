using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>The volume cache behind /api/state (macOS / Linux AudioControl) with a fake reader.</summary>
public class AudioCacheTests
{
    [Fact]
    public void SnapshotReturnsAtOnceAndFillsInTheBackground()
    {
        using var gate = new ManualResetEventSlim(false);
        var audio = new AudioControl(() => { gate.Wait(TimeSpan.FromSeconds(10)); return new AudioSnapshot(42, false); });

        Assert.Null(audio.Snapshot()); // nothing read yet, and the call did not wait for the reader
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => !audio.Refreshing, TimeSpan.FromSeconds(10)));
        Assert.Equal(42, audio.Snapshot()?.Volume);
    }

    [Theory]
    [InlineData(true, true, 2)]   // macOS, read in-process: refreshed after 900 ms
    [InlineData(true, false, 1)]  // macOS, read by starting osascript: kept for 10 s, not re-read every second
    [InlineData(true, null, 1)]   // macOS, no reading at all (osascript said "missing value"): tried again after 10 s
    [InlineData(false, null, 2)]  // Linux, no sink for a moment: retried after 900 ms as before
    public void OnMacOnlyAControllableOutputIsReadEverySecond(bool mac, bool? canControl, int expectedReads)
    {
        var reads = 0;
        var audio = new AudioControl(() =>
        {
            Interlocked.Increment(ref reads);
            return canControl is { } can ? new AudioSnapshot(42, false, can) : null;
        }, slowReadsForUncontrollable: mac);

        audio.Snapshot();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 1 && !audio.Refreshing, TimeSpan.FromSeconds(10)));
        Thread.Sleep(1100); // past the 900 ms of a controllable output, well inside the 10 s of the other kind
        audio.Snapshot();
        Assert.True(SpinWait.SpinUntil(() => !audio.Refreshing, TimeSpan.FromSeconds(10)));

        Assert.Equal(expectedReads, Volatile.Read(ref reads));
    }

    [Fact]
    public void AnOlderBackgroundReadCannotReplaceANewerCurrentReading()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var audio = new AudioControl(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // the background read starts first and is slow: it saw the old volume
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return new AudioSnapshot(20, false);
            }
            return new AudioSnapshot(60, false); // a control action reads later and sees the new volume
        });

        audio.Snapshot();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(60, audio.Current()?.Volume);

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !audio.Refreshing, TimeSpan.FromSeconds(10)));
        Assert.Equal(60, audio.Snapshot()?.Volume);
        Assert.Equal(60, audio.Current()?.Volume); // still fresh, and still the newer reading
    }
}
