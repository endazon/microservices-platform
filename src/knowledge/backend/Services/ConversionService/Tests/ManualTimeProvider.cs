namespace ConversionService.Tests;

// #1621: 試験だけが進める時計。`GetTimestamp` / `GetUtcNow` の両方を `Advance` で動かす
// （図のコード化の総枠は経過時間で、gRPC の期限は壁時計で測るため）。
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
}
