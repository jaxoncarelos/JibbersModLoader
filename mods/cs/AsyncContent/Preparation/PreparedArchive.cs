using Jibbers.Sdk;

namespace JibbersAsyncContent.Preparation;

internal sealed record TexturePixels(int Width, int Height, byte[] Rgba, bool HasMipChain = false)
{
    public int BaseBytes => checked(Width * Height * 4);
}
internal sealed record MeshGeometry(Vector3[] Vertices, Vector2[] Texcoords, Vector3[] Normals, int[] Triangles)
{
    public long Bytes => (long)Vertices.Length * 12 + (long)Texcoords.Length * 8 + (long)Normals.Length * 12 + (long)Triangles.Length * 4;
}
internal sealed record PreparedAsset(string? Text, MeshGeometry? Mesh, TexturePixels? Texture);
internal sealed record PreparedArchive(IReadOnlyDictionary<string, PreparedAsset> Assets, int Fallbacks, double PreparationMilliseconds);
internal sealed record PreparedEntry(string Path, PreparedAsset Asset);
internal sealed record PreparationFallback(string Path, string Reason);
internal sealed record PreparationSummary(int Assets, int Fallbacks, double Milliseconds, IReadOnlyList<PreparationFallback> Details);

internal static class ContentLimits
{
    public const int ArchiveBytes = 64 * 1024 * 1024;
    public const int EntryBytes = 64 * 1024 * 1024;
    // Six 4K outfit maps with their full mip chains exceed 384 MiB before JSON/geometry.
    public const int StagedBytes = 768 * 1024 * 1024;
    public const int ImagePixels = 16 * 1024 * 1024;
    public const int Entries = 2048;
    public const int MeshVertices = 500_000;
    public const int MeshIndices = 3_000_000;
}
