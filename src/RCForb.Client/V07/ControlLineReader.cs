using System.Text;

namespace RCForb.Client.V07;

/// <summary>
/// Splits the v0.7 control stream into lines. The stream is UTF-8 terminated by <c>LF</c>,
/// <c>CR LF</c> or a lone <c>CR</c>; no terminator is returned (SPEC 02 §4.2).
/// </summary>
/// <remarks>
/// A <c>CR</c> at the end of the buffered bytes is held until one more byte arrives, because
/// it may be the first half of <c>CR LF</c>. That is the only lookahead the format needs.
/// UTF-8 sequences may straddle reads; decoding happens after a complete line is found.
/// </remarks>
public sealed class ControlLineReader
{
    private const int MaxBufferedBytes = 64 * 1024;

    private byte[] _buffer = new byte[4096];
    private int _count;

    public int BufferedBytes => _count;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (_count + data.Length > MaxBufferedBytes)
        {
            throw new InvalidOperationException(
                $"A line exceeding {MaxBufferedBytes} bytes is a protocol violation; no terminator arrived.");
        }

        if (_count + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    /// <summary>Takes one complete line, or returns false when more bytes are needed.</summary>
    public bool TryReadLine(out string line)
    {
        line = string.Empty;

        for (var i = 0; i < _count; i++)
        {
            var b = _buffer[i];

            if (b == (byte)'\n')
            {
                line = Decode(0, i);
                Consume(i + 1);
                return true;
            }

            if (b != (byte)'\r')
            {
                continue;
            }

            // A trailing CR may still become CR LF. Wait for the next byte rather than
            // emitting a line and then an empty one from the LF.
            if (i + 1 == _count)
            {
                return false;
            }

            var consumed = _buffer[i + 1] == (byte)'\n' ? i + 2 : i + 1;
            line = Decode(0, i);
            Consume(consumed);
            return true;
        }

        return false;
    }

    private string Decode(int offset, int length) =>
        Encoding.UTF8.GetString(_buffer, offset, length);

    private void Consume(int count)
    {
        _count -= count;
        if (_count > 0)
        {
            Buffer.BlockCopy(_buffer, count, _buffer, 0, _count);
        }
    }
}
