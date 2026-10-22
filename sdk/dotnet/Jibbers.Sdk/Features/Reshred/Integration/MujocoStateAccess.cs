using System.Runtime.InteropServices;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>The MuJoCo ABI boundary. No history, input or presentation logic belongs here.</summary>
internal sealed unsafe class MujocoStateAccess
{
    const uint IntegrationState = ((1u << 13) - 1) & ~1u;
    const int MaximumStateLength = 1_000_000;
    static readonly FieldRef SceneModel = new("physics.scene.model");
    static readonly FieldRef SceneData = new("physics.scene.data");
    delegate* unmanaged<nint, uint, int> stateSize;
    delegate* unmanaged<nint, nint, double*, uint, void> getState;
    delegate* unmanaged<nint, nint, double*, uint, void> setState;
    delegate* unmanaged<nint, nint, void> forward;
    internal readonly record struct Context(nint Model, nint Data, int StateLength);
    bool EnsureAvailable()
    {
        if (stateSize != null)
            return true;
        if (!NativeLibrary.TryLoad("pcore.dll", out var library))
            return false;
        try
        {
            // Resolve every export before publishing a usable API.
            var sizeExport = NativeLibrary.GetExport(library, "mj_stateSize");
            var captureExport = NativeLibrary.GetExport(library, "mj_getState");
            var restoreExport = NativeLibrary.GetExport(library, "mj_setState");
            var forwardExport = NativeLibrary.GetExport(library, "mj_forward");
            getState = (delegate* unmanaged<nint, nint, double*, uint, void>)captureExport;
            setState = (delegate* unmanaged<nint, nint, double*, uint, void>)restoreExport;
            forward = (delegate* unmanaged<nint, nint, void>)forwardExport;
            stateSize = (delegate* unmanaged<nint, uint, int>)sizeExport;
            return true; // Keep the library loaded while these function pointers can be used.
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public Context? OpenScene(nint scene)
    {
        if (!EnsureAvailable())
            return null;
        var model = SceneModel.Get<nint>(scene);
        var data = SceneData.Get<nint>(scene);
        if (model == 0 || data == 0)
            return null;
        var length = stateSize(model, IntegrationState);
        return length is > 0 and <= MaximumStateLength ? new(model, data, length) : null;
    }

    public void Capture(Context context, double[] destination)
    {
        if (destination.Length != context.StateLength)
            throw new ArgumentException("Snapshot size does not match the physics scene.");
        fixed (double* state = destination)
            getState(context.Model, context.Data, state, IntegrationState);
    }

    public void Restore(Context context, double[] snapshot)
    {
        if (snapshot.Length != context.StateLength || stateSize(context.Model, IntegrationState) != context.StateLength)
            throw new InvalidOperationException("Physics state size changed; wait for fresh history.");
        fixed (double* state = snapshot)
            setState(context.Model, context.Data, state, IntegrationState);
        forward(context.Model, context.Data);
    }
}
