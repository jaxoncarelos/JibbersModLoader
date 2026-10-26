using Jibbers.Sdk;
using System.Runtime.InteropServices;
using Jibbers.Sdk.Game.Physics;
using Jibbers.Sdk.Game.Replay;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Reshred;

/// <summary>Captures only the rider/replay scalar fields that may safely be restored on live objects.</summary>
internal sealed unsafe class RiderStateCapture
{
    internal enum Kind
    {
        /// <summary>The replay clip: its int and double fields (frame counters, timestamps).</summary>
        ReplayClip,
        /// <summary>Ragdoll controller and its logic objects: obfuscated int and bool fields.</summary>
        RiderLogic,
    }

    /// <summary>Fields declared by game classes, including inherited game logic.</summary>
    static IEnumerable<Il2CppField> EnumerateScalarFields(Il2CppClass gameClass)
    {
        for (var baseClass = gameClass; !baseClass.IsNull && baseClass.Namespace.Length == 0; baseClass = baseClass.Parent)
        {
            foreach (var field in baseClass.Fields)
            {
                yield return field;
            }
        }
    }

    readonly Dictionary<(nint, Kind), (int Offset, int Size)[]> scalarLayouts = new();
    (int Offset, int Size)[] DescribeScalarLayout(nint instance, Kind kind)
    {
        var gameClass = Il2Cpp.ClassOf(instance);
        if (scalarLayouts.TryGetValue((gameClass.Ptr, kind), out var cachedLayout))
        {
            return cachedLayout;
        }

        var fields = new List<(int Offset, int Size)>();
        foreach (var field in EnumerateScalarFields(gameClass))
        {
            if (field.IsStatic)
            {
                continue;
            }

            var size = (kind, field.TypeName) switch
            {
                (Kind.ReplayClip, "System.Int32") => 4,
                (Kind.ReplayClip, "System.Double") => 8,
                (Kind.RiderLogic, "System.Int32")when Shape.IsObfuscated(field.Name) => 4,
                (Kind.RiderLogic, "System.Boolean")when Shape.IsObfuscated(field.Name) => 1,
                _ => 0,
            };
            if (size > 0)
            {
                fields.Add((field.Offset, size));
            }
        }

        return scalarLayouts[(gameClass.Ptr, kind)] = fields.ToArray();
    }

    readonly Dictionary<nint, Il2CppField[]> riderReferenceFieldsByClass = new();
    /// <summary>The objects whose state is saved now: the clip, the ragdoll controller and its obfuscated logic objects.</summary>
    List<(nint Address, Kind Kind)> FindTargets()
    {
        var output = new List<(nint, Kind)>();
        var recorder = Recorder.Instance;
        if (recorder.IsNull)
        {
            return output;
        }

        var clip = recorder.Clip;
        if (!clip.IsNull)
        {
            output.Add((clip.Ptr, Kind.ReplayClip));
        }

        var rider = recorder.Ragdoll;
        if (!rider.IsNull)
        {
            output.Add((rider.Ptr, Kind.RiderLogic));
            var riderClass = Il2Cpp.ClassOf(rider.Ptr);
            if (!riderReferenceFieldsByClass.TryGetValue(riderClass.Ptr, out var riderReferenceFields))
            {
                riderReferenceFields = EnumerateScalarFields(riderClass).Where(field => !field.IsStatic && !field.TypeClass.IsNull && !field.TypeClass.IsValueType && Shape.IsObfuscated(field.TypeName)).ToArray();
                riderReferenceFieldsByClass[riderClass.Ptr] = riderReferenceFields;
            }

            foreach (var field in riderReferenceFields)
            {
                var referencedObject = field.GetObject(rider.Ptr);
                if (referencedObject != 0 && !output.Any(target => target.Item1 == referencedObject))
                {
                    output.Add((referencedObject, Kind.RiderLogic));
                }
            }
        }

        return output;
    }

    internal sealed record ObjectSnapshot(nint Address, (int Offset, int Size)[] Fields, byte[] Values);
    internal sealed record LiveObjects(List<(nint Address, Kind Kind)> Targets)
    {
        public nint Rider => Targets.FirstOrDefault(target => target.Kind == Kind.RiderLogic).Address;
        public nint Clip => Targets.FirstOrDefault(target => target.Kind == Kind.ReplayClip).Address;
    }

    public LiveObjects FindLiveObjects() => new(FindTargets());
    public void Capture(LiveObjects objects, List<ObjectSnapshot> snapshots)
    {
        snapshots.Clear();
        foreach (var(address, kind)in objects.Targets)
        {
            var layout = DescribeScalarLayout(address, kind);
            var values = new byte[layout.Sum(field => field.Size)];
            var destinationOffset = 0;
            foreach (var(fieldOffset, byteCount)in layout)
            {
                new ReadOnlySpan<byte>((void*)(address + fieldOffset), byteCount).CopyTo(values.AsSpan(destinationOffset));
                destinationOffset += byteCount;
            }

            snapshots.Add(new(address, layout, values));
        }
    }

    public void Restore(IEnumerable<ObjectSnapshot> snapshots, LiveObjects liveObjects)
    {
        var liveAddresses = liveObjects.Targets.Select(target => target.Address).ToHashSet();
        foreach (var snapshot in snapshots)
        {
            if (!liveAddresses.Contains(snapshot.Address))
                continue;
            var sourceOffset = 0;
            foreach (var(fieldOffset, byteCount)in snapshot.Fields)
            {
                snapshot.Values.AsSpan(sourceOffset, byteCount).CopyTo(new Span<byte>((void*)(snapshot.Address + fieldOffset), byteCount));
                sourceOffset += byteCount;
            }
        }
    }
}
