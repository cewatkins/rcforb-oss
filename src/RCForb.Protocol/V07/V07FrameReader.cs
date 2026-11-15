using System.Buffers.Binary;

namespace RCForb.Protocol.V07;

/// <summary>
/// Byte-compatible reader for the v0.7 audio stream. The format has no checksum, no
/// sequence number and two lengths that cannot be expressed (19 and 54), so recovery from
/// a bad header is a destructive one-byte left shift. SPEC 02 §4.7.
/// </summary>
/// <remarks>
/// One packet is extracted per <see cref="TryRead"/> call. A header is only tested once
/// enough bytes are buffered for it, so stale bytes from the previous packet can never be
/// read as a header (SPEC-TRAN-041). A valid header whose payload has not fully arrived
/// consumes nothing and asks for more data (SPEC-TRAN-042).
/// </remarks>
public sealed class V07FrameReader
{
    /// <summary>The reference's pending-buffer size.</summary>
    public const int MaxBufferedBytes = 16384;

    private const int HeaderBytes = 10;
    private const int SessionHeaderBytes = 8;
    private const int SessionMagic = 54;
    private const int MinControlLength = 1;
    private const int MaxControlLength = 18;
    private const int ReservedLength = 19;
    private const int MaxAudioLength = 2047;

    private readonly byte[] _buffer = new byte[MaxBufferedBytes];
    private int _count;

    /// <summary>Bytes currently buffered and not yet consumed.</summary>
    public int BufferedBytes => _count;

    /// <summary>Bytes discarded one at a time by resynchronisation, for diagnostics.</summary>
    public long ResynchronisedBytes { get; private set; }

    /// <summary>Padding runs discarded, for diagnostics.</summary>
    public long PaddingBytes { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (_count + data.Length > MaxBufferedBytes)
        {
            throw new InvalidOperationException(
                $"Appending {data.Length} bytes would exceed the {MaxBufferedBytes}-byte buffer. " +
                "A reader that stops consuming must stall its producer rather than grow the buffer.");
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;
    }

    public V07ReadResult TryRead(out V07Frame frame)
    {
        frame = default;

        while (true)
        {
            if (_count == 0)
            {
                return V07ReadResult.NeedMoreData;
            }

            var span = _buffer.AsSpan(0, _count);

            // A run of four zero bytes is padding left by a sender whose count and buffer
            // length disagreed. Discarding it turns a defect into silence, not noise.
            if (_count >= 4 && span[0] == 0 && span[1] == 0 && span[2] == 0 && span[3] == 0)
            {
                Consume(4);
                PaddingBytes += 4;
                continue;
            }

            if (_count < SessionHeaderBytes)
            {
                return V07ReadResult.NeedMoreData;
            }

            var isSessionHeader =
                span[0] != 0 && span[1] == 0 && span[2] == 0 && span[3] == 0 &&
                span[4] != 0 && span[5] == 0 && span[6] == 0 && span[7] == 0;

            var isDataHeader = false;
            if (_count >= HeaderBytes)
            {
                isDataHeader =
                    span[0] != 0 && span[4] <= 2 &&
                    span[5] == 0 && span[6] == 0 && span[7] == 0 && span[8] == 0 && span[9] == 0;
            }

            if (!isSessionHeader && !isDataHeader)
            {
                // Either not a header yet (fewer than ten bytes) or genuinely invalid. With
                // fewer than ten bytes we cannot tell, so wait rather than destroy bytes.
                if (_count < HeaderBytes && !isSessionHeader)
                {
                    return V07ReadResult.NeedMoreData;
                }

                Resynchronise();
                continue;
            }

            var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(span);

            if (declaredLength == SessionMagic)
            {
                if (!isSessionHeader)
                {
                    Resynchronise();
                    continue;
                }

                var sessionPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                if (sessionPayloadLength < 1 || sessionPayloadLength > 64 - SessionHeaderBytes)
                {
                    Resynchronise();
                    continue;
                }

                var sessionTotal = SessionHeaderBytes + sessionPayloadLength;
                if (_count < sessionTotal)
                {
                    return V07ReadResult.NeedMoreData;
                }

                frame = new V07Frame(
                    V07FrameKind.Session,
                    CopyPayload(SessionHeaderBytes, sessionPayloadLength),
                    TransmitterKeyed: false);
                Consume(sessionTotal);
                return V07ReadResult.Frame;
            }

            if (declaredLength >= MinControlLength && declaredLength <= MaxControlLength)
            {
                if (!TryTakeDataFrame(declaredLength, V07FrameKind.Control, span, ref frame))
                {
                    return V07ReadResult.NeedMoreData;
                }

                return V07ReadResult.Frame;
            }

            if (declaredLength == ReservedLength)
            {
                // Unrepresentable by contract: a 19-byte payload cannot exist, so the header
                // is a misread. SPEC-TRAN-035.
                Resynchronise();
                continue;
            }

            if (declaredLength >= ReservedLength + 1 && declaredLength <= MaxAudioLength)
            {
                if (!TryTakeDataFrame(declaredLength, V07FrameKind.Audio, span, ref frame))
                {
                    return V07ReadResult.NeedMoreData;
                }

                return V07ReadResult.Frame;
            }

            // 2048 and above, or zero/negative: not a length this format can carry.
            Resynchronise();
        }
    }

    private bool TryTakeDataFrame(int payloadLength, V07FrameKind kind, ReadOnlySpan<byte> span, ref V07Frame frame)
    {
        var total = HeaderBytes + payloadLength;
        if (_count < total)
        {
            return false;
        }

        // Byte 4 is ambiguous by design (2 means both "audio" and "keyed"), so routing is by
        // length only; the flag is used for exactly one decision. SPEC-TRAN-033.
        var keyed = kind == V07FrameKind.Control && span[4] == 2;

        frame = new V07Frame(
            kind,
            CopyPayload(HeaderBytes, payloadLength),
            keyed);
        Consume(total);
        return true;
    }

    /// <summary>
    /// The payload is copied out because consuming a frame shifts the pending buffer; a view
    /// into it would silently start returning the following packet's bytes.
    /// </summary>
    private ReadOnlyMemory<byte> CopyPayload(int offset, int length) =>
        _buffer.AsSpan(offset, length).ToArray();

    private void Resynchronise()
    {
        Consume(1);
        ResynchronisedBytes++;
    }

    private void Consume(int count)
    {
        _count -= count;
        if (_count > 0)
        {
            Buffer.BlockCopy(_buffer, count, _buffer, 0, _count);
        }
    }
}
