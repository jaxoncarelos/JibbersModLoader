using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace RickBackflip;

/// <summary>
/// Joue une vidéo (mp4, webm) au-dessus du jeu avec le VideoPlayer d'Unity : l'image arrive dans une
/// RawImage, le son sort directement (Direct). À n'appeler que depuis le thread principal du jeu.
/// </summary>
internal sealed unsafe class VideoOverlay : IDisposable
{
    OwnedGameObject? root;
    OwnedGameObject? playerObject;
    GameRef? image;
    GameRef? player;
    float heightPx;
    float volume;
    bool started;
    bool hasTexture;
    long startedAtMs;

    /// <summary>Durée de la vidéo en secondes (0 tant qu'elle n'est pas prête).</summary>
    public double LengthSeconds { get; private set; }

    public void Show(string path, float heightPx, GifOverlay.Placement placement, float volume01)
    {
        Dispose();
        this.heightPx = heightPx;
        volume = Math.Clamp(volume01, 0f, 1f);
        (started, hasTexture, startedAtMs, LengthSeconds) = (false, false, 0, 0);
        try
        {
            (root, image) = OverlayCanvas.Build("RickBackflip.Video", placement, heightPx, heightPx);
            UnityProperties.Set(image.Target, "set_enabled", false); // pas de carré blanc avant la première image

            playerObject = OwnedGameObject.Create("Player");
            var vp = playerObject.AddComponent("UnityEngine.Video.VideoPlayer");
            playerObject.AttachTo(root.Transform);
            player = new GameRef(vp);

            UnityProperties.Set(vp, "set_playOnAwake", false);
            UnityProperties.Set(vp, "set_isLooping", false);
            UnityProperties.Set(vp, "set_skipOnDrop", true);
            UnityProperties.Set(vp, "set_renderMode", 4);      // VideoRenderMode.APIOnly : on lit « texture »
            UnityProperties.Set(vp, "set_audioOutputMode", 2); // VideoAudioOutputMode.Direct
            Unity.Call(vp, "set_url", Il2Cpp.String("file:///" + path.Replace('\\', '/')));
            Unity.Call(vp, "Prepare");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>À appeler chaque frame. Renvoie faux quand la vidéo est finie ; lève une exception si elle ne peut pas être lue.</summary>
    public bool Update(long elapsedMs)
    {
        if (player is null || image is null || Unity.IsNull(player.Target) || Unity.IsNull(image.Target))
            throw new InvalidOperationException("video overlay was destroyed");
        var vp = player.Target;

        if (!started)
        {
            if (!Unity.Call<bool>(vp, "get_isPrepared"))
            {
                if (elapsedMs > 8000) throw new TimeoutException("the video could not be prepared (unsupported format or damaged file)");
                return true;
            }

            uint w = Unity.Call<uint>(vp, "get_width"), h = Unity.Call<uint>(vp, "get_height");
            if (w > 0 && h > 0)
                UnityProperties.Set(Unity.Call(image.Target, "get_rectTransform"), "set_sizeDelta", new Vector2(heightPx * w / h, heightPx));
            try { LengthSeconds = Unity.Call<double>(vp, "get_length"); }
            catch (Exception) { /* durée inconnue : le réglage « Max duration » s'applique */ }

            SetVolume(vp);
            Unity.Call(vp, "Play");
            started = true;
            startedAtMs = elapsedMs;
            return true;
        }

        if (!hasTexture)
        {
            var texture = Unity.Call(vp, "get_texture");
            if (texture != 0 && !Unity.IsNull(texture))
            {
                Unity.Call(image.Target, "set_texture", texture);
                UnityProperties.Set(image.Target, "set_enabled", true);
                hasTexture = true;
            }
        }

        // Laisse un instant au lecteur pour démarrer avant de croire qu'il est arrêté.
        return elapsedMs - startedAtMs < 700 || Unity.Call<bool>(vp, "get_isPlaying");
    }

    void SetVolume(nint vp)
    {
        try
        {
            var method = Il2Cpp.ClassOf(vp).Method("SetDirectAudioVolume", 2);
            if (method.IsNull) return;
            ushort track = 0;
            var v = volume;
            method.Invoke(vp, (nint)(&track), (nint)(&v));
        }
        catch (Exception e)
        {
            Log.Info("rick_backflip", "Video volume not applied: " + e.Message);
        }
    }

    public void Dispose()
    {
        var (ownedRoot, ownedPlayerObject, ownedImage, ownedPlayer) = (root, playerObject, image, player);
        (root, playerObject, image, player) = (null, null, null, null);
        started = hasTexture = false;
        Cleanup.Run(new Action[]
        {
            () => { if (ownedPlayer is not null && !Unity.IsNull(ownedPlayer.Target)) Unity.Call(ownedPlayer.Target, "Stop"); },
            () => ownedRoot?.Dispose(), // détruit aussi le lecteur, enfant du Canvas
            () => ownedPlayerObject?.Dispose(),
            () => ownedImage?.Dispose(),
            () => ownedPlayer?.Dispose(),
        });
    }
}
