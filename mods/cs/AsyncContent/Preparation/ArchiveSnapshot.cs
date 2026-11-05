namespace JibbersAsyncContent.Preparation;

internal static unsafe class ArchiveSnapshot
{
    public static byte[] Copy(nint source, int length)
    {
        if (length < 0 || length > ContentLimits.ArchiveBytes) throw new ArgumentOutOfRangeException(nameof(length));
        if (source == 0 && length != 0) throw new ArgumentNullException(nameof(source));
        var snapshot = GC.AllocateUninitializedArray<byte>(length);
        new ReadOnlySpan<byte>((void*)source, length).CopyTo(snapshot);
        return snapshot;
    }
}
