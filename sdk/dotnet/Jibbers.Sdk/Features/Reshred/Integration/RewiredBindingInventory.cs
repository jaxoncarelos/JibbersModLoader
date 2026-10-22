using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Discovers gameplay-only bindings and correctly identified west-face controller buttons.</summary>
internal sealed unsafe class RewiredBindingInventory(RewiredApi api)
{
    List<RewiredButton> DiscoverPads(nint controller)
    {
        string Safe(string propertyName)
        {
            try
            {
                return api.Text(controller, propertyName);
            }
            catch (Exception)
            {
                return "";
            }
        }

        var hardware = $"{Safe("get_name")} {Safe("get_hardwareName")} {Safe("get_hardwareIdentifier")}";
        using var ownership = new ResourceScope();
        var found = new List<RewiredButton>();
        using var buttons = api.Items(api.Call(controller, "get_Buttons"));
        foreach (var button in buttons)
        {
            var identifier = api.Call(button.Target, "get_elementIdentifier");
            if (RewiredPolicy.WestName(api.Text(identifier, "get_name"), hardware))
            {
                found.Add(new RewiredButton(ownership.Own(new GameRef(controller)), ownership.Own(new GameRef(button.Target))));
            }
            else
            {
                button.Dispose();
            }
        }

        ownership.TransferOwnership();
        return found;
    }

    public RewiredInventorySnapshot Scan(bool includeControllers)
    {
        using var ownership = new ResourceScope();
        var players = api.ReInput("get_players");
        var mapping = api.ReInput("get_mapping");
        var categories = new Dictionary<int, string>();
        using var mapCategories = api.Items(api.Call(mapping, "get_MapCategories"));
        foreach (var categoryReference in mapCategories)
        {
            categories[api.Int(categoryReference.Target, "get_id")] = api.Text(categoryReference.Target, "get_name");
            categoryReference.Dispose();
        }

        var bindings = new List<RewiredBinding>();
        var controllersFound = new List<RewiredButton>();
        var errors = new List<string>();
        var seenControllers = new HashSet<nint>();
        var seenMaps = new HashSet<nint>();
        using var playerObjects = api.Items(api.Call(players, "get_Players"));
        foreach (var player in playerObjects)
        {
            var playerControllers = api.ReferenceField(player.Target, "controllers");
            var maps = api.Call(api.ReferenceField(playerControllers, "maps"), "GetAllMaps", "System.Collections.Generic.IEnumerable<Rewired.ControllerMap>");
            using var controllerMaps = api.Items(maps);
            foreach (var mapRef in controllerMaps)
            {
                var map = mapRef.Target;
                if (!seenMaps.Add(map))
                {
                    continue;
                }

                var kind = api.Int(map, "get_controllerType");
                if (kind != 0 && !(kind == 2 && includeControllers))
                {
                    continue;
                }

                try
                {
                    var categoryId = api.Int(map, "get_categoryId");
                    if (!categories.TryGetValue(categoryId, out var category))
                    {
                        var findCategory = Il2Cpp.ClassOf(mapping).Method("GetMapCategory", "System.Int32");
                        var categoryIdArgument = categoryId;
                        category = api.Text(findCategory.Invoke(mapping, (nint)(&categoryIdArgument)), "get_name");
                    }

                    var gameplay = RewiredPolicy.GameplayCategory(category);
                    var controller = kind == 2 ? api.Call(map, "get_controller") : 0;
                    if (controller != 0 && seenControllers.Add(controller))
                    {
                        controllersFound.AddRange(DiscoverPads(controller).Select(ownership.Own));
                    }

                    if (!gameplay)
                    {
                        continue;
                    }

                    var westIds = new HashSet<int>();
                    foreach (var pad in controllersFound.Where(controllerButton => controllerButton.Controller.Target == controller))
                    {
                        westIds.Add(api.Int(api.Call(pad.Button.Target, "get_elementIdentifier"), "get_id"));
                    }

                    using var elements = api.Items(api.Call(map, "get_AllMaps"));
                    foreach (var element in elements)
                    {
                        var elementAddress = element.Target;
                        var elementId = kind == 2 ? api.Int(elementAddress, "get_elementIdentifierId") : 0;
                        var key = kind == 0 ? api.Int(elementAddress, "get_keyboardKeyCode") : 0;
                        var modifiers = kind == 0 ? api.Int(elementAddress, "get_modifierKeyFlags") : 0;
                        bindings.Add(new RewiredBinding(ownership.Own(new GameRef(element.Target)), kind, key, modifiers, kind == 2 && westIds.Contains(elementId)));
                    }
                }
                catch (Exception exception)when (kind == 2)
                {
                    // A joystick API mismatch must not discard keyboard bindings already found.
                    errors.Add(exception.Message);
                }
            }
        }

        ownership.TransferOwnership();
        return new(bindings, controllersFound, errors);
    }
}
