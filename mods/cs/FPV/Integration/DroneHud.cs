using Jibbers.Sdk;
using Jibbers.Sdk.UI;

namespace Fpv;

/// <summary>Declares the drone HUD layout and updates its telemetry at 15 Hz.</summary>
internal sealed class DroneHud : IDisposable
{
    readonly HudPanel hud;
    readonly HudLabel[] labels;
    float secondsUntilRefresh;

    public DroneHud()
    {
        hud = Hud.Register("FPV");
        try
        {
            labels = new[]
            {
                hud.AddText("Flight", new HudTextOptions { Anchor = HudAnchor.BottomLeft, Offset = new(40, 40), Alignment = HudTextAlignment.BottomLeft, RichText = true }),
                hud.AddText("Navigation", new HudTextOptions { Anchor = HudAnchor.BottomRight, Offset = new(-40, 40), Alignment = HudTextAlignment.BottomRight, RichText = true }),
                hud.AddText("Controls", new HudTextOptions { Anchor = HudAnchor.TopCenter, Offset = new(0, -40), Alignment = HudTextAlignment.Top, FontSize = 26, RichText = true }),
                hud.AddText("Status", new HudTextOptions { Anchor = HudAnchor.Center, FontSize = 44, RichText = true }),
            };
            hud.Show();
        }
        catch { hud.Dispose(); throw; }
    }

    public void Update(float elapsedSeconds, DroneTelemetry telemetry)
    {
        secondsUntilRefresh -= elapsedSeconds;
        if (secondsUntilRefresh > 0) return;
        secondsUntilRefresh = 1 / 15f;
        var blocks = DroneTelemetryFormatter.Format(telemetry);
        for (var index = 0; index < labels.Length; index++) labels[index].Text = blocks[index];
    }

    public void Dispose() => hud.Dispose();
}
