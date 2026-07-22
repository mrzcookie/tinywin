using TinyWin.Imaging.Dism;

namespace TinyWin.Imaging.Tests;

/// <summary>
/// Progress-bar decoding, across all three encodings DISM might use under redirection.
/// </summary>
/// <remarks>
/// The spike could not determine which one DISM actually uses when stdout is a pipe
/// (docs/spikes/dism-backend.md §5), so all three are covered and the "no bar at all" case is
/// treated as a first-class outcome rather than an edge case.
/// </remarks>
public class DismOutputReaderTests
{
    private readonly List<string> _lines = [];
    private readonly List<double> _progress = [];

    private DismOutputReader NewReader() => new(_lines.Add, _progress.Add);

    [Fact]
    public void Plain_lines_are_emitted_on_newlines()
    {
        var reader = NewReader();

        reader.Append("Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.8457\r\n");
        reader.Complete();

        Assert.Equal(
            ["Deployment Image Servicing and Management tool", "Version: 10.0.26100.8457"],
            _lines);
    }

    [Fact]
    public void Trailing_output_without_a_newline_is_still_emitted()
    {
        var reader = NewReader();

        reader.Append("The operation completed successfully.");
        reader.Complete();

        Assert.Equal(["The operation completed successfully."], _lines);
    }

    /// <summary>Encoding 1: the bar is redrawn by backspacing over the previous paint.</summary>
    [Fact]
    public void Backspace_driven_progress_is_decoded()
    {
        var reader = NewReader();

        reader.Append("[====                       10.0%                          ]");
        reader.Append(new string('\b', 59));
        reader.Append("[==========                 25.0%                          ]");
        reader.Append(new string('\b', 59));
        reader.Append("[==========================100.0%==========================]\r\n");
        reader.Complete();

        Assert.True(reader.SawPercentage);
        Assert.Equal([0.10, 0.25, 1.0], _progress);
    }

    /// <summary>Encoding 2: the bar is redrawn with a bare carriage return and no line feed.</summary>
    [Fact]
    public void Carriage_return_driven_progress_is_decoded()
    {
        var reader = NewReader();

        reader.Append("[====                       10.0%                          ]\r");
        reader.Append("[==========                 25.0%                          ]\r");
        reader.Append("[==========================100.0%==========================]\r\n");
        reader.Complete();

        Assert.True(reader.SawPercentage);
        Assert.Equal([0.10, 0.25, 1.0], _progress);
    }

    /// <summary>
    /// Encoding 3: DISM suppresses the bar under redirection. Nothing is reported and
    /// <see cref="DismOutputReader.SawPercentage"/> stays false, which is the signal callers need to
    /// present the operation as indeterminate rather than stalled at zero.
    /// </summary>
    [Fact]
    public void A_suppressed_progress_bar_reports_nothing_and_says_so()
    {
        var reader = NewReader();

        reader.Append("Mounting image\r\nThe operation completed successfully.\r\n");
        reader.Complete();

        Assert.False(reader.SawPercentage);
        Assert.Empty(_progress);
        Assert.Equal(["Mounting image", "The operation completed successfully."], _lines);
    }

    /// <summary>
    /// Progress must be visible before the bar is terminated. If it were only decoded on a newline,
    /// a 40-minute /ResetBase that never emits one would report nothing until it finished.
    /// </summary>
    [Fact]
    public void Progress_is_reported_before_the_bar_is_terminated()
    {
        var reader = NewReader();

        reader.Append("[=====                      12.5%");

        Assert.Equal([0.125], _progress);
    }

    [Fact]
    public void Progress_bar_repaints_are_not_emitted_as_log_lines()
    {
        var reader = NewReader();

        reader.Append("[==========================100.0%==========================]\r\n");
        reader.Complete();

        Assert.Empty(_lines);
    }

    /// <summary>DISM repaints the same value repeatedly; forwarding each repaint is pure noise.</summary>
    [Fact]
    public void Repeated_identical_percentages_are_reported_once()
    {
        var reader = NewReader();

        for (var i = 0; i < 5; i++)
        {
            reader.Append("[====                       10.0%                          ]\r");
        }

        Assert.Equal([0.10], _progress);
    }

    /// <summary>Output arrives in whatever chunks the pipe delivers, not in whole lines.</summary>
    [Fact]
    public void Output_split_across_chunk_boundaries_is_reassembled()
    {
        var reader = NewReader();

        reader.Append("Deployment Image Servi");
        reader.Append("cing and Management to");
        reader.Append("ol\r\n");
        reader.Complete();

        Assert.Equal(["Deployment Image Servicing and Management tool"], _lines);
    }

    [Fact]
    public void Blank_lines_are_not_emitted()
    {
        var reader = NewReader();

        reader.Append("\r\n\r\n   \r\nReal line\r\n");
        reader.Complete();

        Assert.Equal(["Real line"], _lines);
    }

    [Theory]
    [InlineData("[====   0.0%   ]", 0.0)]
    [InlineData("[====  37.5%   ]", 0.375)]
    [InlineData("[==== 100.0%   ]", 1.0)]
    [InlineData("[====  20,0%   ]", 0.20)]
    public void Percentages_parse_to_fractions(string text, double expected)
    {
        Assert.True(DismOutputReader.TryParsePercentage(text, out var fraction));
        Assert.Equal(expected, fraction, 5);
    }

    [Theory]
    [InlineData("Version: 10.0.26100.8457")]
    [InlineData("The operation completed successfully.")]
    [InlineData("")]
    public void Lines_without_a_percentage_report_none(string text)
    {
        Assert.False(DismOutputReader.TryParsePercentage(text, out _));
    }

    /// <summary>
    /// "Image Version: 10.0.26200.8037" must not be mistaken for a progress bar and swallowed —
    /// it is the line that tells the user which image is being serviced.
    /// </summary>
    [Fact]
    public void Version_lines_are_not_mistaken_for_progress_bars()
    {
        Assert.False(DismOutputReader.IsProgressBar("Image Version: 10.0.26200.8037"));
        Assert.True(DismOutputReader.IsProgressBar("[==========================100.0%==========================]"));
    }
}
