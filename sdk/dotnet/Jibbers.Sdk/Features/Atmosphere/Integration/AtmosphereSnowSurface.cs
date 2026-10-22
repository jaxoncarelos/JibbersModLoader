using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace Jibbers.Sdk.Features.Atmosphere;

internal sealed unsafe class AtmosphereSnowSurface(Action<string> log) : IAtmosphereSurface
{
    static readonly string[] SurfaceTextureProperties =
    {
        "_SurfaceTex",
        "_SnowNormal",
        "_SnowDetailNormal"
    };
    static readonly string[] SurfaceFloatProperties =
    {
        "_SnowTexTiling",
        "_SnowNormalStrength",
        "_SnowDetailTiling",
        "_SnowDetailNormalStrength"
    };
    sealed record SnowSurfaceState(GameRef Material, List<(string PropertyName, GameRef? Texture)> Textures, List<(string PropertyName, float Value)> Floats);
    SnowSurfaceState? snowSurfaceState;
    int framesUntilSurfaceCheck;
    const string Prefab = "JibbersAtmosphere";
    static nint CallMaterial(string method, nint material, params nint[] args) => Unity.Call(material, method, args);
    static nint GetTexture(nint material, string propertyName) => CallMaterial("GetTexture", material, Il2Cpp.String(propertyName));
    static void SetTexture(nint material, string propertyName, nint texture) => CallMaterial("SetTexture", material, Il2Cpp.String(propertyName), texture);
    static float? GetFloat(nint material, string propertyName)
    {
        try
        {
            return Il2Cpp.Unbox<float>(CallMaterial("GetFloat", material, Il2Cpp.String(propertyName)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void SetFloat(nint material, string propertyName, float value) => CallMaterial("SetFloat", material, Il2Cpp.String(propertyName), (nint)(&value));
    public void Apply(JsonObject settings, bool effectsEnabled)
    {
        if (framesUntilSurfaceCheck-- > 0)
        {
            return;
        }

        framesUntilSurfaceCheck = 120;
        var needsSnowSurface = effectsEnabled && AtmosphereConfiguration.GetBool(settings, "snowSurface");
        var currentMaterial = snowSurfaceState?.Material.Target is { } savedMaterial && !Unity.IsNull(savedMaterial) ? savedMaterial : (nint? )null;
        nint? desiredMaterial = null;
        if (needsSnowSurface && UnityTerrain.MapTerrains().FirstOrDefault()is var terrain && terrain != 0)
        {
            var material = Unity.Call(terrain, "get_materialTemplate");
            desiredMaterial = Unity.IsNull(material) ? null : material;
        }

        if (snowSurfaceState is { } previousSurface && !(currentMaterial is { } oldMaterial && desiredMaterial is { } newMaterial && oldMaterial == newMaterial))
        {
            // Restore each property independently; release every owned reference even if a setter fails.
            snowSurfaceState = null;
            RestoreSurface(previousSurface, currentMaterial);
        }

        if (desiredMaterial is not { } targetMaterial)
        {
            return;
        }

        var textureHolder = Unity.Find(Prefab + "(Clone)/SnowSurface");
        if (Unity.IsNull(textureHolder))
        {
            return;
        }

        var sourceMaterial = Unity.Call(Unity.GetComponent(textureHolder, Unity.Class("UnityEngine.Renderer")), "get_sharedMaterial");
        if (snowSurfaceState is null)
        {
            snowSurfaceState = CaptureSurface(targetMaterial);
            foreach (var propertyName in SurfaceTextureProperties)
            {
                var texture = GetTexture(sourceMaterial, propertyName);
                if (!Unity.IsNull(texture))
                {
                    SetTexture(targetMaterial, propertyName, texture);
                }
            }

            log("snow surface applied");
        }

        // uv = world x _WorldScale x tiling, so one repeat is 1 / (scale x tiling) metres.
        var worldScale = GetFloat(targetMaterial, "_WorldScale")is { } scale && scale > 0 ? scale : 0.01f;
        SetFloat(targetMaterial, "_SnowTexTiling", 1 / (worldScale * MathF.Max(AtmosphereConfiguration.GetFloat(settings, "snowSurfaceScale"), 0.1f)));
        SetFloat(targetMaterial, "_SnowNormalStrength", AtmosphereConfiguration.GetFloat(settings, "snowSurfaceNormal"));
        SetFloat(targetMaterial, "_SnowDetailTiling", 1 / (worldScale * MathF.Max(AtmosphereConfiguration.GetFloat(settings, "snowSurfaceDuneScale"), 0.1f)));
        SetFloat(targetMaterial, "_SnowDetailNormalStrength", AtmosphereConfiguration.GetFloat(settings, "snowSurfaceDuneNormal"));
    }

    static SnowSurfaceState CaptureSurface(nint material)
    {
        using var ownership = new ResourceScope();
        var materialRoot = ownership.Own(new GameRef(material));
        var textures = SurfaceTextureProperties.Select(propertyName =>
        {
            var texture = GetTexture(material, propertyName);
            return (propertyName, texture == 0 ? null : ownership.Own(new GameRef(texture)));
        }).ToList();
        var floats = SurfaceFloatProperties.Select(propertyName => (PropertyName: propertyName, Value: GetFloat(material, propertyName))).Where(property => property.Value is not null).Select(property => (property.PropertyName, property.Value.GetValueOrDefault())).ToList();
        ownership.TransferOwnership();
        return new(materialRoot, textures, floats);
    }

    static void RestoreSurface(SnowSurfaceState saved, nint? material)
    {
        var restoration = new List<Action>();
        if (material is { } liveMaterial)
        {
            restoration.AddRange(saved.Textures.Select(texture => (Action)(() => SetTexture(liveMaterial, texture.PropertyName, texture.Texture?.Target ?? 0))));
            restoration.AddRange(saved.Floats.Select(property => (Action)(() => SetFloat(liveMaterial, property.PropertyName, property.Value))));
        }

        restoration.AddRange(saved.Textures.Where(texture => texture.Texture is not null).Select(texture => (Action)texture.Texture!.Dispose));
        restoration.Add(saved.Material.Dispose);
        Cleanup.Run(restoration.ToArray());
    }

    public void Invalidate() => framesUntilSurfaceCheck = 0;
    public void Restore()
    {
        framesUntilSurfaceCheck = 0;
        Apply(new JsonObject(), false);
    }

    public void Dispose() => Restore();
}
