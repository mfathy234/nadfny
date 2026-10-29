using System.Net;
using System.Net.Http.Headers;
using Nadfny.Core;

namespace Nadfny.Features.Updates;

/// <summary>
/// Downloads one file into "&lt;target&gt;.part", resuming from what is already there (HTTP Range) after a dropped
/// connection or an app restart. There is no total time limit, since a 45 MB installer on a slow line can take an
/// hour; a download only fails when no data arrives for <see cref="IdleTimeout"/>, after a few resumed retries.
/// </summary>
internal static class ResumableDownload
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private const int Attempts = 5;

    public static string PartPath(string target) => target + ".part";

    /// <summary>Fills the .part file up to <paramref name="size"/> bytes and returns its path.</summary>
    public static async Task<string> RunAsync(HttpClient http, string url, string target, long size, OpReporter op, CancellationToken ct,
        TimeSpan? idleTimeout = null, TimeSpan? retryDelay = null)
    {
        var part = PartPath(target);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ContinueAsync(http, url, part, size, op, idleTimeout ?? IdleTimeout, ct);
                return part;
            }
            catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested
                && ex is HttpRequestException or IOException or TimeoutException)
            {
                // Keep what arrived; the next attempt asks only for the rest.
                op.Phase($"Connection dropped, resuming (try {attempt + 1} of {Attempts})", size);
                op.Report(Have(part));
                await Task.Delay((retryDelay ?? TimeSpan.FromSeconds(2)) * attempt, ct);
            }
        }
    }

    private static long Have(string part) => File.Exists(part) ? new FileInfo(part).Length : 0;

    private static async Task ContinueAsync(HttpClient http, string url, string part, long size, OpReporter op, TimeSpan idle, CancellationToken ct)
    {
        var have = Have(part);
        if (size > 0 && have > size) { File.Delete(part); have = 0; }
        if (size > 0 && have == size) return;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(idle);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); throw new IOException("The partial download no longer matches; starting again."); }
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range: start over

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            var buffer = new byte[81920];
            op.Report(have);
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(idle); // any data resets the stall timer
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                have += read;
                op.Report(have);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No data arrived for {idle.TotalSeconds:0} seconds.");
        }
        if (size > 0 && Have(part) < size) throw new IOException("The connection closed before the download finished.");
    }
}
