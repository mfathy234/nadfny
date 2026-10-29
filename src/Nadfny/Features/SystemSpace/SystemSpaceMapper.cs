using System.Globalization;
using System.Text.Json;

namespace Nadfny.Features.SystemSpace;

public sealed record HibernationInfo(string Path, long? Bytes, bool? Enabled, string FileType);
public sealed record PagingDrive(string Drive, long? PagefileBytes, long? SwapfileBytes);
public sealed record ShadowStorage(string Drive, string StorageDrive, ulong? Used, ulong? Maximum)
{
    public bool MaximumUnlimited => Maximum == ulong.MaxValue;
}
public sealed record SystemSpaceSnapshot(HibernationInfo Hibernation, IReadOnlyList<PagingDrive> Paging,
    bool? AutomaticManaged, IReadOnlyList<ShadowStorage> Storage, bool ShadowReadable, int? RestorePoints);
public sealed record HibernationResult(bool Cancelled, long Freed, SystemSpaceSnapshot Info);
internal sealed record CimSpace(bool? AutomaticManaged, IReadOnlyList<ShadowStorage> Storage, bool ShadowReadable, int? RestorePoints);

internal static class SystemSpaceMapper
{
    internal static HibernationInfo Hibernation(string path, long? bytes, object? enabled, object? type) =>
        new(path, bytes, enabled is int e && e is 0 or 1 ? e == 1 : null,
            type is int t ? t switch { 1 => "reduced", 2 => "full", _ => "unknown" } : "unknown");

    internal static CimSpace Cim(JsonElement root)
    {
        bool? automatic = Get(root, "automatic").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        var readable = Get(root, "shadowReadable").ValueKind == JsonValueKind.True;
        var storage = Get(root, "storage");
        var rows = readable && storage.ValueKind == JsonValueKind.Array
            ? storage.EnumerateArray().Select(s => new ShadowStorage(
                Label(Get(s, "drive")), Label(Get(s, "storageDrive")),
                Number(Get(s, "used")), Number(Get(s, "maximum")))).ToArray() : [];
        int? count = Get(root, "restoreReadable").ValueKind == JsonValueKind.True &&
            Get(root, "restorePoints").TryGetInt32Safe(out var n) && n >= 0 ? n : null;
        return new(automatic, rows, readable, count);
    }

    private static JsonElement Get(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    private static string Label(JsonElement e) => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString()! : "Unknown volume";
    private static ulong? Number(JsonElement e) => ulong.TryParse(e.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static bool TryGetInt32Safe(this JsonElement e, out int n)
    {
        n = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out n);
    }
}
