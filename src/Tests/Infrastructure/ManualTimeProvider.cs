namespace Sungaila.NewDark.Tests.Infrastructure;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
}