using System.Diagnostics;

namespace Nadfny.Core;

public sealed record OpProgress(string Id, string Phase, long Done, long Total, string? Current);

/// <summary>
/// Progress of one long-running action (a delete, a prune, a cleanup). Pushes at most ~7 updates a second;
/// Total = 0 means "no measurable total": the UI shows the phase and elapsed time instead of a percentage.
/// </summary>
public sealed class OpReporter
{
    private static readonly long Interval = Stopwatch.Frequency / 7;
    private readonly string _id;
    private readonly Action<OpProgress> _emit;
    private long _last;
    private long _done, _total;
    private volatile string _phase = "";
    private volatile string? _current;

    public OpReporter(string id, Action<OpProgress> emit)
    {
        _id = id;
        _emit = emit;
    }

    public static OpReporter None { get; } = new("", _ => { });

    public void Phase(string phase, long total = 0)
    {
        _phase = phase;
        Interlocked.Exchange(ref _done, 0);
        Interlocked.Exchange(ref _total, total);
        Flush();
    }

    public void Report(long done, string? current = null)
    {
        Interlocked.Exchange(ref _done, done);
        if (current is not null) _current = current;
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _last);
        if (now - last < Interval || Interlocked.CompareExchange(ref _last, now, last) != last) return;
        _emit(Snapshot());
    }

    public void Flush()
    {
        Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp());
        _emit(Snapshot());
    }

    private OpProgress Snapshot() => new(_id, _phase, Interlocked.Read(ref _done), Interlocked.Read(ref _total), _current);

    /// <summary>Runs a delete while streaming its byte count. The bar can never pass the total.</summary>
    public async Task DeleteWithProgressAsync(string phase, long expectedBytes, Action<DeleteCounter> delete, CancellationToken ct)
    {
        var counter = new DeleteCounter();
        Phase(phase, expectedBytes);
        var work = Task.Run(() => delete(counter), ct);
        while (!work.IsCompleted)
        {
            await Task.WhenAny(work, Task.Delay(150, ct));
            Report(expectedBytes > 0 ? Math.Min(counter.Bytes, expectedBytes) : counter.Bytes);
        }
        await work; // surface exceptions
        // Final value is the real count (never forced to 100%), clamped so an estimate can't overshoot the bar.
        Report(expectedBytes > 0 ? Math.Min(counter.Bytes, expectedBytes) : counter.Bytes);
        Flush();
    }
}
