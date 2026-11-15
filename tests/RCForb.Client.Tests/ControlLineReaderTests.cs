using System.Text;
using RCForb.Client.V07;

namespace RCForb.Client.Tests;

/// <summary>
/// The v0.7 control stream is line framed, and TCP may split a line, a terminator or a
/// UTF-8 sequence anywhere. These cases pin the boundary behaviour.
/// </summary>
public class ControlLineReaderTests
{
    [Theory]
    [InlineData("one\n", "one")]
    [InlineData("one\r\n", "one")]
    public void Terminators_are_stripped(string input, string expected)
    {
        var reader = new ControlLineReader();
        reader.Append(Encoding.UTF8.GetBytes(input));

        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal(expected, line);
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void A_crlf_split_across_reads_is_one_terminator()
    {
        var reader = new ControlLineReader();

        reader.Append("get id\r"u8);
        Assert.False(reader.TryReadLine(out _));

        reader.Append("\n"u8);
        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal("get id", line);
        Assert.Equal(0, reader.BufferedBytes);
    }

    [Fact]
    public void A_lone_cr_terminates_once_the_next_byte_proves_it_is_not_crlf()
    {
        var reader = new ControlLineReader();

        reader.Append("one\rtw"u8);

        Assert.True(reader.TryReadLine(out var first));
        Assert.Equal("one", first);
        Assert.False(reader.TryReadLine(out _));

        reader.Append("o\n"u8);
        Assert.True(reader.TryReadLine(out var second));
        Assert.Equal("two", second);
    }

    [Fact]
    public void Back_to_back_lines_in_one_read_are_all_available()
    {
        var reader = new ControlLineReader();
        reader.Append("a\nb\r\nc\rx"u8);

        Assert.True(reader.TryReadLine(out var a));
        Assert.True(reader.TryReadLine(out var b));
        Assert.True(reader.TryReadLine(out var c));
        Assert.Equal(("a", "b", "c"), (a, b, c));
        Assert.False(reader.TryReadLine(out _));
    }

    [Fact]
    public void A_multibyte_character_split_across_reads_survives()
    {
        var reader = new ControlLineReader();
        var bytes = Encoding.UTF8.GetBytes("héllo\n");

        // Split in the middle of 'é' (2 bytes in UTF-8).
        reader.Append(bytes.AsSpan(0, 2));
        Assert.False(reader.TryReadLine(out _));

        reader.Append(bytes.AsSpan(2));
        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal("héllo", line);
    }

    [Fact]
    public void An_empty_line_is_returned_as_empty_not_as_a_failure()
    {
        var reader = new ControlLineReader();
        reader.Append("\n"u8);

        Assert.True(reader.TryReadLine(out var line));
        Assert.Equal("", line);
    }
}
