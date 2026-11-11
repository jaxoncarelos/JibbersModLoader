using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Jibbers.Sdk;
using Jibbers.Sdk.Symbols;
using JibbersAsyncContent.Integration;
using JibbersAsyncContent.Preparation;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

const string quad = "v 1 0 0\nv 0 1 0\nv 0 0 1\nv 1 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvt 0 1\nvn 1 0 0\nf 1/1/1 2/2/1 3/3/1 4/4/1\n";
foreach (var (width, height) in new[] { (4096, 4096), (1, 8), (8, 1), (7, 13), (1, 1) })
foreach (var fullChain in new[] { false, true })
{
    var length = fullChain ? MipmapGenerator.ByteCount(width, height) : checked(width * height * 4);
    var plan = new TextureUploadPlan(new(width, height, new byte[length], fullChain), 1024 * 1024);
    var offset = 0;
    var level = 0;
    var row = 0;
    var mipWidth = width;
    var mipHeight = height;
    while (plan.TryNext(out var batch))
    {
        Check(batch.Level == level && batch.Row == row && batch.Width == mipWidth && batch.ByteOffset == offset, "Upload rows overlap or skip pixels.");
        Check(batch.Rows > 0 && batch.Rows <= mipHeight - row && batch.Pixels * 4 <= Math.Max(1024 * 1024, mipWidth * 4), "Upload batch exceeds its bound.");
        offset += batch.Pixels * 4;
        row += batch.Rows;
        if (row == mipHeight) { level++; row = 0; mipWidth = Math.Max(1, mipWidth / 2); mipHeight = Math.Max(1, mipHeight / 2); }
    }
    Check(offset == length && !plan.TryNext(out _), "Upload did not visit every mip byte exactly once.");
}
Reject<ArgumentException>(() => new TextureUploadPlan(new(4, 4, new byte[4])));
unsafe
{
    byte* source = stackalloc byte[] { 3, 7, 11, 255 };
    var snapshot = ArchiveSnapshot.Copy((nint)source, 4);
    source[0] = 9;
    Check(snapshot.SequenceEqual(new byte[] { 3, 7, 11, 255 }), "Archive snapshot does not own its bytes.");
}
Reject<ArgumentOutOfRangeException>(() => ArchiveSnapshot.Copy(0, -1));
Reject<ArgumentNullException>(() => ArchiveSnapshot.Copy(0, 1));
Console.WriteLine("PASS bounded texture row coverage, mip offsets, completion and independent archive snapshots");
var geometry = ObjDecoder.Decode(quad, default);
Check(geometry.Vertices[0] == new Vector3(-1, 0, 0), "Native X reflection lost.");
Check(geometry.Normals.All(normal => normal == new Vector3(-1, 0, 0)), "Normals reflection lost.");
Check(geometry.Triangles.SequenceEqual(new[] { 0, 2, 1, 0, 3, 2 }), "Native polygon winding differs.");
var negative = ObjDecoder.Decode("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\nf 1 2 3\n", default);
Check(negative.Vertices.Length == 3 && negative.Normals.Length == 0, "Negative indices or normal fallback failed.");
Check(negative.Triangles.SequenceEqual(new[] { 0, 2, 1, 0, 2, 1 }), "Equivalent OBJ indices did not share native corner keys.");
Reject<NotSupportedException>(() => ObjDecoder.Decode("v NaN 0 0", default));
Reject<NotSupportedException>(() => ObjDecoder.Decode("v 0 0 0\nf 0 1 2", default));
Reject<NotSupportedException>(() => ObjDecoder.Decode("v 0 0 0\nf 1 2 3", default));
Reject<NotSupportedException>(() => ObjDecoder.Decode("curv 1 2 3", default));
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    Reject<OperationCanceledException>(() => ObjDecoder.Decode(quad, cancelled.Token));
}
Console.WriteLine("PASS OBJ reflection, winding, attributes, index validation, unsupported syntax and cancellation");

var png = PngFixture();
var pixels = await Task.Run(() => PngDecoder.Decode(png, default));
byte[] expected = [0, 0, 255, 255, 80, 40, 20, 0, 255, 0, 0, 255, 0, 255, 0, 128];
Check(pixels.Width == 2 && pixels.Height == 2 && pixels.Rgba.SequenceEqual(expected), "RGBA, alpha or texture origin changed.");
var excessive = (byte[])png.Clone();
BinaryPrimitives.WriteInt32BigEndian(excessive.AsSpan(16, 4), 100_000);
Reject<NotSupportedException>(() => PngDecoder.Dimensions(excessive));
Reject<NotSupportedException>(() => PngDecoder.Decode([1, 2, 3], default));
Console.WriteLine("PASS PNG channels, semi/zero alpha, bottom-left texture origin and dimension limits");
byte[] opaque = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 2, 0, 0, 255];
var floorMip = MipmapGenerator.Complete(new(2, 2, opaque), MipmapFilter.Floor, default);
var roundedMip = MipmapGenerator.Complete(new(2, 2, opaque), MipmapFilter.Rounded, default);
var evenMip = MipmapGenerator.Complete(new(2, 2, opaque), MipmapFilter.Even, default);
Check(floorMip.HasMipChain && floorMip.Rgba.Length == 20 && floorMip.Rgba[16] == 0 && roundedMip.Rgba[16] == 1 && evenMip.Rgba[16] == 0,
    "Mip rounding modes are not distinguished by the qualification fixtures.");
Check(opaque.Length == 16 && opaque[12] == 2, "Mip preparation modified the original source.");
var transparent = (byte[])opaque.Clone(); transparent[3] = 0;
Check(!MipmapGenerator.Complete(new(2, 2, transparent), MipmapFilter.Floor, default).HasMipChain, "Transparent data must retain native mip filtering.");
Check(MipmapGenerator.ByteCount(1, 8) == 60 && MipmapGenerator.ByteCount(8, 1) == 60, "Thin mip chains have wrong lengths.");
Console.WriteLine("PASS mip rounding discrimination, transparent fallback, source ownership and thin texture layouts");
try
{
    MipmapGenerator.Selected = MipmapFilter.Floor;
    using var source = typeof(JibbersAsyncContent.AsyncContentMod).Assembly.GetManifestResourceStream("AsyncContent.Fixtures.mip_rgb8_square.png")!;
    using var buffer = new MemoryStream(); source.CopyTo(buffer);
    var workerTexture = PngDecoder.Decode(buffer.ToArray(), default);
    Check(workerTexture.HasMipChain && workerTexture.Rgba.Length == MipmapGenerator.ByteCount(16, 16), "Qualified worker mips were not generated in the decoder's own allocation.");
    var nativeFallback = PngDecoder.Decode(png, default);
    Check(!nativeFallback.HasMipChain && nativeFallback.Rgba.AsSpan(0, nativeFallback.BaseBytes).SequenceEqual(expected), "Transparent mip fallback changed base pixels.");
}
finally { MipmapGenerator.Selected = MipmapFilter.None; }
Console.WriteLine("PASS qualified decoder mip allocation and transparent fallback preserve source pixels");
var fixtureAssembly = typeof(JibbersAsyncContent.AsyncContentMod).Assembly;
using (var manifestStream = fixtureAssembly.GetManifestResourceStream("AsyncContent.Fixtures.expected.json")!)
using (var manifest = JsonDocument.Parse(manifestStream))
{
    var fixtureCount = 0;
    foreach (var fixture in manifest.RootElement.EnumerateArray())
    {
        var name = fixture.GetProperty("name").GetString()!;
        using var fixtureStream = fixtureAssembly.GetManifestResourceStream("AsyncContent.Fixtures." + name + ".png")!;
        using var bytes = new MemoryStream();
        fixtureStream.CopyTo(bytes);
        var decoded = PngDecoder.Decode(bytes.ToArray(), default);
        var reference = fixture.GetProperty("expected").EnumerateArray().Select(value => value.GetByte()).ToArray();
        Check(decoded.Rgba.SequenceEqual(reference), "PNG fixture changed: " + name);
        fixtureCount++;
    }
    Console.WriteLine($"PASS {fixtureCount} PNG format fixtures: grayscale, palette, alpha, 16-bit samples, all filters, Adam7 and gamma metadata");
}
var corrupt = (byte[])png.Clone();
corrupt[45] ^= 1;
Reject<InvalidDataException>(() => PngDecoder.Decode(corrupt, default));
var extraCoordinates = ObjDecoder.Decode("v 0 0 0 1 0 0\nv 1 0 0 0 1 0\nv 0 1 0 0 0 1\nvt 0 0 0\nvt 1 0 0\nvt 0 1 0\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\nf 1/1 3/3 2/2\nignored metadata", default);
Check(extraCoordinates.Vertices.Length == 6 && extraCoordinates.Normals.Length == 6 && extraCoordinates.Normals[3] == new Vector3(0, 1, 0),
    "Native extended-OBJ or mixed normal semantics changed.");

var archiveBytes = Zip(("body.obj", Encoding.UTF8.GetBytes(quad)), ("skin.png", png), ("unknown.obj", "curv 1 2 3"u8.ToArray()));
var archive = await Task.Run(() => ArchiveDecoder.Decode(archiveBytes, default));
Check(archive.Assets.Count == 2 && archive.Fallbacks == 1, "Archive fallback coverage incorrect.");
Check(archive.Assets["body.obj"].Mesh!.Triangles.SequenceEqual(geometry.Triangles), "Archive mesh changed.");
Check(archive.Assets["skin.png"].Texture!.Rgba.SequenceEqual(expected), "Archive image changed.");
var duplicate = Zip(("a/b.obj", Encoding.UTF8.GetBytes(quad)), ("a\\b.obj", Encoding.UTF8.GetBytes(quad)));
Reject<NotSupportedException>(() => ArchiveDecoder.Decode(duplicate, default));
Reject<InvalidDataException>(() => ArchiveDecoder.Decode([1, 2, 3], default));
Console.WriteLine("PASS worker-owned ZIP extraction, prepared assets, unsupported-content fallback and duplicate-path rejection");
// Builds that decode session textures on their own worker: PNGs are left to the game, unread.
var meshesOnly = await Task.Run(() => ArchiveDecoder.Decode(archiveBytes, default, textures: false));
Check(meshesOnly.Assets.Count == 1 && meshesOnly.Assets.ContainsKey("body.obj") && !meshesOnly.Assets.ContainsKey("skin.png") && meshesOnly.Fallbacks == 1,
    "Native-texture builds must prepare meshes only and leave PNGs to the game.");
var meshesOnlyJson = ArchiveDecoder.Decode(Zip(("config.json", "{}"u8.ToArray()), ("skin.png", png)), default, textures: false);
Check(meshesOnlyJson.Assets.Count == 1 && meshesOnlyJson.Assets["config.json"].Text == "{}", "JSON extraction changed in native-texture mode.");
Console.WriteLine("PASS native-texture builds prepare OBJ/JSON only and leave PNG decoding to the game");

var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var active = 0;
var maximumActive = 0;
var queue = new PreparationQueue((bytes, emit, token) =>
{
    var current = Interlocked.Increment(ref active);
    maximumActive = Math.Max(maximumActive, current);
    started.TrySetResult();
    try
    {
        // Only the test worker waits. The production game thread never waits for preparation.
        gate.Task.Wait(token);
        token.ThrowIfCancellationRequested();
        return new(0, 0, 0, []);
    }
    finally { Interlocked.Decrement(ref active); }
});
var first = queue.TryStart(1, () => [1]) ?? throw new Exception("First import was rejected.");
await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
var second = queue.TryStart(1, () => [2]) ?? throw new Exception("Second import was rejected.");
Check(!first.Completion.IsCompleted, "Queue blocked admission or did not start.");
Check(queue.TryStart(1, () => throw new Exception("Snapshot should not be copied at capacity.")) is null, "Queue capacity was not enforced.");
Check(queue.TryStart(ContentLimits.ArchiveBytes + 1, () => []) is null, "Archive byte limit not enforced before copying.");
first.Dispose();
gate.TrySetResult();
try { await first.Completion; } catch (OperationCanceledException) { }
await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
second.Dispose();
for (var attempt = 0; attempt < 100 && queue.Retained != 0; attempt++) await Task.Delay(10);
Check(queue.Retained == 0 && maximumActive == 1, "Cancellation leaked slots or ran competing decoders.");
Reject<InvalidOperationException>(() => queue.TryStart(1, () => []));
Check(queue.Retained == 0, "Snapshot failure leaked admission slot.");
Console.WriteLine("PASS nonblocking queue admission, one worker, two retained imports, cancellation and slot cleanup");

var streaming = new PreparationQueue();
using (var stream = streaming.TryStart(archiveBytes.Length, () => archiveBytes)!)
{
    var delivered = new List<string>();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!stream.Completion.IsCompleted)
    {
        while (stream.TryRead(out var entry)) delivered.Add(entry!.Path);
        await Task.Delay(1, timeout.Token);
    }
    while (stream.TryRead(out var entry)) delivered.Add(entry!.Path);
    var summary = await stream.Completion;
    Check(delivered.Count == 2 && summary.Assets == 2 && summary.Fallbacks == 1, "Bounded stream lost its final asset.");
}
var blockedProducer = new PreparationQueue();
using (var stream = blockedProducer.TryStart(archiveBytes.Length, () => archiveBytes)!)
{
    await Task.Delay(30);
    stream.Dispose(); // The writer must cancel even if its bounded channel is full.
    try { await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
}
Console.WriteLine("PASS bounded asset streaming, final-entry draining and cancellation under channel backpressure");
var largeImage = PngFixture(4096, 4096, grayscale: true);
var largeArchive = Zip(("one.png", largeImage), ("two.png", largeImage), ("three.png", largeImage));
var largeQueue = new PreparationQueue();
using (var stream = largeQueue.TryStart(largeArchive.Length, () => largeArchive)!)
{
    long decodedBytes = 0;
    var delivered = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (!stream.Completion.IsCompleted)
    {
        while (stream.TryRead(out var entry)) { decodedBytes += entry!.Asset.Texture!.Rgba.Length; delivered++; }
        await Task.Delay(1, timeout.Token);
    }
    while (stream.TryRead(out var entry)) { decodedBytes += entry!.Asset.Texture!.Rgba.Length; delivered++; }
    var summary = await stream.Completion;
    Check(delivered == 3 && decodedBytes == 192L * 1024 * 1024 && summary.Fallbacks == 0,
        "A normal three-texture 4K set reverted to native decoding at the old 128 MiB limit.");
}
var jsonOnly = ArchiveDecoder.Decode(Zip(("config.json", Encoding.UTF8.GetBytes("{\"name\":\"雪\"}"))), default);
Check(jsonOnly.Assets["config.json"].Text == "{\"name\":\"雪\"}", "JSON text changed during background extraction.");
Console.WriteLine("PASS 192 MiB streamed 4K texture set without native fallback, plus UTF-8 JSON extraction");
try
{
    MipmapGenerator.Selected = MipmapFilter.PairFloorRows;
    var outfitArchive = Zip(Enumerable.Range(0, 6).Select(index => ($"map-{index}.png", largeImage)).ToArray());
    var outfitQueue = new PreparationQueue();
    using var stream = outfitQueue.TryStart(outfitArchive.Length, () => outfitArchive)!;
    long deliveredBytes = 0;
    var delivered = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    while (!stream.Completion.IsCompleted)
    {
        while (stream.TryRead(out var entry))
        {
            Check(entry!.Asset.Texture!.HasMipChain, "Large-outfit regression did not exercise prepared mips.");
            deliveredBytes += entry.Asset.Texture.Rgba.Length;
            delivered++;
        }
        await Task.Delay(1, timeout.Token);
    }
    while (stream.TryRead(out var entry)) { deliveredBytes += entry!.Asset.Texture!.Rgba.Length; delivered++; }
    var summary = await stream.Completion;
    Check(delivered == 6 && deliveredBytes == 6L * MipmapGenerator.ByteCount(4096, 4096) && summary.Fallbacks == 0,
        "Six-map 4K outfit crossed the old 384 MiB limit and reverted to blocking native decoding.");
}
finally { MipmapGenerator.Selected = MipmapFilter.None; }
Console.WriteLine("PASS six 4K outfit maps with full mip chains stream beyond 384 MiB without native fallback");
var largeEntry = GC.AllocateUninitializedArray<byte>(33 * 1024 * 1024);
largeEntry.AsSpan().Fill((byte)' ');
largeEntry[0] = (byte)'{'; largeEntry[1] = (byte)'}';
var acceptedLargeEntry = ArchiveDecoder.Decode(Zip(("large.json", largeEntry)), default);
Check(acceptedLargeEntry.Fallbacks == 0 && acceptedLargeEntry.Assets["large.json"].Text!.Length == largeEntry.Length,
    "The obsolete 32 MiB entry cap still forces valid game content onto the native path.");
Console.WriteLine("PASS entry above 32 MiB is admitted under the game's 64 MiB limit");

if (args is ["--dump", var dump])
{
    var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(dump))!;
    var catalogue = Catalogue.Parse(ContentBindings.CatalogueJson);
    Check(Shape.Lint(catalogue).Count == 0, "Production symbol catalogue failed lint.");
    var resolution = Shape.Resolve(catalogue, snapshot);
    Check(resolution.Failed.Count == 0 && resolution.Ok.Count == 3, "Production symbols did not resolve: " + string.Join(", ", resolution.Failed));
    var parser = resolution.Ok["async_content.obj_parser"].Members["parse"];
    // Native caller disassembly establishes this declaration as the real text-to-mesh entry point.
    Check(Shape.MemberName(snapshot, parser).StartsWith("cll(", StringComparison.Ordinal), "Validated parser ordinal changed.");
    var state = resolution.Ok["async_content.iterator"].Members["state"];
    Check(Shape.MemberName(snapshot, state) == "rla", "Validated coroutine state field changed.");
    Check(Shape.MemberName(snapshot, resolution.Ok["async_content.importer"].Members["register"]).StartsWith("gdn(", StringComparison.Ordinal),
        "Registration timing hook resolved the wrong signature.");
    Console.WriteLine("PASS production importer, iterator and parser bindings against the real game dump");
}

if (args is ["--assets", var assetRoot])
{
    var verified = 0;
    var failures = 0;
    foreach (var category in new[] { "Models", "Skins", "Clothes", "Characters" })
    foreach (var path in Directory.EnumerateFiles(Path.Combine(assetRoot, category), "*", SearchOption.AllDirectories))
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) PngDecoder.Decode(File.ReadAllBytes(path), default);
            else if (extension.Equals(".obj", StringComparison.OrdinalIgnoreCase)) ObjDecoder.Decode(File.ReadAllText(path), default);
            else continue;
            verified++;
        }
        catch (NotSupportedException error) { failures++; Console.WriteLine("Dataset fallback: " + Path.GetFileName(path) + ": " + error.Message); }
    }
    Check(failures == 0, "Installed asset coverage still has avoidable fallbacks.");
    Console.WriteLine($"PASS {verified} installed PNG/OBJ assets decode without native fallback");
}

static byte[] Zip(params (string Name, byte[] Bytes)[] files)
{
    using var memory = new MemoryStream();
    using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        foreach (var file in files)
        {
            using var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal).Open();
            entry.Write(file.Bytes);
        }
    return memory.ToArray();
}

static byte[] PngFixture(int width = 2, int height = 2, bool grayscale = false)
{
    using var output = new MemoryStream();
    output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    byte[] header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
    header[8] = 8; header[9] = grayscale ? (byte)0 : (byte)6;
    Chunk("IHDR", header);
    using var compressed = new MemoryStream();
    using (var deflater = new ZLibStream(compressed, CompressionLevel.Optimal, true))
    {
        if (grayscale)
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++) deflater.Write(row);
        }
        else deflater.Write(new byte[] { 0, 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 0, 255, 255, 80, 40, 20, 0 });
    }
    Chunk("IDAT", compressed.ToArray());
    Chunk("IEND", []);
    return output.ToArray();

    void Chunk(string type, byte[] data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, data.Length); output.Write(value);
        var name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var item in name.Concat(data))
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc); output.Write(value);
    }
}
