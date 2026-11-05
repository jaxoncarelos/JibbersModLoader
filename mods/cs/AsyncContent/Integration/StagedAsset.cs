using Jibbers.Sdk;

namespace JibbersAsyncContent.Integration;

/// <summary>Owns an unpublished Unity asset; ownership passes only when the original importer requests it.</summary>
internal sealed class StagedAsset(GameRef? reference, string? text, bool texture) : IDisposable
{
    bool handedOff;
    public string? Text { get; } = text;
    public bool IsTexture { get; } = texture;
    public bool IsMesh => !IsTexture && reference is not null;
    public nint HandOff()
    {
        var target = (reference ?? throw new InvalidOperationException("Text data has no Unity object to hand off.")).Target;
        handedOff = true;
        return target;
    }
    public void Dispose()
    {
        if (reference is null) return;
        try { if (!handedOff && !Unity.IsNull(reference.Target)) Unity.Destroy(reference.Target); }
        finally { reference.Dispose(); }
    }
}
