using System.Text.Json.Nodes;
using Jibbers.Sdk.Game.Env;
using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Atmosphere;

/// <summary>Colour grading through the game's own Beautify volume and EnvironmentManager lighting fields; originals restored when off.</summary>
static unsafe class AtmosphereColorGrading
{
    static readonly Dictionary<(string Kind, string Name), JsonNode?> originalValues = new();
    static nint lastEnvironmentManager;

    static nint Beautify(nint environmentManager)
    {
        var field = Manager.Beautify_Field;
        return field.IsNull ? 0 : field.GetObject(environmentManager);
    }

    static JsonNode? Read(nint instance, string name)
    {
        var field = Il2Cpp.ClassOf(instance).Field(name);
        if (field.IsNull)
        {
            return null;
        }
        return field.TypeName switch
        {
            "System.Single" => field.Get<float>(instance),
            "System.Boolean" => field.Get<bool>(instance),
            "System.Int32" => field.Get<int>(instance),
            "UnityEngine.Color" => ColorComponents(field.Get<Color>(instance)),
            _ when field.TypeClass.IsEnum => field.Get<int>(instance),
            _ => null,
        };
    }

    static JsonArray ColorComponents(Color color) => new(color.R, color.G, color.B, color.A);

    static void Write(nint instance, string name, JsonNode? value)
    {
        var field = Il2Cpp.ClassOf(instance).Field(name);
        if (field.IsNull || value is null)
        {
            return;
        }
        double ReadNumber(JsonNode node) => double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        switch (field.TypeName)
        {
            case "System.Single":
                field.Set(instance, (float)ReadNumber(value));
                break;
            case "System.Boolean":
                field.Set(instance, value.GetValue<bool>());
                break;
            case "UnityEngine.Color" when value is JsonArray components:
                float Component(int index) => components.Count > index ? (float)ReadNumber(components[index]!) : 1;
                field.Set(instance, new Color(Component(0), Component(1), Component(2), Component(3)));
                break;
            default:
                field.Set(instance, (int)ReadNumber(value));
                break;
        }
    }

    public static void Apply(nint environmentManager, bool enabled, JsonObject settings)
    {
        lastEnvironmentManager = environmentManager;
        var beautifyInstance = Beautify(environmentManager);
        if (!enabled)
        {
            Restore();
            return;
        }
        if (beautifyInstance != 0 && settings["beautify"] is JsonObject beautifyValues)
        {
            foreach (var (name, value) in beautifyValues)
            {
                var parameterField = Il2Cpp.ClassOf(beautifyInstance).Field(name);
                var parameterInstance = parameterField.IsNull ? 0 : parameterField.GetObject(beautifyInstance);
                if (parameterInstance == 0)
                {
                    continue;
                }
                if (!originalValues.ContainsKey(("beautify", name)))
                {
                    originalValues[("beautify", name)] = Read(parameterInstance, "m_Value");
                    originalValues[("override", name)] = Read(parameterInstance, "m_OverrideState");
                }
                Write(parameterInstance, "m_Value", value);
                Write(parameterInstance, "m_OverrideState", true);
            }
        }
        if (settings["environment"] is JsonObject environmentValues)
        {
            foreach (var (name, value) in environmentValues)
            {
                if (!originalValues.ContainsKey(("env", name)))
                {
                    originalValues[("env", name)] = Read(environmentManager, name);
                }
                Write(environmentManager, name, value);
            }
        }
    }

    public static void Restore()
    {
        if (lastEnvironmentManager == 0 || originalValues.Count == 0)
        {
            return;
        }
        var beautifyInstance = Beautify(lastEnvironmentManager);
        foreach (var ((kind, name), value) in originalValues)
        {
            try
            {
                if (kind == "env")
                {
                    Write(lastEnvironmentManager, name, value);
                }
                else if (beautifyInstance != 0 && Il2Cpp.ClassOf(beautifyInstance).Field(name) is var parameterField && !parameterField.IsNull && parameterField.GetObject(beautifyInstance) is var parameterInstance && parameterInstance != 0)
                {
                    Write(parameterInstance, kind == "beautify" ? "m_Value" : "m_OverrideState", value);
                }
            }
            catch (Exception)
            {
                // the environment was replaced
            }
        }
        originalValues.Clear();
    }
}
