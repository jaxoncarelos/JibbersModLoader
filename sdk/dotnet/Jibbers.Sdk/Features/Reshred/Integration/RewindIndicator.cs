using Jibbers.Sdk.UI;

namespace Jibbers.Sdk.Features.Rewind;

/// <summary>Formats runtime feedback through the SDK's registered HUD API.</summary>
internal sealed class RewindIndicator : IRewindIndicator
{
    readonly HudPanel hud;
    readonly HudLabel progress;
    readonly Action<string> log;
    bool reportedFailure;

    public RewindIndicator(Action<string> log)
    {
        this.log = log;
        hud = Hud.Register("Rewind", new HudOptions { SortingOrder = 950 });
        progress = hud.AddText("Progress", new HudTextOptions
        {
            Anchor = HudAnchor.BottomCenter,
            Offset = new(0, 194),
            Size = new(1400, 100),
            FontSize = 30,
        });
    }

    public void Show(string text)
    {
        try
        {
            progress.Text = text;
            hud.Show();
            reportedFailure = false;
        }
        catch (Exception exception)
        {
            if (!reportedFailure)
            {
                reportedFailure = true;
                log("indicator unavailable: " + exception.Message);
            }
        }
    }

    public void Hide() => hud.Hide();
    public void Dispose() => hud.Dispose();
}