using JibbersAlpineVision;
using System.Text.Json;
using Jibbers.Sdk;
using System.Globalization;
static double Number(System.Text.Json.Nodes.JsonNode? value)=>double.Parse(value!.ToJsonString(),CultureInfo.InvariantCulture);
var profiles=new[]{"Natural","Alpine Film","Golden Hour","Bluebird"}.Select(VisualProfile.For).ToArray();
foreach(var p in profiles)
{
    var weather=p.Weather(false,1);
    if(weather["snowSurface"]!.GetValue<bool>()!=false || weather["ao"]!.GetValue<double>()!=0)throw new Exception("Native snow/AO configuration is wrong");
    if(p.SunIntensity<=0 || p.Exposure<=0 || p.Contrast<1)throw new Exception("Invalid profile");
    var low=p.Weather(true,1);
    if(Number(low["ao"])!=0)throw new Exception("Mod must leave game AO/shadows alone");
    Console.WriteLine("PASS "+p.Name+": native snow, preserved weather controls, native game shadows/AO");
}
if(profiles.Select(x=>x.SkyTop).Distinct().Count()!=4)throw new Exception("Profiles are identical");
if(new AlpineVisionMod().Settings.Any(s=>s.Key is "raytracing" or "rtKey"))throw new Exception("Removed ray tracing control survived");
RegressionTests.Run();
if(args.Length==1)
{
    float[] C(Color c)=>new[]{c.R,c.G,c.B};
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
    File.WriteAllText(args[0],JsonSerializer.Serialize(new {profiles=profiles.Select(p=>new {
        name=p.Name,sun=C(p.Sun),top=C(p.SkyTop),horizon=C(p.Horizon),ambient=C(p.AmbientSky),ground=C(p.AmbientGround),
        intensity=p.SunIntensity,elevation=p.Elevation,exposure=p.Exposure,contrast=p.Contrast,saturation=p.Saturation,warmth=p.Warmth,clouds=p.Clouds})}));
    Console.WriteLine("Exported authored profiles to "+args[0]);
}
