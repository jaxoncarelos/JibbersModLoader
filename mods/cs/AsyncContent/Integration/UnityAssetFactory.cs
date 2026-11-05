using System.Runtime.InteropServices;
using System.Diagnostics;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAsyncContent.Preparation;

namespace JibbersAsyncContent.Integration;

/// <summary>Main-thread upload only; game-owned caches receive the results through the original importer.</summary>
internal sealed unsafe class UnityAssetFactory
{
    readonly Il2CppClass textureClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Texture2D");
    readonly Il2CppClass meshClass = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Mesh");
    readonly Il2CppField nativePointer = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Object").Field("m_CachedPtr");
    readonly delegate* unmanaged<nint, PixelSpan*, int, void> setPixels;
    readonly delegate* unmanaged<nint, byte, byte, void> apply;
    readonly delegate* unmanaged<nint, nint, int, int, int, int, byte> setRawMip;
    readonly delegate* unmanaged<nint, nint, ulong, byte> setRawChain;
    readonly delegate* unmanaged<nint, int, int, int, int, PixelSpan*, int, void> setPixelBlock;
    readonly Il2CppMethod uninitializedConstructor;
    readonly Il2CppMethod internalConstructor;
    readonly Il2CppMethod setVertices, setTexcoords, setNormals, setTriangles;
    public UnityAssetFactory()
    {
        setPixels = (delegate* unmanaged<nint, PixelSpan*, int, void>)Il2Cpp.ICall("UnityEngine.Texture2D::SetAllPixels32_Injected");
        apply = (delegate* unmanaged<nint, byte, byte, void>)Il2Cpp.ICall("UnityEngine.Texture2D::ApplyImpl_Injected");
        setRawMip = (delegate* unmanaged<nint, nint, int, int, int, int, byte>)Il2Cpp.ICall("UnityEngine.Texture2D::SetPixelDataImpl_Injected");
        setRawChain = (delegate* unmanaged<nint, nint, ulong, byte>)Il2Cpp.ICall("UnityEngine.Texture2D::LoadRawTextureDataImpl_Injected");
        setPixelBlock = (delegate* unmanaged<nint, int, int, int, int, PixelSpan*, int, void>)Il2Cpp.ICall("UnityEngine.Texture2D::SetBlockOfPixels32_Injected");
        uninitializedConstructor = textureClass.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Int32", "System.Boolean", "System.Boolean");
        internalConstructor = textureClass.Method(".ctor", "System.Int32", "System.Int32", "UnityEngine.TextureFormat", "System.Int32", "System.Boolean", "System.IntPtr", "System.Boolean", "UnityEngine.MipmapLimitDescriptor");
        if (setPixels == null || apply == null || nativePointer.IsNull || nativePointer.TypeName != "System.IntPtr")
            throw new NotSupportedException("Texture upload ABI is unavailable.");
        Require(textureClass.Method(".ctor", "System.Int32", "System.Int32"));
        Require(meshClass.Method(".ctor", Array.Empty<string>()));
        setVertices = Require(meshClass.Method("SetVertices", "System.Collections.Generic.List<UnityEngine.Vector3>"));
        setTexcoords = Require(meshClass.Method("SetUVs", "System.Int32", "System.Collections.Generic.List<UnityEngine.Vector2>"));
        setNormals = Require(meshClass.Method("SetNormals", "System.Collections.Generic.List<UnityEngine.Vector3>"));
        setTriangles = Require(meshClass.Method("SetTriangles", "System.Collections.Generic.List<System.Int32>", "System.Int32"));
        foreach (var method in new[] { "RecalculateNormals", "RecalculateBounds", "RecalculateTangents" }) Require(meshClass.Method(method, Array.Empty<string>()));
        ValidateVector("UnityEngine.Vector3", 12);
        ValidateVector("UnityEngine.Vector2", 8);
    }

    static Il2CppMethod Require(Il2CppMethod method) => !method.IsNull ? method : throw new NotSupportedException("Required Unity upload method was stripped.");
    static void ValidateVector(string name, int size)
    {
        var type = Il2Cpp.RequireClass(Unity.Core, name);
        if (!type.IsValueType || type.ValueSize != size) throw new NotSupportedException("Unity vector layout changed: " + name);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PixelSpan { public nint Begin; public int Length; }

    public GameRef Texture(TexturePixels pixels, Action<string, double>? observe = null)
    {
        var texture = textureClass.New();
        var root = new GameRef(texture);
        try
        {
            var mark = Stopwatch.GetTimestamp();
            var width = pixels.Width;
            var height = pixels.Height;
            ConstructTexture(texture, width, height);
            var native = nativePointer.Get<nint>(texture);
            if (native == 0) throw new InvalidOperationException("Texture native object is not initialized.");
            Mark("texture allocation", ref mark, observe);
            fixed (byte* rgba = pixels.Rgba)
            {
                if (pixels.HasMipChain && setRawChain != null)
                {
                    if (setRawChain(native, (nint)rgba, (ulong)pixels.Rgba.Length) == 0) throw new InvalidOperationException("Native full mip-chain copy failed.");
                }
                else if (setRawMip != null)
                {
                    if (setRawMip(native, (nint)rgba, 0, 1, pixels.BaseBytes, 0) == 0)
                        throw new InvalidOperationException("Native mip data copy failed.");
                }
                else
                {
                    var span = new PixelSpan { Begin = (nint)rgba, Length = checked(width * height) };
                    setPixels(native, &span, 0);
                }
            }
            Mark("texture pixel copy", ref mark, observe);
            // Keep the original constructor's mip chain and CPU readability.
            apply(native, pixels.HasMipChain && setRawChain != null ? (byte)0 : (byte)1, 0);
            Mark("texture mipmaps/GPU apply", ref mark, observe);
            return root;
        }
        catch { Unity.Destroy(texture); root.Dispose(); throw; }
    }

    public bool SupportsRawChain => setRawChain != null;
    public bool SupportsBatches => setPixelBlock != null;
    public bool BatchesVerified { get; set; }

    public TextureUpload BeginTextureUpload(TexturePixels pixels, int batchBytes = 1024 * 1024) => new(this, pixels, batchBytes);

    /// <summary>Owns an unpublished texture until all rows and mip levels are ready.</summary>
    internal sealed class TextureUpload : IDisposable
    {
        readonly UnityAssetFactory factory;
        readonly TexturePixels pixels;
        readonly TextureUploadPlan plan;
        GameRef? root;
        readonly nint native;
        bool ready;

        public TextureUpload(UnityAssetFactory factory, TexturePixels pixels, int batchBytes)
        {
            if (!factory.SupportsBatches) throw new NotSupportedException("Texture row upload is unavailable.");
            this.factory = factory;
            this.pixels = pixels;
            plan = new(pixels, batchBytes);
            var texture = factory.textureClass.New();
            root = new GameRef(texture);
            try
            {
                factory.ConstructTexture(texture, pixels.Width, pixels.Height);
                native = factory.nativePointer.Get<nint>(texture);
                if (native == 0) throw new InvalidOperationException("Texture native object is not initialized.");
            }
            catch { Dispose(); throw; }
        }

        // One bounded CPU copy per call. Apply is deliberately a separate step.
        public bool Step()
        {
            ObjectDisposedException.ThrowIf(root is null, this);
            if (ready) return true;
            if (plan.TryNext(out var batch))
            {
                fixed (byte* data = pixels.Rgba)
                {
                    var span = new PixelSpan { Begin = (nint)(data + batch.ByteOffset), Length = batch.Pixels };
                    factory.setPixelBlock(native, 0, batch.Row, batch.Width, batch.Rows, &span, batch.Level);
                }
                return false;
            }
            factory.apply(native, pixels.HasMipChain ? (byte)0 : (byte)1, 0);
            return ready = true;
        }

        public GameRef TakeResult()
        {
            if (!ready || root is null) throw new InvalidOperationException("Texture upload is not complete.");
            var result = root;
            root = null;
            return result;
        }

        public void Dispose()
        {
            if (root is null) return;
            try { Unity.Destroy(root.Target); }
            finally { root.Dispose(); root = null; }
        }
    }
    public string TexturePath => $"rawCopy={setRawMip != null}, fullMipCopy={setRawChain != null}, uninitializedCtor={(!uninitializedConstructor.IsNull && uninitializedConstructor.NativeCode != 0) || (!internalConstructor.IsNull && internalConstructor.NativeCode != 0)}";

    void ConstructTexture(nint texture, int width, int height)
    {
        const int rgba32 = 4;
        var format = rgba32;
        var allMips = -1;
        byte linear = 0, uninitialized = 1;
        if (!uninitializedConstructor.IsNull && uninitializedConstructor.NativeCode != 0)
        {
            uninitializedConstructor.Invoke(texture, (nint)(&width), (nint)(&height), (nint)(&format), (nint)(&allMips), (nint)(&linear), (nint)(&uninitialized));
            return;
        }
        if (!internalConstructor.IsNull && internalConstructor.NativeCode != 0)
        {
            var descriptor = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.MipmapLimitDescriptor");
            if (!descriptor.IsValueType || descriptor.ValueSize is <= 0 or > 256) throw new NotSupportedException("Mipmap descriptor layout changed.");
            Span<byte> defaults = stackalloc byte[descriptor.ValueSize];
            defaults.Clear();
            nint nativeTexture = 0;
            fixed (byte* data = defaults)
                internalConstructor.Invoke(texture, (nint)(&width), (nint)(&height), (nint)(&format), (nint)(&allMips), (nint)(&linear), (nint)(&nativeTexture), (nint)(&uninitialized), (nint)data);
            return;
        }
        textureClass.Method(".ctor", "System.Int32", "System.Int32").Invoke(texture, (nint)(&width), (nint)(&height));
    }

    public GameRef Mesh(MeshGeometry geometry, nint name, Action<string, double>? observe = null)
    {
        var mesh = meshClass.New();
        var root = new GameRef(mesh);
        try
        {
            var mark = Stopwatch.GetTimestamp();
            meshClass.Method(".ctor", Array.Empty<string>()).Invoke(mesh);
            Unity.Call(mesh, "set_name", name);
            if (geometry.Vertices.Length > ushort.MaxValue)
            {
                var indexFormat = 1;
                meshClass.Method("set_indexFormat", "UnityEngine.Rendering.IndexFormat").Invoke(mesh, (nint)(&indexFormat));
            }
            Mark("mesh allocation", ref mark, observe);
            using (var vertices = NativeList(setVertices, 0, geometry.Vertices)) setVertices.Invoke(mesh, vertices.Target);
            var channel = 0;
            using (var texcoords = NativeList(setTexcoords, 1, geometry.Texcoords)) setTexcoords.Invoke(mesh, (nint)(&channel), texcoords.Target);
            if (geometry.Normals.Length != 0)
                using (var normals = NativeList(setNormals, 0, geometry.Normals)) setNormals.Invoke(mesh, normals.Target);
            using (var triangles = NativeList(setTriangles, 0, geometry.Triangles)) setTriangles.Invoke(mesh, triangles.Target, (nint)(&channel));
            Mark("mesh array copy", ref mark, observe);
            if (geometry.Normals.Length == 0) Unity.Call(mesh, "RecalculateNormals");
            Mark("mesh normal generation", ref mark, observe);
            Unity.Call(mesh, "RecalculateBounds");
            Unity.Call(mesh, "RecalculateTangents");
            Mark("mesh bounds/tangent generation", ref mark, observe);
            return root;
        }
        catch { Unity.Destroy(mesh); root.Dispose(); throw; }
    }

    static void Mark(string stage, ref long mark, Action<string, double>? observe)
    {
        var now = Stopwatch.GetTimestamp();
        try { observe?.Invoke(stage, (now - mark) * 1000d / Stopwatch.Frequency); }
        catch { /* Diagnostics cannot change upload behavior. */ }
        mark = now;
    }

    static GameRef NativeList<T>(Il2CppMethod consumer, uint argument, T[] values) where T : unmanaged
    {
        var parameterType = Il2CppApi.method_get_param(consumer.Ptr, argument);
        var listClass = new Il2CppClass(Il2CppApi.class_from_type(parameterType));
        var itemsField = listClass.Field("_items");
        var sizeField = listClass.Field("_size");
        if (itemsField.IsNull || sizeField.IsNull || sizeField.TypeName != "System.Int32")
            throw new NotSupportedException("Native list layout unavailable.");
        var elementClass = itemsField.TypeClass.ElementClass;
        if (elementClass.IsNull || elementClass.ValueSize != sizeof(T)) throw new NotSupportedException("Native list element layout changed.");
        var array = Il2Cpp.NewArray(elementClass, values.Length);
        using var arrayRoot = new GameRef(array);
        values.AsSpan().CopyTo(new Span<T>((void*)Il2Cpp.ArrayData(array), values.Length));
        var list = listClass.New();
        var listRoot = new GameRef(list);
        try
        {
            Require(listClass.Method(".ctor", Array.Empty<string>())).Invoke(list);
            itemsField.SetObject(list, array);
            sizeField.Set(list, values.Length);
            return listRoot;
        }
        catch { listRoot.Dispose(); throw; }
    }
}
