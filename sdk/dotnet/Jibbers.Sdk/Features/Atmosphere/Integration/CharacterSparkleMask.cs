using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>Keeps the snow glitter off characters: world-space boxes around the nearest riders, which the glitter shader rejects.</summary>
internal sealed class CharacterSparkleMask : IDisposable
{
    const int MaximumBoxes = 8;
    const float BoundsMargin = 0.15f, CachedBoundsDistance = 120, MaximumCharacterExtent = 3;
    const int RendererRefreshInterval = 30;
    int framesUntilRendererRefresh;
    readonly List<List<GameRef>> rendererGroups = new();
    readonly List<(Vector4 Center, Vector4 Extents)?> cachedBounds = new();
    readonly List<(Vector4 Center, Vector4 Extents)> boxes = new();
    readonly HashSet<nint> knownRiders = new();
    readonly HashSet<nint> knownSkinnedRenderers = new();
    bool inventoryKnown;
    int checksUntilHierarchyRefresh;
    static readonly string[] boxCenters = Enumerable.Range(0, MaximumBoxes).Select(index => $"_JA_CharBox{index}C").ToArray();
    static readonly string[] boxExtents = Enumerable.Range(0, MaximumBoxes).Select(index => $"_JA_CharBox{index}E").ToArray();
    void ReleaseRenderers()
    {
        foreach (var rendererGroup in rendererGroups)
        {
            rendererGroup.ForEach(renderer => renderer.Dispose());
        }

        rendererGroups.Clear();
        cachedBounds.Clear();
        knownRiders.Clear();
        knownSkinnedRenderers.Clear();
        inventoryKnown = false;
        checksUntilHierarchyRefresh = 0;
    }

    void CollectRenderers()
    {
        var skinnedMeshRendererClass = Unity.Class("UnityEngine.SkinnedMeshRenderer");
        var meshRendererClass = Unity.Class("UnityEngine.MeshRenderer");
        var riders = Jibbers.Sdk.Game.Player.Ragdoll.All().Select(rider => rider.Ptr).ToArray();
        var skinnedRenderers = Unity.FindObjects(skinnedMeshRendererClass).Where(renderer => !Unity.IsNull(renderer)).ToArray();
        // Most refreshes have exactly the same actors. Keep their groups and GC roots;
        // only moving bounds need updating, not hierarchy walks and handle reconstruction.
        if (inventoryKnown && checksUntilHierarchyRefresh-- > 0 && knownRiders.SetEquals(riders) && knownSkinnedRenderers.SetEquals(skinnedRenderers))
            return;
        var rendererSets = new List<nint[]>();
        foreach (var rider in riders)
        {
            rendererSets.Add(Unity.GetComponentsInChildren(rider, Unity.Class("UnityEngine.Renderer")));
        }

        // Other skinned meshes (remote riders and clothing) grouped by their scene root.
        foreach (var rendererGroup in skinnedRenderers.GroupBy(renderer => Unity.Call<int>(Unity.Call(Unity.Transform(renderer), "get_root"), "GetInstanceID")))
        {
            rendererSets.Add(rendererGroup.ToArray());
        }

        ReleaseRenderers();
        var seenRenderers = new HashSet<nint>();
        foreach (var renderers in rendererSets)
        {
            var ownedRenderers = new List<GameRef>();
            foreach (var renderer in renderers)
            {
                if (Unity.IsNull(renderer) || !seenRenderers.Add(renderer))
                {
                    continue;
                }

                var rendererClass = Jibbers.Sdk.Interop.Il2Cpp.ClassOf(renderer);
                if (skinnedMeshRendererClass.IsAssignableFrom(rendererClass) || meshRendererClass.IsAssignableFrom(rendererClass))
                {
                    ownedRenderers.Add(new GameRef(renderer));
                }
            }

            if (ownedRenderers.Count > 0)
            {
                rendererGroups.Add(ownedRenderers);
                cachedBounds.Add(null);
            }
        }
        knownRiders.UnionWith(riders);
        knownSkinnedRenderers.UnionWith(skinnedRenderers);
        inventoryKnown = true;
        checksUntilHierarchyRefresh = 20; // Periodic fallback also catches equipment-only hierarchy changes.
    }

    /// <summary>Refreshes the boxes for this frame; whether glitter may be drawn.</summary>
    public bool Update(bool enabled, Vector3? cameraPosition)
    {
        try
        {
            if (!enabled)
            {
                ReleaseRenderers();
                UploadBounds(cameraPosition);
                framesUntilRendererRefresh = 0;
                return false;
            }

            if (framesUntilRendererRefresh-- <= 0)
            {
                CollectRenderers();
                framesUntilRendererRefresh = RendererRefreshInterval;
            }

            UploadBounds(cameraPosition);
            return true;
        }
        catch (Exception)
        {
            framesUntilRendererRefresh = RendererRefreshInterval;
            return false;
        }
    }

    void UploadBounds(Vector3? cameraPosition)
    {
        boxes.Clear();
        bool CanReuseBounds((Vector4 Center, Vector4 Extents) rightBounds) => cameraPosition is { } observerPosition && (new Vector3(rightBounds.Center.X, rightBounds.Center.Y, rightBounds.Center.Z) - observerPosition).Magnitude > CachedBoundsDistance;
        for (var groupIndex = 0; groupIndex < rendererGroups.Count; groupIndex++)
        {
            if (cachedBounds[groupIndex] is { } kept && CanReuseBounds(kept))
            {
                boxes.Add(kept);
                continue;
            }

            var minimumBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var maximumBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var rendererHandle in rendererGroups[groupIndex])
            {
                var renderer = rendererHandle.Target;
                if (Unity.IsNull(renderer) || !Unity.Call<bool>(renderer, "get_enabled"))
                {
                    continue;
                }

                var rightBounds = Unity.Call<Bounds>(renderer, "get_bounds");
                var(center, extents) = (rightBounds.Center, rightBounds.Extents);
                minimumBounds = new Vector3(MathF.Min(minimumBounds.X, center.X - extents.X), MathF.Min(minimumBounds.Y, center.Y - extents.Y), MathF.Min(minimumBounds.Z, center.Z - extents.Z));
                maximumBounds = new Vector3(MathF.Max(maximumBounds.X, center.X + extents.X), MathF.Max(maximumBounds.Y, center.Y + extents.Y), MathF.Max(maximumBounds.Z, center.Z + extents.Z));
            }

            if (minimumBounds.X > maximumBounds.X || maximumBounds.X - minimumBounds.X > 2 * MaximumCharacterExtent || maximumBounds.Y - minimumBounds.Y > 2 * MaximumCharacterExtent || maximumBounds.Z - minimumBounds.Z > 2 * MaximumCharacterExtent)
            {
                cachedBounds[groupIndex] = null;
                continue;
            }

            var box = (new Vector4((minimumBounds.X + maximumBounds.X) / 2, (minimumBounds.Y + maximumBounds.Y) / 2, (minimumBounds.Z + maximumBounds.Z) / 2, 0), new Vector4((maximumBounds.X - minimumBounds.X) / 2 + BoundsMargin, (maximumBounds.Y - minimumBounds.Y) / 2 + BoundsMargin, (maximumBounds.Z - minimumBounds.Z) / 2 + BoundsMargin, 0));
            cachedBounds[groupIndex] = box;
            boxes.Add(box);
        }

        if (cameraPosition is { } viewPosition)
        {
            boxes.Sort((leftBounds, rightBounds) => (new Vector3(leftBounds.Center.X, leftBounds.Center.Y, leftBounds.Center.Z) - viewPosition).Magnitude.CompareTo((new Vector3(rightBounds.Center.X, rightBounds.Center.Y, rightBounds.Center.Z) - viewPosition).Magnitude));
        }

        var boxCount = Math.Min(boxes.Count, MaximumBoxes);
        for (var boxIndex = 0; boxIndex < boxCount; boxIndex++)
        {
            AtmosphereShader.SetVector(boxCenters[boxIndex], boxes[boxIndex].Center);
            AtmosphereShader.SetVector(boxExtents[boxIndex], boxes[boxIndex].Extents);
        }

        AtmosphereShader.SetFloat("_JA_CharBoxCount", boxCount);
    }

    public void Dispose()
    {
        ReleaseRenderers();
        try
        {
            AtmosphereShader.SetFloat("_JA_CharBoxCount", 0);
        }
        catch (Exception)
        {
        // the scene is going away
        }
    }
}
