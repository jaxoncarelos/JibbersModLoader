using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using JibbersAsyncContent.Preparation;

namespace JibbersAsyncContent.Integration;

/// <summary>Intercepts only the local archive coroutine and its synchronous asset readers.</summary>
internal sealed unsafe class GameContentAdapter : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte Advance(nint iterator, nint metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint ReadEntry(nint entry, nint metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint ParseMesh(nint text, nint name, nint metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Reset(nint metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DisposeIterator(nint iterator, nint metadata);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte RegisterEntry(nint name, nint reader, nint items, nint ids, nint metadata);

    sealed class ImportContext(GameContentAdapter owner, ImportOperation operation, nint iterator)
    {
        public GameContentAdapter Owner { get; } = owner;
        public ImportOperation Operation { get; } = operation;
        public nint Iterator { get; } = iterator;
        public nint MeshText { get; set; }
        public StagedAsset? Mesh { get; set; }
    }
    [ThreadStatic] static ImportContext? current;
    readonly int mainThread = Environment.CurrentManagedThreadId;
    readonly Func<bool> enabled;
    readonly Func<double> frameBudget;
    readonly Action<string> log;
    readonly ContentBindings bindings = new();
    readonly UnityAssetFactory assets = new();
    readonly PreparationQueue preparation = new();
    readonly Dictionary<nint, ImportOperation> imports = [];
    readonly List<IDisposable> hooks = [];
    readonly Il2CppMethod frameCount = Il2Cpp.RequireClass(Unity.Core, "UnityEngine.Time").Method("get_frameCount", 0);
    readonly Il2CppMethod interfaceDispose = Il2Cpp.RequireClass("mscorlib.dll", "System.IDisposable").Method("Dispose", 0);
    readonly nint gameModule;
    readonly delegate* unmanaged<nint, nint, nint> resolveVirtual;
    Detour<Advance>? advance;
    Detour<ReadEntry>? texture;
    Detour<ReadEntry>? text;
    Detour<ParseMesh>? mesh;
    Detour<Reset>? reset;
    Detour<RegisterEntry>? register;
    Detour<DisposeIterator>? disposeIterator;
    Il2CppMethod disposeImplementation;
    int lastFrame = -1;
    double frameWork;
    int completed, acceleratedTextures, acceleratedMeshes, admissionFallbacks;
    bool disposed;

    public GameContentAdapter(Func<bool> enabled, Func<double> frameBudget, Action<string> log)
    {
        this.enabled = enabled;
        this.frameBudget = frameBudget;
        this.log = log;
        if (frameCount.IsNull || interfaceDispose.IsNull) throw new NotSupportedException("Content lifecycle APIs are unavailable.");
        gameModule = NativeLibrary.Load(Path.Combine(Bootstrap.GameDir, "GameAssembly.dll"));
        try
        {
            MipmapGenerator.Selected = MipmapFilter.None;
            NativeParityCheck.Run(assets, bindings);
            SafeLog("Native PNG/OBJ parity fixtures passed (pixels, alpha, mipmaps, geometry, UVs, normals, winding and tangents).");
            try { MipmapGenerator.Selected = NativeMipCalibration.Find(assets); }
            catch (Exception error) { MipmapGenerator.Selected = MipmapFilter.None; SafeLog("Worker mip filtering retained native generation: " + error.Message); }
            try { assets.BatchesVerified = NativeMipCalibration.VerifyBatches(assets); }
            catch (Exception error) { SafeLog("Texture row upload disabled: " + error.Message); }
            SafeLog($"Texture row upload parity verified: {assets.BatchesVerified}.");
            SafeLog($"Texture upload path: {assets.TexturePath}; exact worker mip filter: {MipmapGenerator.Selected} (opaque power-of-two images only).");
            resolveVirtual = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(gameModule, "il2cpp_object_get_virtual_method");
            advance = bindings.Step.Detour<Advance>(AdvanceImport);
            hooks.Add(advance);
            texture = bindings.ReadTexture.Detour<ReadEntry>(ReadTexture);
            hooks.Add(texture);
            text = bindings.ReadText.Detour<ReadEntry>(ReadText);
            hooks.Add(text);
            mesh = bindings.ParseMesh.Detour<ParseMesh>(ParsePreparedMesh);
            hooks.Add(mesh);
            reset = bindings.Reset.Detour<Reset>(ResetContent);
            hooks.Add(reset);
            register = bindings.Register.Detour<RegisterEntry>(RegisterContent);
            hooks.Add(register);
            // All replacement callbacks can reach their original before interception begins.
            texture.Enable();
            text.Enable();
            mesh.Enable();
            reset.Enable();
            register.Enable();
            advance.Enable();
        }
        catch { Dispose(); throw; }
    }

    byte AdvanceImport(nint iterator, nint metadata)
    {
        if (Environment.CurrentManagedThreadId != mainThread || disposed) return advance!.Original(iterator, metadata);
        var originalCalled = false;
        byte originalResult = 0;
        ImportOperation? operation = null;
        try
        {
            imports.TryGetValue(iterator, out operation);
            if (!enabled())
            {
                Finish(iterator, "disabled; original loader resumed");
                originalCalled = true;
                return advance!.Original(iterator, metadata);
            }
            if (operation is null && bindings.State.Get<int>(iterator) == 0)
            {
                BudgetSpent(); // Initialize this frame's shared accounting before snapshotting.
                var bytes = bindings.Bytes.GetObject(iterator);
                var length = Il2Cpp.ArrayLength(bytes);
                var snapshotStarted = Stopwatch.GetTimestamp();
                PreparationQueue.Lease? lease;
                try { lease = preparation.TryStart(length, () => ArchiveSnapshot.Copy(Il2Cpp.ArrayData(bytes), length)); }
                finally
                {
                    var elapsed = Stopwatch.GetElapsedTime(snapshotStarted).TotalMilliseconds;
                    frameWork += elapsed;
                    if (elapsed >= 8) SafeLog($"Archive snapshot: {elapsed:0.00} ms ({length / 1024} KiB).");
                }
                if (lease is not null)
                {
                    operation = new ImportOperation(iterator, lease);
                    try
                    {
                        EnsureDisposeHook(iterator);
                        imports.Add(iterator, operation);
                    }
                    catch { operation.Dispose(); throw; }
                    SafeLog($"Preparing archive on worker ({length / 1024} KiB). Original validation will still run.");
                }
                else
                {
                    admissionFallbacks++;
                    SafeLog("Archive preparation unavailable or at capacity; original loader retained.");
                }
            }
            if (operation is not null && !operation.PreparationObserved)
            {
                if (BudgetSpent()) return Yield(iterator);
                if (operation.PendingTexture is not null)
                {
                    ContinueTexture(operation);
                    return Yield(iterator);
                }
                if (operation.Preparation.TryRead(out var entry))
                {
                    Stage(operation, entry!);
                    return Yield(iterator); // One unpublished asset upload per frame.
                }
                var task = operation.Preparation.Completion;
                // The producer can enqueue its last entry between TryRead and task completion.
                if (task.IsCompleted && operation.Preparation.TryRead(out var lastEntry))
                {
                    Stage(operation, lastEntry!);
                    return Yield(iterator);
                }
                if (!task.IsCompleted && Stopwatch.GetElapsedTime(operation.Started).TotalSeconds < 30)
                    return Yield(iterator);
                operation.PreparationObserved = true;
                if (task.IsCompletedSuccessfully)
                {
                    operation.Prepared = task.GetAwaiter().GetResult(); // Completed: never waits on the game thread.
                    SafeLog($"Stream prepared {operation.Prepared.Assets} assets in {operation.Prepared.Milliseconds:0.0} ms including queue backpressure; {operation.Prepared.Fallbacks} native decode fallbacks.");
                    foreach (var fallback in operation.Prepared.Details) SafeLog($"Decode fallback {Label(fallback.Path)}: {fallback.Reason}");
                }
                else
                {
                    operation.Preparation.Dispose();
                    var reason = task.Exception?.GetBaseException().Message ?? "timeout or cancellation";
                    SafeLog("Preparation stopped: " + reason + "; completed staged assets retained, remaining entries use the original loader.");
                }
            }
            if (operation is not null && BudgetSpent()) return Yield(iterator);
            var stateBefore = bindings.State.Get<int>(iterator);
            var previous = current;
            current = operation is not null ? new ImportContext(this, operation, iterator) : null;
            var started = Stopwatch.GetTimestamp();
            byte result;
            try
            {
                originalCalled = true;
                result = advance!.Original(iterator, metadata);
                originalResult = result;
            }
            finally
            {
                current = previous;
                var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                frameWork += milliseconds;
                try
                {
                    if (milliseconds >= 8) SafeLog($"Original import step state {stateBefore}->{bindings.State.Get<int>(iterator)}: {milliseconds:0.00} ms");
                }
                catch { }
                if (operation is not null)
                {
                    operation.LongestStepMilliseconds = Math.Max(operation.LongestStepMilliseconds, milliseconds);
                    operation.ReleaseTransfers();
                }
            }
            if (operation?.Cancelled == true)
            {
                Terminate(iterator);
                Finish(iterator, "cancelled on content reset");
                return 0;
            }
            if (result == 0 || operation?.NativeDisposed == true) Finish(iterator, "completed");
            return result;
        }
        catch (Exception error)
        {
            SafeLog("Import acceleration failed: " + error.Message);
            Finish(iterator, "fallback");
            // Never run an original step twice if it already started.
            return originalCalled ? originalResult : advance!.Original(iterator, metadata);
        }
    }

    byte Yield(nint iterator)
    {
        bindings.Current.SetObject(iterator, 0);
        return 1;
    }

    nint ReadTexture(nint entry, nint metadata)
    {
        var context = current;
        if (context?.Owner == this && context.Operation.PreparationObserved)
        {
            try
            {
                if (bindings.OwnsEntry(context.Iterator, entry) && context.Operation.Assets.TryGetValue(bindings.Path(entry), out var asset) && asset.IsTexture)
                {
                    var result = asset.HandOff();
                    context.Operation.Textures++;
                    acceleratedTextures++;
                    return result;
                }
            }
            catch (Exception error) { UploadFallback(context.Operation, error); }
        }
        var started = Stopwatch.GetTimestamp();
        var original = texture!.Original(entry, metadata);
        try
        {
            if (context?.Owner == this)
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                context.Operation.NativeTextureMilliseconds += elapsed;
                if (elapsed >= 8) SafeLog($"Native texture fallback {Label(bindings.Path(entry))}: {elapsed:0.00} ms");
            }
        }
        catch { /* Timing must never break the native reader. */ }
        return original;
    }

    nint ReadText(nint entry, nint metadata)
    {
        var context = current;
        if (context?.Owner == this && context.Operation.PreparationObserved)
        {
            try
            {
                if (bindings.OwnsEntry(context.Iterator, entry) && context.Operation.Assets.TryGetValue(bindings.Path(entry), out var asset) && asset.Text is { } source && !asset.IsTexture)
                {
                    var result = context.Operation.Transfer(new GameRef(Il2Cpp.String(source)));
                    context.MeshText = asset.IsMesh ? result : 0;
                    context.Mesh = asset.IsMesh ? asset : null;
                    return result;
                }
            }
            catch (Exception error) { UploadFallback(context.Operation, error); }
        }
        var started = Stopwatch.GetTimestamp();
        var original = text!.Original(entry, metadata);
        if (context?.Owner == this) context.Operation.NativeTextMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return original;
    }

    nint ParsePreparedMesh(nint source, nint name, nint metadata)
    {
        var context = current;
        if (context?.Owner == this && context.MeshText == source && context.Mesh is { } staged)
        {
            context.MeshText = 0;
            context.Mesh = null;
            try
            {
                var result = staged.HandOff();
                context.Operation.Meshes++;
                acceleratedMeshes++;
                return result;
            }
            catch (Exception error) { UploadFallback(context.Operation, error); }
        }
        var started = Stopwatch.GetTimestamp();
        var original = mesh!.Original(source, name, metadata);
        try
        {
            if (context?.Owner == this)
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                context.Operation.NativeMeshMilliseconds += elapsed;
                if (elapsed >= 8) SafeLog($"Native mesh fallback {Label(Il2Cpp.ReadString(name))}: {elapsed:0.00} ms");
            }
        }
        catch { /* Timing must never break the native parser. */ }
        return original;
    }

    byte RegisterContent(nint name, nint reader, nint items, nint ids, nint metadata)
    {
        var started = Stopwatch.GetTimestamp();
        var result = register!.Original(name, reader, items, ids, metadata);
        try
        {
            if (current?.Owner == this)
            {
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                current.Operation.RegistrationMilliseconds += elapsed;
                if (elapsed >= 8) SafeLog($"Content registration {Label(Il2Cpp.ReadString(name))}: {elapsed:0.00} ms (includes nested reads)");
            }
        }
        catch { /* Timing must never change registration or completion. */ }
        return result;
    }

    bool BudgetSpent()
    {
        var frame = frameCount.Invoke<int>(0);
        if (frame != lastFrame) { lastFrame = frame; frameWork = 0; }
        return frameWork >= Math.Clamp(frameBudget(), 1, 5);
    }

    void Stage(ImportOperation operation, PreparedEntry entry)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            StagedAsset staged;
            void Observe(string stage, double elapsed)
            {
                if (elapsed >= 8) SafeLog($"{stage} {Label(entry.Path)}: {elapsed:0.00} ms");
            }
            if (entry.Asset.Texture is { } pixels)
            {
                if (assets.BatchesVerified && pixels.BaseBytes > 1024 * 1024)
                {
                    operation.PendingTexture = assets.BeginTextureUpload(pixels);
                    operation.PendingTexturePath = entry.Path;
                    return;
                }
                staged = new(assets.Texture(pixels, Observe), null, true);
            }
            else if (entry.Asset.Mesh is { } geometry)
            {
                using var name = new GameRef(Il2Cpp.String(Path.GetFileNameWithoutExtension(entry.Path)));
                staged = new(assets.Mesh(geometry, name.Target, Observe), entry.Asset.Text, false);
            }
            else if (entry.Asset.Text is { } source) staged = new(null, source, false);
            else return;
            try { operation.Assets.Add(entry.Path, staged); }
            catch { staged.Dispose(); throw; }
            operation.UploadCount++;
        }
        catch (Exception error) { UploadFallback(operation, error); SafeLog($"Staging fallback {Label(entry.Path)}: {error.Message}"); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            operation.UploadMilliseconds += elapsed;
            operation.LongestUploadMilliseconds = Math.Max(operation.LongestUploadMilliseconds, elapsed);
            frameWork += elapsed;
            if (elapsed >= 8) SafeLog($"Staged upload {Label(entry.Path)}: {elapsed:0.00} ms");
        }
    }

    void ContinueTexture(ImportOperation operation)
    {
        var started = Stopwatch.GetTimestamp();
        var upload = operation.PendingTexture!;
        try
        {
            var remaining = Math.Max(0.1, Math.Clamp(frameBudget(), 1, 5) - frameWork);
            do
            {
                if (!upload.Step()) { operation.TextureBatches++; continue; }
                var staged = new StagedAsset(upload.TakeResult(), null, true);
                try { operation.Assets.Add(operation.PendingTexturePath!, staged); }
                catch { staged.Dispose(); throw; }
                operation.UploadCount++;
                operation.BatchedTextures++;
                upload.Dispose();
                operation.PendingTexture = null;
                operation.PendingTexturePath = null;
                break;
            } while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < remaining);
        }
        catch (Exception error)
        {
            UploadFallback(operation, error);
            SafeLog($"Texture row upload fallback {Label(operation.PendingTexturePath!)}: {error.Message}");
            upload.Dispose();
            operation.PendingTexture = null;
            operation.PendingTexturePath = null;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            frameWork += elapsed;
            operation.UploadMilliseconds += elapsed;
            operation.LongestUploadMilliseconds = Math.Max(operation.LongestUploadMilliseconds, elapsed);
            if (elapsed >= 8) SafeLog($"Texture row upload step: {elapsed:0.00} ms");
        }
    }

    static string Label(string path) => "\"" + System.Text.Json.JsonEncodedText.Encode(path.Length > 160 ? path[..160] : path).ToString() + "\"";

    void EnsureDisposeHook(nint iterator)
    {
        if (disposeIterator is not null) return;
        var implementation = new Il2CppMethod(resolveVirtual(iterator, interfaceDispose.Ptr));
        if (implementation.IsNull || implementation.DeclaringClass.Ptr != Il2Cpp.ClassOf(iterator).Ptr || implementation.NativeCode == 0)
            throw new NotSupportedException("Cannot resolve the archive iterator's actual IDisposable implementation.");
        disposeIterator = Detour<DisposeIterator>.Create(implementation.NativeCode, DisposeImport);
        disposeImplementation = implementation;
        hooks.Add(disposeIterator);
        disposeIterator.Enable();
    }

    void DisposeImport(nint iterator, nint metadata)
    {
        try
        {
            if (Environment.CurrentManagedThreadId == mainThread)
            {
                if (current?.Owner == this && imports.TryGetValue(iterator, out var operation) && current.Operation == operation)
                    operation.NativeDisposed = true; // Finish after the active step's timing and transfer cleanup.
                else Finish(iterator, "iterator disposed");
            }
        }
        catch (Exception error) { SafeLog("Iterator cleanup: " + error.Message); }
        disposeIterator!.Original(iterator, metadata);
    }
    void ResetContent(nint metadata)
    {
        try
        {
            if (Environment.CurrentManagedThreadId == mainThread) ClearImports("content reset", terminate: true);
            else MainThread.Later(() => ClearImports("content reset", terminate: true));
        }
        catch (Exception error) { SafeLog("Reset cleanup: " + error.Message); }
        reset!.Original(metadata);
    }
    void UploadFallback(ImportOperation operation, Exception error)
    {
        operation.UploadFallbacks++;
        if (operation.UploadFallbacks == 1) SafeLog("Unity upload fell back to the original loader: " + error.Message);
    }
    void Finish(nint iterator, string reason)
    {
        if (!imports.Remove(iterator, out var operation)) return;
        operation.Dispose();
        completed++;
        SafeLog($"Import {reason}: {operation.Textures} async-prepared textures, {operation.Meshes} async-prepared meshes, {operation.UploadFallbacks} upload fallbacks; longest original step {operation.LongestStepMilliseconds:0.00} ms; longest staged upload {operation.LongestUploadMilliseconds:0.00} ms; native texture {operation.NativeTextureMilliseconds:0.00} ms, mesh {operation.NativeMeshMilliseconds:0.00} ms, text {operation.NativeTextMilliseconds:0.00} ms; registration {operation.RegistrationMilliseconds:0.00} ms including nested reads; {operation.BatchedTextures} textures uploaded in {operation.TextureBatches} row batches.");
    }
    void Terminate(nint iterator)
    {
        // Dispose existing archive resources before marking the original iterator finished.
        disposeIterator!.Original(iterator, disposeImplementation.Ptr);
        bindings.State.Set(iterator, -1);
        bindings.Current.SetObject(iterator, 0);
    }
    void ClearImports(string reason, bool terminate = false)
    {
        foreach (var iterator in imports.Keys.ToArray())
        {
            var operation = imports[iterator];
            if (terminate && current?.Owner == this && current.Operation == operation)
            {
                operation.Cancelled = true; // End it when the synchronous native step returns.
                operation.Preparation.Dispose();
                continue;
            }
            try { if (terminate) Terminate(iterator); }
            finally { Finish(iterator, reason); }
        }
    }
    void SafeLog(string message) { try { log(message); } catch { } }
    public void CancelImports() => ClearImports("disabled; original loader retained");
    public JsonObject Status() => new()
    {
        ["pendingImports"] = imports.Count, ["retainedPreparations"] = preparation.Retained,
        ["completedImports"] = completed, ["acceleratedTextures"] = acceleratedTextures,
        ["acceleratedMeshes"] = acceleratedMeshes, ["admissionFallbacks"] = admissionFallbacks
    };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearImports("mod unloaded");
        for (var index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose();
        hooks.Clear();
        if (gameModule != 0) NativeLibrary.Free(gameModule);
    }
}
