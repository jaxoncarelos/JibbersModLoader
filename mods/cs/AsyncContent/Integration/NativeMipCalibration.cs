using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAsyncContent.Preparation;

namespace JibbersAsyncContent.Integration;

/// <summary>Enables worker mip generation only when every pixel of every reference mip matches native PNG loading.</summary>
internal static unsafe class NativeMipCalibration
{
    sealed record Reference(TexturePixels Pixels, byte[][] Mips);
    public static bool VerifyBatches(UnityAssetFactory factory)
    {
        if (!factory.SupportsBatches) return false;
        var assembly = typeof(NativeMipCalibration).Assembly;
        var count = 0;
        // Exercise row boundaries, thin textures, alpha, and every prepared mip.
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".png", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var reference = ReadReference(bytes.ToArray());
            using var upload = factory.BeginTextureUpload(reference.Pixels, 12);
            while (!upload.Step()) { }
            using var uploaded = upload.TakeResult();
            try
            {
                var actual = ReadMips(uploaded.Target);
                if (actual.Length != reference.Mips.Length || !actual.Zip(reference.Mips, (a, b) => a.SequenceEqual(b)).All(equal => equal))
                    throw new NotSupportedException("Texture row upload changed pixels or mipmaps: " + name);
            }
            finally { Unity.Destroy(uploaded.Target); }
            count++;
        }
        return count >= 19;
    }
    public static MipmapFilter Find(UnityAssetFactory factory)
    {
        if (!factory.SupportsRawChain) return MipmapFilter.None;
        var references = new List<Reference>();
        var assembly = typeof(NativeMipCalibration).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.Contains(".mip_", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            references.Add(ReadReference(bytes.ToArray()));
        }
        if (references.Count < 4) return MipmapFilter.None;
        foreach (var mode in Enum.GetValues<MipmapFilter>().Where(mode => mode != MipmapFilter.None))
        {
            if (!references.All(reference => Matches(MipmapGenerator.Complete(reference.Pixels, mode, default), reference.Mips))) continue;
            // Verify the actual raw upload path as well, not just the arithmetic.
            var verified = true;
            foreach (var reference in references)
            {
                var pixels = MipmapGenerator.Complete(reference.Pixels, mode, default);
                using var uploaded = factory.Texture(pixels);
                try
                {
                    var actual = ReadMips(uploaded.Target);
                    if (actual.Length != reference.Mips.Length || !actual.Zip(reference.Mips, (a, b) => a.SequenceEqual(b)).All(equal => equal)) verified = false;
                }
                finally { Unity.Destroy(uploaded.Target); }
            }
            if (verified) return mode;
        }
        return MipmapFilter.None;
    }

    static bool Matches(TexturePixels pixels, byte[][] expected)
    {
        if (!pixels.HasMipChain || expected.Length < 2) return false;
        var offset = 0;
        foreach (var level in expected)
        {
            if (pixels.Rgba.Length - offset < level.Length || !pixels.Rgba.AsSpan(offset, level.Length).SequenceEqual(level)) return false;
            offset += level.Length;
        }
        return offset == pixels.Rgba.Length;
    }

    static Reference ReadReference(byte[] png)
    {
        var pixels = PngDecoder.Decode(png, default);
        var type = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Texture2D");
        var texture = type.New();
        using var root = new GameRef(texture);
        try
        {
            var size = 2;
            type.Method(".ctor", "System.Int32", "System.Int32").Invoke(texture, (nint)(&size), (nint)(&size));
            var encoded = Il2Cpp.NewArray(Il2Cpp.RequireClass("mscorlib.dll", "System.Byte"), png.Length);
            using var encodedRoot = new GameRef(encoded);
            png.CopyTo(new Span<byte>((void*)Il2Cpp.ArrayData(encoded), png.Length));
            var conversion = Il2Cpp.RequireClass("UnityEngine.ImageConversionModule.dll", "UnityEngine.ImageConversion");
            if (!conversion.Method("LoadImage", "UnityEngine.Texture2D", "System.Byte[]").Invoke<bool>(0, texture, encoded)) throw new NotSupportedException("Native mip fixture PNG failed.");
            return new(pixels, ReadMips(texture));
        }
        finally { Unity.Destroy(texture); }
    }

    static byte[][] ReadMips(nint texture)
    {
        var count = Unity.Call<int>(texture, "get_mipmapCount");
        var result = new byte[count][];
        var read = Il2Cpp.ClassOf(texture).Method("GetPixels32", "System.Int32");
        for (var level = 0; level < count; level++)
        {
            var index = level;
            var colors = read.Invoke(texture, (nint)(&index));
            using var root = new GameRef(colors);
            result[level] = new ReadOnlySpan<byte>((void*)Il2Cpp.ArrayData(colors), checked(Il2Cpp.ArrayLength(colors) * 4)).ToArray();
        }
        return result;
    }
}
