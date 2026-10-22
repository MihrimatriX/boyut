namespace Boyut.Core.Ntfs;

/// <summary>Raw byte access to an NTFS volume: a device handle (\\.\C:) or an image file.</summary>
public interface IVolumeReader : IDisposable
{
    /// <summary>Reads must start and end on multiples of this (sector size for devices, 1 for files).</summary>
    int Alignment { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>.</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>Volume reader over any seekable stream (disk images, tests).</summary>
public sealed class StreamVolumeReader(Stream stream, bool leaveOpen = false) : IVolumeReader
{
    public int Alignment => 1;

    public int Read(long offset, Span<byte> buffer)
    {
        stream.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public void Dispose()
    {
        if (!leaveOpen) stream.Dispose();
    }

    public static StreamVolumeReader OpenImage(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess));
}

internal static class VolumeReaderExtensions
{
    /// <summary>Reads an arbitrary range, widening it to the reader's alignment when needed.</summary>
    public static void ReadExactly(this IVolumeReader reader, long offset, Span<byte> destination)
    {
        int align = Math.Max(1, reader.Alignment);
        long start = offset / align * align;
        long end = (offset + destination.Length + align - 1) / align * align;
        if (start == offset && end == offset + destination.Length)
        {
            if (reader.Read(offset, destination) < destination.Length) throw new EndOfStreamException("Birim okunamadı.");
            return;
        }

        var buffer = new byte[end - start];
        int read = reader.Read(start, buffer);
        int skip = (int)(offset - start);
        if (read < skip + destination.Length) throw new EndOfStreamException("Birim okunamadı.");
        buffer.AsSpan(skip, destination.Length).CopyTo(destination);
    }
}
