using System.Net;
using System.Net.Sockets;
using System.Text;
using Nadfny.Core;
using Nadfny.Features.Updates;

namespace Nadfny.Tests;

/// <summary>The updater's downloader against a small local HTTP server that can drop, stall or ignore ranges.</summary>
public sealed class ResumableDownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nadfny-dl-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Content = Enumerable.Range(0, 400_000).Select(i => (byte)(i * 31 % 251)).ToArray();

    public ResumableDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) SafeDeleter.DeletePermanently(_dir);
    }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task ResumesAfterTheConnectionDrops()
    {
        using var server = new TestServer(Content) { DropFirstAfter = 150_000 };
        var target = Path.Combine(_dir, "setup.exe");

        var part = await ResumableDownload.RunAsync(Http, server.Url, target, Content.Length, OpReporter.None, CancellationToken.None, retryDelay: TimeSpan.FromMilliseconds(10));

        Assert.Equal(Content, await File.ReadAllBytesAsync(part));
        Assert.Equal(2, server.Requests.Count);
        Assert.Null(server.Requests[0]);
        Assert.Equal(150_000, server.Requests[1]); // asked only for the rest
    }

    [Fact]
    public async Task ContinuesAPartialDownloadFromAnEarlierRun()
    {
        using var server = new TestServer(Content);
        var target = Path.Combine(_dir, "setup.exe");
        await File.WriteAllBytesAsync(ResumableDownload.PartPath(target), Content[..100_000]);

        var part = await ResumableDownload.RunAsync(Http, server.Url, target, Content.Length, OpReporter.None, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(part));
        Assert.Equal([100_000L], server.Requests);
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        using var server = new TestServer(Content) { IgnoreRanges = true };
        var target = Path.Combine(_dir, "setup.exe");
        await File.WriteAllBytesAsync(ResumableDownload.PartPath(target), Content[..100_000]);

        var part = await ResumableDownload.RunAsync(Http, server.Url, target, Content.Length, OpReporter.None, CancellationToken.None);

        Assert.Equal(Content, await File.ReadAllBytesAsync(part));
    }

    [Fact]
    public async Task GivesUpWhenNoDataArrivesAndKeepsWhatItHas()
    {
        using var server = new TestServer(Content) { StallAfter = 50_000 };
        var target = Path.Combine(_dir, "setup.exe");

        await Assert.ThrowsAsync<TimeoutException>(() => ResumableDownload.RunAsync(Http, server.Url, target, Content.Length, OpReporter.None,
            CancellationToken.None, idleTimeout: TimeSpan.FromMilliseconds(300), retryDelay: TimeSpan.FromMilliseconds(10)));

        // Every retry resumed instead of starting over, and the bytes so far stay for the next try.
        Assert.Equal(5, server.Requests.Count);
        Assert.True(new FileInfo(ResumableDownload.PartPath(target)).Length >= 50_000);
    }

    [Fact]
    public void RemoveOtherDownloads_KeepsOnlyThisVersion()
    {
        var target = Path.Combine(_dir, "Nadfny-0.4.0-setup.exe");
        foreach (var name in new[] { "Nadfny-0.3.0-setup.exe", "Nadfny-0.3.0-setup.exe.part", "Nadfny-0.4.0-setup.exe", "Nadfny-0.4.0-setup.exe.part" })
            File.WriteAllText(Path.Combine(_dir, name), "x");

        UpdateService.RemoveOtherDownloads(_dir, target);

        Assert.Equal(["Nadfny-0.4.0-setup.exe", "Nadfny-0.4.0-setup.exe.part"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray());
    }

    /// <summary>Minimal HTTP/1.1 file server on a loopback port; records each request's Range start (null = none).</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _content;
        private readonly CancellationTokenSource _stop = new();
        private int _served;

        public long? DropFirstAfter { get; init; }
        public long? StallAfter { get; init; }
        public bool IgnoreRanges { get; init; }
        public List<long?> Requests { get; } = [];
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/setup.exe";

        public TestServer(byte[] content)
        {
            _content = content;
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                long? from = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        from = long.Parse(line["Range: bytes=".Length..].TrimEnd('-'));
                }
                int request;
                lock (Requests) { Requests.Add(from); request = ++_served; }

                var start = IgnoreRanges ? 0 : from ?? 0;
                var partial = start > 0;
                var header = partial
                    ? $"HTTP/1.1 206 Partial Content\r\nContent-Length: {_content.Length - start}\r\nContent-Range: bytes {start}-{_content.Length - 1}/{_content.Length}\r\nConnection: close\r\n\r\n"
                    : $"HTTP/1.1 200 OK\r\nContent-Length: {_content.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));

                var end = _content.Length;
                if (request == 1 && DropFirstAfter is { } drop) end = (int)drop;
                if (StallAfter is { } stall) end = (int)Math.Max(start, Math.Min(end, stall + start));
                try
                {
                    await stream.WriteAsync(_content.AsMemory((int)start, end - (int)start));
                    await stream.FlushAsync();
                    if (StallAfter is not null) await Task.Delay(Timeout.Infinite, _stop.Token); // hold the connection open, send nothing
                }
                catch (Exception) { /* client went away */ }
                // Returning closes the socket: for DropFirstAfter that is a connection cut mid-body.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
