using System.Globalization;

namespace TinyWin.App.Services;

/// <summary>Size and duration formatting, kept in one place so every page reads the same.</summary>
public static class Formatting
{
    public static string Bytes(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024;
        const double Mb = 1024d * 1024;

        return bytes >= Gb
            ? (bytes / Gb).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : (bytes / Mb).ToString("0", CultureInfo.CurrentCulture) + " MB";
    }

    public static string Megabytes(long megabytes) =>
        megabytes >= 1024
            ? (megabytes / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : megabytes.ToString("0", CultureInfo.CurrentCulture) + " MB";

    public static string Duration(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
