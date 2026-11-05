using System.Diagnostics;
using System.IO.Compression;

namespace JibbersAsyncContent.Preparation;

internal static class ArchiveDecoder
{
    public static PreparedArchive Decode(byte[] snapshot, CancellationToken cancellation)
    {
        var assets = new Dictionary<string, PreparedAsset>(StringComparer.Ordinal);
        var summary = Prepare(snapshot, entry => assets.Add(entry.Path, entry.Asset), cancellation);
        return new(assets, summary.Fallbacks, summary.Milliseconds);
    }

    public static PreparationSummary Prepare(byte[] snapshot, Action<PreparedEntry> emit, CancellationToken cancellation)
    {
        if (snapshot.Length > ContentLimits.ArchiveBytes) throw new NotSupportedException("Archive byte limit.");
        var started = Stopwatch.GetTimestamp();
        var count = 0;
        var details = new List<PreparationFallback>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fallbacks = 0;
        long preparedBytes = 0;
        using var memory = new MemoryStream(snapshot, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Count > ContentLimits.Entries) throw new NotSupportedException("Archive entry limit.");
        foreach (var entry in archive.Entries)
            if (!names.Add(entry.FullName.Replace('\\', '/'))) throw new NotSupportedException("Ambiguous archive paths; retain original importer.");
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var extension = Path.GetExtension(name);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".obj", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (entry.Length > ContentLimits.EntryBytes) throw new NotSupportedException("Entry byte limit.");
                // Do not allocate another image/mesh after the retained output budget is exhausted.
                if (preparedBytes >= ContentLimits.StagedBytes) throw new NotSupportedException("Staged asset byte limit.");
                var bytes = ReadEntry(entry, cancellation);
                PreparedAsset asset;
                long bytesUsed;
                if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var dimensions = PngDecoder.Dimensions(bytes);
                    bytesUsed = MipmapGenerator.Eligible(dimensions.Width, dimensions.Height, MipmapGenerator.Selected)
                        ? MipmapGenerator.ByteCount(dimensions.Width, dimensions.Height) : (long)dimensions.Width * dimensions.Height * 4;
                    if (bytesUsed > ContentLimits.StagedBytes - preparedBytes) throw new NotSupportedException("Staged image budget.");
                    asset = new(null, null, PngDecoder.Decode(bytes, cancellation));
                }
                else
                {
                    using var textStream = new MemoryStream(bytes, writable: false);
                    using var reader = new StreamReader(textStream, detectEncodingFromByteOrderMarks: true);
                    var text = reader.ReadToEnd();
                    var mesh = extension.Equals(".obj", StringComparison.OrdinalIgnoreCase) ? ObjDecoder.Decode(text, cancellation) : null;
                    bytesUsed = (mesh?.Bytes ?? 0) + (long)text.Length * 2;
                    if (bytesUsed > ContentLimits.StagedBytes - preparedBytes) throw new NotSupportedException("Staged mesh budget.");
                    asset = new(text, mesh, null);
                }
                emit(new(name, asset));
                count++;
                preparedBytes += bytesUsed;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException or EndOfStreamException or FormatException or OverflowException or ArgumentException)
            {
                // Original validation/loading still decides whether this content is accepted.
                fallbacks++;
                if (details.Count < 8) details.Add(new(name, error.Message));
            }
        }
        return new(count, fallbacks, Stopwatch.GetElapsedTime(started).TotalMilliseconds, details);
    }

    static byte[] ReadEntry(ZipArchiveEntry entry, CancellationToken cancellation)
    {
        var result = new byte[checked((int)entry.Length)];
        using var input = entry.Open();
        var position = 0;
        while (position < result.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = input.Read(result, position, Math.Min(65536, result.Length - position));
            if (read == 0) throw new InvalidDataException("Truncated archive entry.");
            position += read;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Entry exceeds its declared size.");
        return result;
    }
}
