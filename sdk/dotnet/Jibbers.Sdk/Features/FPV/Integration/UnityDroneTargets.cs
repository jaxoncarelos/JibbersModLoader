using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Fpv;

/// <summary>Read-only scene target selection, rooting and velocity estimation.</summary>
internal sealed class UnityDroneTargets(Follow follow) : IDroneTargets
{
    readonly GameRef followCamera = new(follow.Ptr);
    GameRef? lockedTransform;
    int lockedIdentity;
    DroneVector lastPosition;
    DroneVector velocity;
    public string? LockedName { get; private set; }

    Follow Follow => new(followCamera.Target);
    public DroneVector? LocalRiderPosition => ReadLocalRider()?.Position;
    public RecallReference? RecallReference => lockedTransform is not null ? new(lastPosition, velocity) : ReadLocalRider();

    RecallReference? ReadLocalRider()
    {
        var transform = Follow.Target;
        if (Unity.IsNull(transform))
            return null;
        var position = Unity.Call<Vector3>(transform, "get_position");
        var forward = Unity.Call<Vector3>(transform, "get_forward");
        var riderPosition = new DroneVector(position.X, position.Y, position.Z);
        return riderPosition.IsFinite ? new(riderPosition, new(forward.X, forward.Y, forward.Z)) : null;
    }

    public void Cycle(DroneVector dronePosition, DroneRotation viewRotation)
    {
        var candidates = FindCandidates().Select(candidate =>
        {
            var position = Unity.Position(candidate.Transform);
            var targetPosition = new DroneVector(position.X, position.Y, position.Z);
            return (candidate.Transform, candidate.Name, Position: targetPosition, Identity: Unity.Call<int>(candidate.Transform, "GetInstanceID"), Score: viewRotation.Forward.Dot((targetPosition - dronePosition).Normalized));
        }).Where(candidate => candidate.Position.IsFinite).OrderByDescending(candidate => candidate.Score).ToList();
        if (candidates.Count == 0)
            return;
        var index = lockedTransform is null ? 0 : (candidates.FindIndex(candidate => candidate.Identity == lockedIdentity) + 1) % candidates.Count;
        var selected = candidates[index];
        Release();
        lockedTransform = new GameRef(selected.Transform);
        lockedIdentity = selected.Identity;
        LockedName = selected.Name;
        lastPosition = selected.Position;
        velocity = default;
    }

    List<(nint Transform, string Name)> FindCandidates()
    {
        var localTransform = Follow.Target;
        var candidates = new List<(nint Transform, string Name)>();
        if (!Unity.IsNull(localTransform))
            candidates.Add((localTransform, "you"));
        var localRagdoll = new FieldRef("fpv.follow.target_ragdoll").GetObject(Follow.Ptr);
        foreach (var rider in Unity.FindObjects(SymbolTable.Class("player.ragdoll")))
        {
            if (Unity.IsNull(rider) || rider == localRagdoll)
                continue;
            var bones = Unity.GetComponentsInChildren(rider, Unity.Class("UnityEngine.Transform"), true);
            var target = new[]
            {
                "pelvis",
                "hips",
                "hip",
                "spine",
                "torso",
                "root"
            }.SelectMany(name => bones.Where(bone => Unity.Name(bone).Contains(name, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
            if (target == 0)
                target = Unity.Transform(rider);
            if (!Unity.IsNull(localTransform) && (Unity.Position(target) - Unity.Position(localTransform)).Magnitude < 1.5f)
                continue;
            candidates.Add((target, Unity.Name(rider)));
        }

        return candidates;
    }

    public FlightTarget? Update(float deltaTime)
    {
        if (lockedTransform is null)
            return null;
        if (Unity.IsNull(lockedTransform.Target))
        {
            Release();
            return null;
        }

        var position = Unity.Position(lockedTransform.Target);
        var targetPosition = new DroneVector(position.X, position.Y, position.Z);
        if (!targetPosition.IsFinite)
        {
            Release();
            return null;
        }

        if (deltaTime > 1e-4f)
        {
            var displacement = targetPosition - lastPosition;
            velocity = displacement.Length > 80 * deltaTime + 5 ? default : velocity + (displacement * (1 / deltaTime) - velocity) * DroneMath.Blend(deltaTime, 0.15f);
        }

        lastPosition = targetPosition;
        return new(targetPosition, velocity);
    }

    public void Release()
    {
        lockedTransform?.Dispose();
        lockedTransform = null;
        LockedName = null;
    }

    public void Dispose() => Cleanup.Run(Release, followCamera.Dispose);
}
