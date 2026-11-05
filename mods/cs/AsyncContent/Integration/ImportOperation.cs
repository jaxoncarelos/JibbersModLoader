using Jibbers.Sdk;
using JibbersAsyncContent.Preparation;
using System.Diagnostics;

namespace JibbersAsyncContent.Integration;

internal sealed class ImportOperation : IDisposable
{
    readonly GameRef iterator;
    readonly List<GameRef> transfers = [];
    public long Started { get; } = Stopwatch.GetTimestamp();
    public PreparationQueue.Lease Preparation { get; }
    public PreparationSummary? Prepared { get; set; }
    public UnityAssetFactory.TextureUpload? PendingTexture { get; set; }
    public string? PendingTexturePath { get; set; }
    public int TextureBatches { get; set; }
    public int BatchedTextures { get; set; }
    public Dictionary<string, StagedAsset> Assets { get; } = new(StringComparer.Ordinal);
    public double UploadMilliseconds { get; set; }
    public double LongestUploadMilliseconds { get; set; }
    public int UploadCount { get; set; }
    public double NativeTextureMilliseconds { get; set; }
    public double NativeMeshMilliseconds { get; set; }
    public double NativeTextMilliseconds { get; set; }
    public double RegistrationMilliseconds { get; set; }
    public bool PreparationObserved { get; set; }
    public bool NativeDisposed { get; set; }
    public bool Cancelled { get; set; }
    public int Textures { get; set; }
    public int Meshes { get; set; }
    public int UploadFallbacks { get; set; }
    public double LongestStepMilliseconds { get; set; }
    public nint Transfer(GameRef reference)
    {
        transfers.Add(reference);
        return reference.Target;
    }
    public void ReleaseTransfers()
    {
        foreach (var reference in transfers) reference.Dispose();
        transfers.Clear();
    }
    public ImportOperation(nint instance, PreparationQueue.Lease preparation)
    {
        Preparation = preparation;
        try { iterator = new GameRef(instance); }
        catch { preparation.Dispose(); throw; }
    }
    public void Dispose()
    {
        iterator.Dispose();
        ReleaseTransfers();
        PendingTexture?.Dispose();
        PendingTexture = null;
        foreach (var asset in Assets.Values) asset.Dispose();
        Assets.Clear();
        Preparation.Dispose();
    }
}
