using System.Buffers.Binary;

namespace RCForb.Client.Audio;

/// <summary>
/// Minimal RIFF/WAVE writer for the decoded receive stream: 16-bit mono, one sample rate.
/// It exists so a headless listen can be checked with any audio player after the fact;
/// device playback is a separate concern and can run alongside it through
/// <see cref="CompositeAudioSink"/>.
/// </summary>
public sealed class WavFileWriter : IGuestAudioSink, IDisposable
{
    private readonly FileStream _stream;
    private readonly int _sampleRate;
    private readonly short _channels;
    private readonly short _bitsPerSample;
    private long _dataBytes;
    private bool _disposed;

    public WavFileWriter(string path, int sampleRate = 8000, short channels = 1, short bitsPerSample = 16)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader();
    }

    public long DataBytes => _dataBytes;

    public void Write(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length == 0)
        {
            return;
        }

        var bytes = new byte[samples.Length * sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        }

        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[44];
        var byteRate = _sampleRate * _channels * _bitsPerSample / 8;
        var blockAlign = (short)(_channels * _bitsPerSample / 8);

        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], _channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], _sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], _bitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], 0);

        _stream.Write(header);
    }

    private void PatchSizes()
    {
        var riffSize = 36 + _dataBytes;
        Span<byte> value = stackalloc byte[4];

        _stream.Seek(4, SeekOrigin.Begin);
        BinaryPrimitives.WriteInt32LittleEndian(value, (int)Math.Min(riffSize, int.MaxValue));
        _stream.Write(value);

        _stream.Seek(40, SeekOrigin.Begin);
        BinaryPrimitives.WriteInt32LittleEndian(value, (int)Math.Min(_dataBytes, int.MaxValue));
        _stream.Write(value);

        _stream.Seek(0, SeekOrigin.End);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PatchSizes();
        _stream.Dispose();
    }
}
