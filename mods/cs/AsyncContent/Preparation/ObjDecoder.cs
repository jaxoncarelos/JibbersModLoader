using System.Globalization;
using Jibbers.Sdk;

namespace JibbersAsyncContent.Preparation;

/// <summary>Pure data preparation. Matches the native importer's reflected X axis and reversed triangle fan.</summary>
internal static class ObjDecoder
{
    public static MeshGeometry Decode(string text, CancellationToken cancellation)
    {
        var sourceVertices = new List<Vector3>();
        var sourceTexcoords = new List<Vector2>();
        var sourceNormals = new List<Vector3>();
        var vertices = new List<Vector3>();
        var texcoords = new List<Vector2>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();
        // The native corner cache keys the resolved position/UV/normal tuple, preserving seams.
        var corners = new Dictionary<(int Position, int Uv, int Normal), int>();
        bool? hasNormals = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            var comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    sourceVertices.Add(new(-Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "vt" when parts.Length >= 3:
                    sourceTexcoords.Add(new(Number(parts[1]), Number(parts[2])));
                    break;
                case "vn" when parts.Length >= 4:
                    sourceNormals.Add(new(-Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "f" when parts.Length >= 4:
                    var first = Corner(parts[1]);
                    var previous = Corner(parts[2]);
                    for (var corner = 3; corner < parts.Length; corner++)
                    {
                        var current = Corner(parts[corner]);
                        if (triangles.Count > ContentLimits.MeshIndices - 3) throw new NotSupportedException("Mesh index limit.");
                        triangles.Add(first);
                        triangles.Add(current);
                        triangles.Add(previous);
                        previous = current;
                    }
                    break;
                case "o": case "g": case "s": case "usemtl": case "mtllib":
                    break; // The native parser ignores these; materials are assigned by the importer.
                case "v": case "vt": case "vn": case "f":
                    throw new NotSupportedException("Incomplete OBJ geometry record: " + parts[0]);
                default:
                    break; // Match the original parser's handling of metadata/unsupported directives.
            }
            if (sourceVertices.Count + sourceTexcoords.Count + sourceNormals.Count > ContentLimits.MeshVertices * 3)
                throw new NotSupportedException("Mesh source limit.");
        }
        if (vertices.Count == 0 || triangles.Count == 0) throw new NotSupportedException("Empty mesh.");
        return new(vertices.ToArray(), texcoords.ToArray(), hasNormals == true ? normals.ToArray() : [], triangles.ToArray());

        int Corner(string token)
        {
            if (vertices.Count >= ContentLimits.MeshVertices) throw new NotSupportedException("Mesh vertex limit.");
            var indices = token.Split('/');
            if (indices.Length is < 1 or > 3) throw new NotSupportedException("OBJ corner syntax.");
            var normalPresent = indices.Length == 3 && indices[2].Length > 0;
            hasNormals = hasNormals == true || normalPresent;
            var positionIndex = Index(indices[0], sourceVertices.Count);
            var uvIndex = indices.Length > 1 && indices[1].Length > 0 ? Index(indices[1], sourceTexcoords.Count) : -1;
            var normalIndex = normalPresent ? Index(indices[2], sourceNormals.Count) : -1;
            var key = (positionIndex, uvIndex, normalIndex);
            if (corners.TryGetValue(key, out var existing)) return existing;
            var vertex = sourceVertices[positionIndex];
            var uv = uvIndex >= 0 ? sourceTexcoords[uvIndex] : default;
            // The game's native corner decoder uses Vector3.up for an omitted normal.
            var normal = normalIndex >= 0 ? sourceNormals[normalIndex] : new Vector3(0, 1, 0);
            var result = vertices.Count;
            vertices.Add(vertex);
            texcoords.Add(uv);
            normals.Add(normal);
            corners.Add(key, result);
            return result;
        }
    }

    static float Number(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            throw new NotSupportedException("Invalid OBJ coordinate.");
        return value;
    }

    static int Index(string text, int count)
    {
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new NotSupportedException("Invalid OBJ index.");
        var result = value > 0 ? value - 1 : count + value;
        return result >= 0 && result < count ? result : throw new NotSupportedException("OBJ index outside its source list.");
    }
}
