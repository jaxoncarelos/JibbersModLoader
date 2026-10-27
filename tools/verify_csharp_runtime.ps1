param([Parameter(Mandatory)][string]$RuntimeDir, [Parameter(Mandatory)][string]$ModsDir, [switch]$Channel)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$runtimePackage = (Resolve-Path -LiteralPath $RuntimeDir).Path
$modPackage = (Resolve-Path -LiteralPath $ModsDir).Path
$stage = Join-Path $repo ('out/csharp-runtime-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><UseAppHost>true</UseAppHost>
  </PropertyGroup>
</Project>
'@
$source = @'
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

var bundle = Path.GetFullPath(args[0]);
var sdkPath = Path.GetFullPath(args[1]);
var mods = Path.GetFullPath(args[2]);
var core = typeof(object).Assembly.Location;
static string ComparablePath(string path)
{
    if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
    else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
    return Path.GetFullPath(path);
}
if (!ComparablePath(core).StartsWith(ComparablePath(bundle) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Probe used a machine runtime instead of the bundled runtime: " + core);
var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(sdkPath);
var modBase = sdk.GetType("Jibbers.Sdk.JibbersMod", throwOnError: true)!;
var ids = new HashSet<string>();
var channel = args.Length > 3 && args[3] == "channel";
// Channel packages live directly in JibbersMods. Historical backups are not active mods.
var files = Directory.GetFiles(mods, "*.dll", channel ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories).AsEnumerable();
if (channel)
    files = files.Where(file => new[] { "jibbers_atmosphere.dll", "jibbers_fpv.dll", "jibbers_reshred.dll" }.Contains(Path.GetFileName(file)));
foreach (var file in files)
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
    foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && modBase.IsAssignableFrom(t)))
    {
        var instance = Activator.CreateInstance(type)!;
        var id = (string)type.GetProperty("Id")!.GetValue(instance)!;
        var settings = ((IEnumerable)type.GetProperty("Settings")!.GetValue(instance)!).Cast<object>().ToList();
        if (!ids.Add(id) || settings.Count == 0) throw new Exception("Duplicate or empty mod: " + id);
        // Validate the menu schema and defaults without calling OnLoad or any game API.
        var store = sdk.GetType("Jibbers.Sdk.SettingsStore", true)!;
        Activator.CreateInstance(store, type.GetProperty("Settings")!.GetValue(instance), id, null);
        Console.WriteLine($"Loaded {type.FullName}: {settings.Count} settings validated");
    }
}
// The migration fixture enrolls only the mods that once had native versions; packages hold all five.
var expected = channel ? new[] { "alpinevision", "reshred", "fpv" } : new[] { "alpinevision", "reshred", "fpv", "slowmo", "rumble" };
if (!ids.SetEquals(expected)) throw new Exception("Wrong public mod set: " + string.Join(", ", ids));
Console.WriteLine($"PASS: {RuntimeInformation.FrameworkDescription}, bundled CoreLib: {core}");
Console.WriteLine($"SDK and {ids.Count} public C# mods loaded; no game process or game APIs used.");
'@
[IO.File]::WriteAllText((Join-Path $stage 'RuntimeProbe.csproj'), $project)
[IO.File]::WriteAllText((Join-Path $stage 'Program.cs'), $source)
& dotnet build (Join-Path $stage 'RuntimeProbe.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Runtime probe build failed' }
$previousRoot = $env:DOTNET_ROOT_X64
$previousMulti = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $sdkFolder = Join-Path $runtimePackage 'JibbersMods/dotnet'
    $managedMods = Join-Path $modPackage 'JibbersMods/cs'
    if ($Channel) {
        $package = Join-Path $runtimePackage 'JibbersMods/packages/jibbers_dotnet'
        $identity = [IO.File]::ReadAllText((Join-Path $package 'active.txt'))
        if ($identity -notmatch '^[a-f0-9]{64}$') { throw 'Invalid prepared runtime identity' }
        $sdkFolder = Join-Path $runtimePackage ('JibbersMods/system/clr/' + $identity.Substring(0,32))
        $managedMods = Join-Path $modPackage 'JibbersMods'
    }
    $env:DOTNET_ROOT_X64 = Join-Path $sdkFolder 'runtime'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $mode = if ($Channel) { 'channel' } else { 'manual' }
    & (Join-Path $stage 'bin/Release/net8.0/RuntimeProbe.exe') $env:DOTNET_ROOT_X64 (Join-Path $sdkFolder 'Jibbers.Sdk.dll') $managedMods $mode
    if ($LASTEXITCODE -ne 0) { throw 'Bundled runtime/SDK/mod loading smoke test failed' }
} finally {
    $env:DOTNET_ROOT_X64 = $previousRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $previousMulti
}
