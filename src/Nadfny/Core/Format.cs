using System.Globalization;

namespace Nadfny.Core;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Binary units with one decimal above MB, matching what Explorer shows.</summary>
    public static string Bytes(long bytes)
    {
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        var fmt = i >= 2 && v < 100 ? "0.0" : "0";
        return v.ToString(fmt, CultureInfo.InvariantCulture) + " " + Units[i];
    }
}
