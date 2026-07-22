using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TinyWin.Imaging.Dism;

/// <summary>
/// Turns the raw character stream from <c>dism.exe</c>'s stdout into complete lines plus progress
/// percentages.
/// </summary>
/// <remarks>
/// <para>This exists because DISM's progress bar is <b>not</b> a sequence of lines. It renders
/// <c>[==========            20.0%                          ]</c> by rewriting a single line, and
/// the spike could not determine which mechanism it uses when stdout is a pipe rather than a
/// console (docs/spikes/dism-backend.md §5). So this reader handles all three possibilities:</para>
/// <list type="number">
///   <item><b>Backspaces.</b> <c>\b</c> erases the last buffered character, so the buffer always
///     holds what the console would be showing.</item>
///   <item><b>Bare carriage returns.</b> <c>\r</c> ends the current line the same way <c>\n</c>
///     does, so a bar redrawn with <c>\r</c> is seen once per redraw.</item>
///   <item><b>No bar at all.</b> Nothing special is needed — <see cref="SawPercentage"/> simply
///     stays false, and the caller degrades to reporting stage transitions instead of sitting at
///     zero. That flag is the whole point: silence must be distinguishable from 0%.</item>
/// </list>
/// <para>Because a percentage is recognised the moment its <c>%</c> arrives, progress is reported
/// even when the bar is never terminated by any newline at all.</para>
/// <para>Pure and synchronous by design — <see cref="Append(ReadOnlySpan{char})"/> is a state
/// machine over characters, which is what makes it unit-testable without elevation or a process.</para>
/// </remarks>
public sealed partial class DismOutputReader
{
    private readonly Action<string> _onLine;
    private readonly Action<double>? _onProgress;
    private readonly StringBuilder _buffer = new();

    private double _lastReportedPercent = -1;

    public DismOutputReader(Action<string> onLine, Action<double>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        _onLine = onLine;
        _onProgress = onProgress;
    }

    /// <summary>
    /// True once a real percentage has been seen. False means DISM told us nothing about progress,
    /// which callers must report as indeterminate rather than as no progress.
    /// </summary>
    public bool SawPercentage { get; private set; }

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '\b':
                    if (_buffer.Length > 0)
                    {
                        _buffer.Length--;
                    }

                    break;

                case '\r':
                case '\n':
                    Flush();
                    break;

                case '\0':
                    // Padding DISM occasionally emits around the bar. Never part of a value.
                    break;

                default:
                    _buffer.Append(c);
                    if (c == '%')
                    {
                        TryReportProgress(_buffer);
                    }

                    break;
            }
        }
    }

    /// <summary>Emits whatever is still buffered. Call once the process has exited.</summary>
    public void Complete() => Flush();

    private void Flush()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var line = _buffer.ToString();
        _buffer.Clear();

        // A finished progress bar is not output anyone wants to parse or log; it is a repaint.
        if (IsProgressBar(line))
        {
            TryReportProgress(line);
            return;
        }

        if (!string.IsNullOrWhiteSpace(line))
        {
            _onLine(line.TrimEnd());
        }
    }

    private void TryReportProgress(StringBuilder buffer) => TryReportProgress(buffer.ToString());

    private void TryReportProgress(string text)
    {
        if (!TryParsePercentage(text, out var fraction))
        {
            return;
        }

        SawPercentage = true;

        // DISM repaints the same value many times over. Reporting every repaint would spam the UI
        // thread for no added information.
        if (Math.Abs(fraction - _lastReportedPercent) < 0.0001)
        {
            return;
        }

        _lastReportedPercent = fraction;
        _onProgress?.Invoke(fraction);
    }

    /// <summary>Extracts DISM's percentage as a 0..1 fraction. Pure; the parsing tests hit this directly.</summary>
    public static bool TryParsePercentage(string text, out double fraction)
    {
        fraction = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = PercentageRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        // The comma alternative is belt and braces: /English should force '.', but a host whose
        // console codepage surprises us costs nothing to tolerate here.
        var number = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return false;
        }

        fraction = Math.Clamp(percent / 100.0, 0.0, 1.0);
        return true;
    }

    /// <summary>True for a line that is only DISM's progress bar.</summary>
    public static bool IsProgressBar(string line) => ProgressBarRegex().IsMatch(line);

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentageRegex();

    [GeneratedRegex(@"^\s*\[[=\s.%\d]*\]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressBarRegex();
}
