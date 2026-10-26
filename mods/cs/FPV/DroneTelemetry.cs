using Jibbers.Sdk;
using System.Globalization;

namespace Fpv;

internal readonly record struct DroneTelemetry(FlightMode FlightMode, float Power, float Speed, float? Altitude, float FlightTime, string? Locked, bool MotorsDisarmed, bool Paused, bool UsingGamepad);
internal static class DroneTelemetryFormatter
{
    public static string[] Format(DroneTelemetry telemetry)
    {
        const string box = "<mark=#00000080 padding=\"14,14,6,6\">";
        var power = float.IsFinite(telemetry.Power) ? Math.Clamp(telemetry.Power, 0, 1) : 0;
        var bars = (int)MathF.Round(power * 10);
        var seconds = (int)MathF.Max(telemetry.FlightTime, 0);
        // Rider names are untrusted text, not TMP markup.
        var name = telemetry.Locked?.Replace("<", "〈").Replace(">", "〉");
        var top = new List<string>();
        if (telemetry.Paused)
            top.Add("PAUSED");
        if (name is not null)
            top.Add("<color=#7fe0ff>LOCK  " + name + "</color>");
        if (telemetry.FlightTime < 8)
            top.Add(telemetry.UsingGamepad ? "L3+R3 land   LB mode   RB lock (hold: release)   Y return" : "F9 land   F11 mode   Middle mouse lock   Home return");
        var altitude = telemetry.Altitude?.ToString("F1", CultureInfo.InvariantCulture) ?? "---";
        return new[]
        {
            $"{box}{telemetry.FlightMode.ToString().ToUpperInvariant()}{(telemetry.MotorsDisarmed ? "  <color=#ff5050>DISARMED</color>" : "")}\nTHR [{new string ('|', bars)}{new string ('.', 10 - bars)}] {power * 100:F0}%</mark>",
            FormattableString.Invariant($"{box}{telemetry.Speed * 3.6f:F0} km/h\nALT {altitude} m\n{seconds / 60:00}:{seconds % 60:00}</mark>"),
            top.Count > 0 ? box + string.Join("\n", top) + "</mark>" : "",
            telemetry.FlightMode == FlightMode.Cine ? "" : "<alpha=#B0>+",
        };
    }
}
