namespace TinyWin.IsoBuilder.Tests;

/// <summary>Locates the checked-in golden files and captured tool output.</summary>
internal static class TestFiles
{
    public static string ReadGolden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>
    /// Reads a golden file as an ordered list of values, dropping comments and blank lines. Golden
    /// files carry their rationale inline so a future edit has to read why the value is what it is.
    /// </summary>
    public static IReadOnlyList<string> ReadGoldenLines(string name) =>
    [
        .. ReadGolden(name)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')),
    ];

    /// <summary>A scratch directory that removes itself.</summary>
    public static TempDirectory NewTempDirectory() => new();
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tinywin-isotest-" + Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string WriteFile(string relativePath, long length)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);

        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
