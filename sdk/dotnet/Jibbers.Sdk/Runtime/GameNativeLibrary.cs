using System.Runtime.InteropServices;

namespace Jibbers.Sdk.Runtime;

/// <summary>Loads one of the game's own native plugins (such as MuJoCo's <c>pcore</c>) on Windows or macOS.</summary>
public static class GameNativeLibrary
{
    /// <summary>Loads <paramref name="name"/> (without extension) from the game; false if it is not present.</summary>
    public static bool TryLoad(string name, out nint library)
    {
        if (!OperatingSystem.IsMacOS()) return NativeLibrary.TryLoad(name + ".dll", out library);
        var app = Environment.GetEnvironmentVariable("JIBBERS_GAME_APP");
        if (app is not null)
            foreach (var candidate in new[] {
                $"Contents/Plugins/{name}.bundle/Contents/MacOS/{name}",
                $"Contents/Plugins/{name}.bundle/Contents/MacOS/lib{name}",
                $"Contents/Plugins/{name}.bundle", $"Contents/Frameworks/{name}.bundle",
                $"Contents/Plugins/lib{name}.dylib", $"Contents/Plugins/{name}.dylib",
                $"Contents/Frameworks/lib{name}.dylib", $"Contents/Frameworks/{name}.dylib" })
                if (File.Exists(Path.Combine(app, candidate)) && NativeLibrary.TryLoad(Path.Combine(app, candidate), out library)) return true;
        return NativeLibrary.TryLoad(name, out library);
    }
}
