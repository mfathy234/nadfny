using System.Diagnostics;

namespace Nadfny.Core;

public sealed record ScanProgressSnapshot(string Scope, string Phase, int Done, int Total, long Folders, string? Current);

/// <summary>Thread-safe progress counter that pushes at most ~8 updates a second to the UI.</summary>
public sealed class ScanProgress
{
    private static readonly long IntervalTicks = Stopwatch.Frequency / 8;

    private readonly string _scope;
    private readonly Action<ScanProgressSnapshot> _emit;
    private long _lastEmit;
    private int _done, _total;
    private long _folders;
    private volatile string _phase = "";
    private volatile string? _current;

    public ScanProgress(string scope, Action<ScanProgressSnapshot> emit)
    {
        _scope = scope;
        _emit = emit;
    }

    public static ScanProgress None { get; } = new("none", _ => { });

    public void SetPhase(string phase, int total = 0)
    {
        _phase = phase;
        Interlocked.Exchange(ref _done, 0);
        Interlocked.Exchange(ref _total, total);
        Flush();
    }

    public void AddTotal(int n) => Interlocked.Add(ref _total, n);

    public void CompleteOne(string? current = null)
    {
        Interlocked.Increment(ref _done);
        if (current is not null) _current = current;
        MaybeEmit();
    }

    public void Visit(string folder)
    {
        Interlocked.Increment(ref _folders);
        _current = folder;
        MaybeEmit();
    }

    public void Flush()
    {
        Interlocked.Exchange(ref _lastEmit, Stopwatch.GetTimestamp());
        _emit(Snapshot());
    }

    public ScanProgressSnapshot Snapshot() =>
        new(_scope, _phase, Volatile.Read(ref _done), Volatile.Read(ref _total), Interlocked.Read(ref _folders), _current);

    private void MaybeEmit()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastEmit);
        if (now - last < IntervalTicks) return;
        if (Interlocked.CompareExchange(ref _lastEmit, now, last) != last) return; // another thread is emitting
        _emit(Snapshot());
    }
}
