using System.Text.Json.Nodes;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Game.Map;

namespace Jibbers.Sdk.Features.Atmosphere;

internal static unsafe class UnityTerrain
{
    public static List<nint> MapTerrains()
    {
        var terrains = new List<nint>();
        foreach (var importer in Importer.All())
        {
            terrains.AddRange(Il2Cpp.ArrayObjects(importer.Terrains));
        }

        if (terrains.Count == 0)
        {
            terrains.AddRange(Unity.FindObjects(Il2Cpp.RequireClass("UnityEngine.TerrainModule.dll", "UnityEngine.Terrain")));
        }

        return terrains.Where(terrain => !Unity.IsNull(terrain)).ToList();
    }

    /// <summary>Lowest and highest terrain heights of the map (a coarse grid of samples) and its xz rectangle.</summary>
    public static (float MinimumHeight, float MaximumHeight, Vector4 Rectangle)? MeasureTerrainBounds()
    {
        float minimumHeight = float.MaxValue, maximumHeight = float.MinValue;
        var rectangle = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        foreach (var terrain in MapTerrains())
        {
            var terrainPosition = Unity.Position(terrain);
            var size = Unity.Call<Vector3>(Unity.Call(terrain, "get_terrainData"), "get_size");
            rectangle = new(MathF.Min(rectangle.X, terrainPosition.X), MathF.Min(rectangle.Y, terrainPosition.Z), MathF.Max(rectangle.Z, terrainPosition.X + size.X), MathF.Max(rectangle.W, terrainPosition.Z + size.Z));
            for (var xIndex = 0; xIndex <= 24; xIndex++)
            {
                for (var zIndex = 0; zIndex <= 24; zIndex++)
                {
                    var samplePosition = new Vector3(terrainPosition.X + size.X * xIndex / 24, 0, terrainPosition.Z + size.Z * zIndex / 24);
                    try
                    {
                        var height = Unity.Call<float>(terrain, "SampleHeight", (nint)(&samplePosition)) + terrainPosition.Y;
                        minimumHeight = MathF.Min(minimumHeight, height);
                        maximumHeight = MathF.Max(maximumHeight, height);
                    }
                    catch (Exception)
                    {
                    // a terrain without height data
                    }
                }
            }
        }

        return minimumHeight < float.MaxValue ? (minimumHeight, maximumHeight, rectangle) : null;
    }
}
