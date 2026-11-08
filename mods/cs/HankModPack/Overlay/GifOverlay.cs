using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace RickBackflip;

/// <summary>
/// Affiche un GIF au-dessus du jeu : un Canvas Unity à lui (ScreenSpaceOverlay) avec une RawImage dont la
/// texture est remplacée à chaque image du GIF. Même technique que les overlays du SDK.
/// À n'appeler que depuis le thread principal du jeu.
/// </summary>
internal sealed unsafe class GifOverlay : IDisposable
{
    OwnedGameObject? root;
    GameRef? image;
    GameRef? texture;
    int shownFrame = -1;

    /// <summary>Coin de l'écran où le GIF est collé, décalage compris (en pixels de référence 1920x1080).</summary>
    public sealed record Placement(float AnchorX, float AnchorY, float OffsetX, float OffsetY, float PivotX = -1, float PivotY = -1);

    public bool IsShown => root is not null && !Unity.IsNull(root.Target);

    public void Show(GifAnimation gif, float heightPx, Placement placement)
    {
        Dispose();
        try
        {
            var width = heightPx * gif.Width / gif.Height;
            (root, image) = OverlayCanvas.Build("RickBackflip.Overlay", placement, width, heightPx);

            CreateTexture(gif);
            Draw(gif, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Incline l'image (degrés, sens inverse des aiguilles d'une montre), autour de son centre.</summary>
    public void Rotate(float degrees)
    {
        if (image is null || Unity.IsNull(image.Target)) return;
        UnityProperties.Set(Unity.Call(image.Target, "get_rectTransform"), "set_localEulerAngles", new Vector3(0, 0, degrees));
    }

    /// <summary>Affiche l'image <paramref name="index"/> du GIF (ne fait rien si c'est déjà elle).</summary>
    public void Draw(GifAnimation gif, int index)
    {
        if (texture is null || image is null || Unity.IsNull(texture.Target) || Unity.IsNull(image.Target))
            throw new InvalidOperationException("overlay was destroyed");
        if (index == shownFrame) return;
        shownFrame = index;

        var k = Unity.Class("UnityEngine.Texture2D");
        Fill(k, texture.Target, gif.Frames[index]);
        var apply = k.Method("Apply", 0);
        if (!apply.IsNull) apply.Invoke(texture.Target);
        else
        {
            byte mips = 0, unreadable = 0;
            k.Method("Apply", 2).Invoke(texture.Target, (nint)(&mips), (nint)(&unreadable));
        }
    }

    void CreateTexture(GifAnimation gif)
    {
        var k = Unity.Class("UnityEngine.Texture2D");
        var tex = k.New();
        int w = gif.Width, h = gif.Height, format = 4; // RGBA32
        byte mips = 0;
        k.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Boolean")
            .Invoke(tex, (nint)(&w), (nint)(&h), (nint)(&format), (nint)(&mips));
        UnityProperties.Set(tex, "set_wrapMode", 1);   // Clamp
        UnityProperties.Set(tex, "set_filterMode", 1); // Bilinear
        texture = new GameRef(tex);
        Unity.Call(image!.Target, "set_texture", tex);
        shownFrame = -1;
    }

    /// <summary>Copie des octets RGBA dans la texture, avec la méthode que cette version du jeu a gardée.</summary>
    static void Fill(Il2CppClass k, nint tex, byte[] rgba)
    {
        var raw = k.Method("LoadRawTextureData", "System.IntPtr", "System.Int32");
        if (!raw.IsNull)
        {
            fixed (byte* p = rgba)
            {
                var (ptr, len) = ((nint)p, rgba.Length);
                raw.Invoke(tex, (nint)(&ptr), (nint)(&len));
            }
            return;
        }
        var bytes = k.Method("LoadRawTextureData", "System.Byte[]");
        if (bytes.IsNull) throw new InvalidOperationException("this game build cannot fill textures");
        var arr = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), rgba.Length);
        rgba.CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(arr), rgba.Length));
        bytes.Invoke(tex, arr);
    }

    public void Dispose()
    {
        var (ownedRoot, ownedImage, ownedTexture) = (root, image, texture);
        (root, image, texture, shownFrame) = (null, null, null, -1);
        Cleanup.Run(new Action[]
        {
            () => ownedRoot?.Dispose(),
            () => { if (ownedTexture is not null && !Unity.IsNull(ownedTexture.Target)) Unity.Destroy(ownedTexture.Target); ownedTexture?.Dispose(); },
            () => ownedImage?.Dispose(),
        });
    }
}
