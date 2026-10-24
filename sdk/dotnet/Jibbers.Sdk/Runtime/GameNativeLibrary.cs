using System.Runtime.InteropServices;

namespace Jibbers.Sdk.Runtime;

internal static class GameNativeLibrary
{
    internal static bool TryLoad(string name, out nint library)
    {
        if (!OperatingSystem.IsMacOS()) return NativeLibrary.TryLoad(name + ".dll", out library);
        var app = Environment.GetEnvironmentVariable("JIBBERS_GAME_APP");
        if (app is not null)
            foreach (var candidate in new[] {
                $"Contents/Plugins/{name}.bundle/Contents/MacOS/{name}",
                $"Contents/Plugins/{name}.bundle/Contents/MacOS/lib{name}",
                $"Contents/Plugins/lib{name}.dylib", $"Contents/Plugins/{name}.dylib",
                $"Contents/Frameworks/lib{name}.dylib", $"Contents/Frameworks/{name}.dylib" })
                if (File.Exists(Path.Combine(app, candidate)) && NativeLibrary.TryLoad(Path.Combine(app, candidate), out library)) return true;
        return NativeLibrary.TryLoad(name, out library);
    }
}
