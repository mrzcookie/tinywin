using System.Runtime.CompilerServices;
using System.Text;

namespace TinyWin.Tests.Unattend;

/// <summary>
/// Compares generated <c>autounattend.xml</c> against the checked-in expected files in
/// <c>tests/TinyWin.Tests/Golden/Unattend</c>.
/// </summary>
/// <remarks>
/// <para>
/// To regenerate every golden file after an intentional change:
/// </para>
/// <code>
/// $env:TINYWIN_UPDATE_GOLDEN = '1'; dotnet test; Remove-Item Env:\TINYWIN_UPDATE_GOLDEN
/// </code>
/// <para>
/// Then read the resulting <c>git diff</c> line by line. That diff is the review — an unattend
/// file that is wrong in a subtle way still installs, right up until it doesn't.
/// </para>
/// <para>
/// Files are written to the source tree rather than the build output, and are compared with line
/// endings normalised, because <c>.gitattributes</c> checks <c>*.xml</c> out as LF while the
/// generator deliberately emits CRLF. Line endings are asserted separately in
/// <see cref="UnattendGeneratorTests"/>.
/// </para>
/// </remarks>
internal static class GoldenXml
{
    internal const string UpdateVariable = "TINYWIN_UPDATE_GOLDEN";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static bool UpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true" or "TRUE";

    internal static string DirectoryPath { get; } = ResolveDirectory();

    internal static string PathFor(string caseName) => Path.Combine(DirectoryPath, caseName + ".xml");

    internal static void Verify(string caseName, string actual)
    {
        var path = PathFor(caseName);

        if (UpdateRequested)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path, Canonical(actual) + "\n", Utf8NoBom);
            return;
        }

        if (!File.Exists(path))
        {
            Assert.Fail(
                $"No golden file for case '{caseName}' at {path}. "
                + $"Set {UpdateVariable}=1 and re-run the tests to create it, then review the diff.");
        }

        var expected = Canonical(File.ReadAllText(path));

        Assert.Equal(expected, Canonical(actual));
    }

    private static string Canonical(string xml) => xml
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal)
        .TrimEnd('\n');

    // The default argument is filled in by the compiler with the path of this file, which sits one
    // directory below the golden files' parent.
    private static string ResolveDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFilePath)!, "..", "Golden", "Unattend"));
}
