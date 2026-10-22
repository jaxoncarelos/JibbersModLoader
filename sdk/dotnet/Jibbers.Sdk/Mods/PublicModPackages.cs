using System.Reflection.PortableExecutable;

namespace Jibbers.Sdk;

/// <summary>Catalog upgrades keep their historical paths so installed bootstraps can migrate them.</summary>
internal static class PublicModPackages
{
    static readonly string[] PackageNames = { "jibbers_atmosphere", "jibbers_reshred", "jibbers_fpv" };

    public static IEnumerable<string> Files(string modsDirectory)
    {
        foreach (var name in PackageNames)
        {
            var path = Path.Combine(modsDirectory, name + ".dll");
            if (IsManaged(path)) yield return path;
        }
    }

    static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var assembly = new PEReader(stream);
            return assembly.HasMetadata && assembly.PEHeaders.CorHeader is not null;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException) { return false; }
    }
}
