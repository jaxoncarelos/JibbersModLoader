using System.Collections;

namespace Jibbers.Sdk.Features.Rewind;

internal sealed record RewiredBinding(GameRef Element, int Kind, int Key, int Modifiers, bool West);
internal sealed record RewiredButton(GameRef Controller, GameRef Button) : IDisposable
{
    public void Dispose() => Cleanup.Run(Button.Dispose, Controller.Dispose);
}

/// <summary>Owns transient roots created while enumerating Rewired collections.</summary>
internal sealed class RewiredObjectCollection(List<GameRef> items) : IEnumerable<GameRef>, IDisposable
{
    public IEnumerator<GameRef> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose()
    {
        var owned = items.ToArray();
        items.Clear();
        Cleanup.Run(owned.Select(item => (Action)item.Dispose).ToArray());
    }
}

internal sealed class RewiredInventorySnapshot(List<RewiredBinding> bindings, List<RewiredButton> controllers, List<string> errors) : IDisposable
{
    public List<RewiredBinding> Bindings => bindings;
    public List<string> ControllerErrors => errors;

    public List<RewiredButton> TakeControllers()
    {
        var transfer = controllers;
        controllers = new();
        return transfer;
    }

    public void Dispose()
    {
        var release = bindings.Select(binding => (Action)binding.Element.Dispose).Concat(controllers.Select(controller => (Action)controller.Dispose)).ToArray();
        bindings.Clear();
        controllers.Clear();
        Cleanup.Run(release);
    }
}
