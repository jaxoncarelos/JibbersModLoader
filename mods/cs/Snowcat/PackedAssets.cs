using System.IO.Compression;
using System.Text;
using Jibbers.Sdk;
using SnowShovel;

namespace Snowcat;

/// <summary>
/// The snowcat's model and images, which travel inside the mod as base64 text (see <c>Packed/</c>):
/// the model as a deflated mesh (already in Unity's frame, as the OBJ loader read it), the images as
/// their JPEG / PNG files. No game calls.
/// </summary>
internal static class PackedAssets
{
    const int Magic = 0x4D434E53; // "SNCM"
    static MeshData? model;

    /// <summary>The snowcat model (8.5 m long, facing +z, standing on y 0), unpacked once.</summary>
    public static MeshData Model()
    {
        if (model is not null) return model;
        using var inflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(Packed.Data.Model)), CompressionMode.Decompress);
        using var r = new BinaryReader(inflate, Encoding.UTF8);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("the packed snowcat model is damaged");
        var count = r.ReadInt32();
        var hasUvs = r.ReadBoolean();
        var subs = new int[r.ReadInt32()][];
        var names = new string[subs.Length];
        for (var s = 0; s < subs.Length; s++)
        {
            names[s] = r.ReadString();
            subs[s] = new int[r.ReadInt32()];
            for (var i = 0; i < subs[s].Length; i++)
            {
                subs[s][i] = r.ReadInt32();
                if ((uint)subs[s][i] >= (uint)count) throw new InvalidDataException("the packed snowcat model is damaged");
            }
        }
        Vector3 V() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var vertices = new Vector3[count];
        for (var i = 0; i < count; i++) vertices[i] = V();
        var normals = new Vector3[count];
        for (var i = 0; i < count; i++) normals[i] = V();
        Vector2[]? uvs = null;
        if (hasUvs)
        {
            uvs = new Vector2[count];
            for (var i = 0; i < count; i++) uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        }
        return model = new MeshData(vertices, normals, subs, names, uvs);
    }

    /// <summary>The snowcat's paint: one texture atlas for the whole model (JPEG).</summary>
    public static byte[] Paint() => Convert.FromBase64String(Packed.Data.Paint);

    /// <summary>The tread pattern on the tracks (PNG).</summary>
    public static byte[] Tread() => Convert.FromBase64String(Packed.Data.Tread);
}
