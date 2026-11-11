using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAsyncContent.Preparation;

namespace JibbersAsyncContent.Integration;

/// <summary>Runs tiny local fixtures before any hook is enabled; refuses acceleration if Unity output differs.</summary>
internal static unsafe class NativeParityCheck
{
    public static void Run(UnityAssetFactory factory, ContentBindings bindings, bool textures = true)
    {
        if (textures) CheckTextures(factory);
        CheckMesh(factory, bindings, "v 1 0 0\nv 0 1 0\nv 0 0 1\nv 1 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n");
        CheckMesh(factory, bindings, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\nf 1 2 3\n");
        CheckMesh(factory, bindings, "v 0 0 0 1 0 0\nv 1 0 0 0 1 0\nv 0 1 0 0 0 1\nvt 0 0 0\nvt 1 0 0\nvt 0 1 0\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1 3/3 2/2\nignored metadata\n");
    }

    static void CheckTextures(UnityAssetFactory factory)
    {
        CheckTexture(factory);
        var assembly = typeof(NativeParityCheck).Assembly;
        foreach (var fixture in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".png", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(fixture)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            try { CheckTexture(factory, bytes.ToArray()); }
            catch (Exception error) { throw new NotSupportedException("PNG parity failed for " + fixture, error); }
        }
    }

    static void CheckTexture(UnityAssetFactory factory, byte[]? encodedPng = null)
    {
        var png = encodedPng ?? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAF0lEQVR4nGP4z8DwHwgbGID0/wANEQYAPLwGCNSI6DwAAAAASUVORK5CYII=");
        var pixels = PngDecoder.Decode(png, default);
        var textureClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Texture2D");
        var reference = textureClass.New();
        using var referenceRoot = new GameRef(reference);
        GameRef? candidate = null;
        try
        {
            var size = 2;
            textureClass.Method(".ctor", "System.Int32", "System.Int32").Invoke(reference, (nint)(&size), (nint)(&size));
            var encoded = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), png.Length);
            using var encodedRoot = new GameRef(encoded);
            png.CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(encoded), png.Length));
            var conversion = Il2Cpp.RequireClass("UnityEngine.ImageConversionModule.dll", "UnityEngine.ImageConversion");
            if (!conversion.Method("LoadImage", "UnityEngine.Texture2D", "System.Byte[]").Invoke<bool>(0, reference, encoded))
                throw new NotSupportedException("Native PNG fixture failed.");
            candidate = factory.Texture(pixels);
            var actual = candidate.Target;
            var readPixels = textureClass.Method("GetPixels32", "System.Int32");
            var zero = 0;
            var sourcePixels = readPixels.Invoke(reference, (nint)(&zero));
            using var sourceRoot = new GameRef(sourcePixels);
            var actualPixels = readPixels.Invoke(actual, (nint)(&zero));
            using var actualRoot = new GameRef(actualPixels);
            if (Il2Cpp.ArrayLength(sourcePixels) != pixels.Width * pixels.Height || Il2Cpp.ArrayLength(actualPixels) != pixels.Width * pixels.Height ||
                !new ReadOnlySpan<byte>((void*)Il2Cpp.ArrayData(sourcePixels), pixels.Rgba.Length).SequenceEqual(new ReadOnlySpan<byte>((void*)Il2Cpp.ArrayData(actualPixels), pixels.Rgba.Length)) ||
                Unity.Call<int>(reference, "get_mipmapCount") != Unity.Call<int>(actual, "get_mipmapCount"))
                throw new NotSupportedException("Async PNG upload differs from the original decoder; acceleration not enabled.");
        }
        finally
        {
            if (candidate is not null) { Unity.Destroy(candidate.Target); candidate.Dispose(); }
            Unity.Destroy(reference);
        }
    }

    static void CheckMesh(UnityAssetFactory factory, ContentBindings bindings, string text)
    {
        using var source = new GameRef(Il2Cpp.String(text));
        using var name = new GameRef(Il2Cpp.String("async-content-parity-check"));
        var original = bindings.ParseMesh.Invoke(0, source.Target, name.Target);
        using var originalRoot = new GameRef(original);
        GameRef? candidate = null;
        try
        {
            candidate = factory.Mesh(ObjDecoder.Decode(text, default), name.Target);
            foreach (var channel in new[] { "get_vertices", "get_normals", "get_uv", "get_triangles", "get_tangents" })
            {
                var expected = Unity.Call(original, channel);
                using var expectedRoot = new GameRef(expected);
                var actual = Unity.Call(candidate.Target, channel);
                using var actualRoot = new GameRef(actual);
                var element = Il2Cpp.ClassOf(expected).ElementClass;
                var count = Il2Cpp.ArrayLength(expected);
                var byteCount = checked(count * element.ValueSize);
                if (count != Il2Cpp.ArrayLength(actual) ||
                    !new ReadOnlySpan<byte>((void*)Il2Cpp.ArrayData(expected), byteCount).SequenceEqual(new ReadOnlySpan<byte>((void*)Il2Cpp.ArrayData(actual), byteCount)))
                    throw new NotSupportedException("Async OBJ output differs from the original parser: " + channel);
            }
        }
        finally
        {
            if (candidate is not null) { Unity.Destroy(candidate.Target); candidate.Dispose(); }
            Unity.Destroy(original);
        }
    }
}
