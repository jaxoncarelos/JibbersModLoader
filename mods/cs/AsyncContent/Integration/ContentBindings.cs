using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace JibbersAsyncContent.Integration;

internal sealed class ContentBindings
{
    public static string CatalogueJson
    {
        get
        {
            using var stream = typeof(ContentBindings).Assembly.GetManifestResourceStream("AsyncContent.symbols.json")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    public readonly MethodRef Step = new("async_content.iterator.step");
    public readonly MethodRef Reset = new("async_content.importer.reset");
    public readonly MethodRef ReadText = new("async_content.importer.text");
    /// <summary>
    /// The synchronous zip-entry PNG decoder, or null on builds that decode session textures on a
    /// worker themselves (November 2026 onwards): textures then stay entirely on the game's path.
    /// </summary>
    public readonly MethodRef? ReadTexture;
    public readonly MethodRef Register = new("async_content.importer.register");
    public readonly MethodRef ParseMesh = new("async_content.obj_parser.parse");
    public readonly FieldRef Bytes = new("async_content.iterator.bytes");
    public readonly FieldRef Archive = new("async_content.iterator.archive");
    public readonly FieldRef Current = new("async_content.iterator.current");
    public readonly FieldRef State = new("async_content.iterator.state");
    public readonly Il2CppMethod EntryName = Il2Cpp.RequireClass("System.IO.Compression.dll", "System.IO.Compression.ZipArchiveEntry").Method("get_FullName", 0);
    public readonly Il2CppField EntryArchive = Il2Cpp.RequireClass("System.IO.Compression.dll", "System.IO.Compression.ZipArchiveEntry").Fields
        .Single(field => !field.IsStatic && field.TypeName == "System.IO.Compression.ZipArchive");

    public unsafe ContentBindings()
    {
        ReadTexture = OptionalMethod("async_content.texture_reader.texture");
        foreach (var method in new[] { Step, Reset, ReadText, ParseMesh, Register })
            if (method.NativeCode == 0) throw new NotSupportedException("Content method has no native implementation: " + method.Path);
        if (EntryName.IsNull) throw new NotSupportedException("Zip entry name API is unavailable.");
        _ = Bytes.Field;
        _ = Archive.Field;
        _ = Current.Field;
        _ = State.Field;
        // The first Int32 is only accepted as the state field after checking the native constructor.
        var iteratorClass = new ClassRef("async_content.iterator").Class;
        var instance = iteratorClass.New();
        using var root = new GameRef(instance);
        var sentinel = 123;
        iteratorClass.Method(".ctor", "System.Int32").Invoke(instance, (nint)(&sentinel));
        if (State.Get<int>(instance) != sentinel)
            throw new NotSupportedException("Archive coroutine state layout changed.");
        State.Set(instance, -1);
        if (Step.Invoke<bool>(instance))
            throw new NotSupportedException("Stopped archive coroutine unexpectedly resumed.");
    }
    static MethodRef? OptionalMethod(string path)
    {
        var method = new MethodRef(path);
        try { return method.NativeCode != 0 ? method : null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
    public string Path(nint entry) => Il2Cpp.ReadString(EntryName.Invoke(entry)).Replace('\\', '/');
    public bool OwnsEntry(nint iterator, nint entry)
    {
        var archive = Archive.GetObject(iterator);
        return archive != 0 && EntryArchive.GetObject(entry) == archive;
    }
}
