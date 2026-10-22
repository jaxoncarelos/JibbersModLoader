param([Parameter(Mandatory)][string]$RuntimeDir, [Parameter(Mandatory)][string]$ModsDir)
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
if (!core.StartsWith(bundle + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Probe used a machine runtime instead of the bundled runtime: " + core);
var sdk = AssemblyLoadContext.Default.LoadFromAssemblyPath(sdkPath);
var modBase = sdk.GetType("Jibbers.Sdk.JibbersMod", throwOnError: true)!;
var ids = new HashSet<string>();
foreach (var file in Directory.GetFiles(mods, "*.dll", SearchOption.AllDirectories))
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
if (!ids.SetEquals(new[] { "atmosphere", "reshred", "fpv" })) throw new Exception("Wrong public mod set");
Console.WriteLine($"PASS: {RuntimeInformation.FrameworkDescription}, bundled CoreLib: {core}");
Console.WriteLine("SDK and all three public C# mods loaded; no game process or game APIs used.");
'@
[IO.File]::WriteAllText((Join-Path $stage 'RuntimeProbe.csproj'), $project)
[IO.File]::WriteAllText((Join-Path $stage 'Program.cs'), $source)
& dotnet build (Join-Path $stage 'RuntimeProbe.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Runtime probe build failed' }
$previousRoot = $env:DOTNET_ROOT_X64
$previousMulti = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT_X64 = Join-Path $runtimePackage 'JibbersMods/dotnet/runtime'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    & (Join-Path $stage 'bin/Release/net8.0/RuntimeProbe.exe') $env:DOTNET_ROOT_X64 (Join-Path $runtimePackage 'JibbersMods/dotnet/Jibbers.Sdk.dll') (Join-Path $modPackage 'JibbersMods/cs')
    if ($LASTEXITCODE -ne 0) { throw 'Bundled runtime/SDK/mod loading smoke test failed' }
} finally {
    $env:DOTNET_ROOT_X64 = $previousRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $previousMulti
}
