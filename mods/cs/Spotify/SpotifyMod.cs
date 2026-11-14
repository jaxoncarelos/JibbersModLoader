using Jibbers.Sdk;
using Jibbers.Sdk.Interop;
using Spotify.Integration;

namespace Spotify;

/// <summary>
/// Spotify: a spinning record with the album art, title, artist and time in a corner of the game
/// screen while a song plays, with buttons, hotkeys and controller buttons; and a mini Spotify app on
/// the phone (now playing, seek, shuffle, repeat, and the songs played lately). Spotify is read and
/// controlled through Windows' own media controls, so no Spotify account or key is needed.
/// </summary>
public sealed class SpotifyMod : JibbersMod
{
    public override string Id => "spotify";
    public override string Name => "Spotify";
    public override string Author => "scotttucker1599";
    public override string Description => "A spinning record while Spotify plays, skip/seek/shuffle controls, and a mini Spotify app on the phone. No login.";

    [Choice("Show record", "Always", "Always", "On song change", "Never", Section = "Record")] public string ShowRecord { get; private set; } = "Always";
    [Choice("Record style", "Full", "Full", "Record only", Section = "Record")] public string Style { get; private set; } = "Full";
    [Choice("Record position", "Top left", "Top left", "Top right", "Bottom left", "Bottom right", Section = "Record")] public string Corner { get; private set; } = "Top left";
    [Slider("Record size", 100, 60, 160, 5, Section = "Record")] public double Size { get; private set; }
    [Slider("Opacity", 100, 20, 100, 5, Section = "Record")] public double Opacity { get; private set; }
    [Slider("Spin speed", 100, 0, 300, 10, Section = "Record")] public double SpinSpeed { get; private set; }
    [Toggle("Hide when paused", false, Section = "Record")] public bool HideWhenPaused { get; private set; }
    [Toggle("Colour from album art", true, Section = "Record")] public bool ArtColour { get; private set; }
    [Toggle("Pop on new song", true, Section = "Record")] public bool Pop { get; private set; }
    [Toggle("Only Spotify", true, Section = "Record")] public bool SpotifyOnly { get; private set; }
    [KeyBinding("Skip song", Key.RightBracket, Section = "Keys")] public Key NextKey { get; private set; }
    [KeyBinding("Previous song", Key.LeftBracket, Section = "Keys")] public Key PreviousKey { get; private set; }
    [KeyBinding("Play / pause", Key.Backslash, Section = "Keys")] public Key PauseKey { get; private set; }
    [KeyBinding("Back 10 seconds", Key.Semicolon, Section = "Keys")] public Key SeekBackKey { get; private set; }
    [KeyBinding("Forward 10 seconds", Key.Quote, Section = "Keys")] public Key SeekForwardKey { get; private set; }
    [Toggle("Controller (R3 skip, L3 back, both pause)", true, Section = "Keys")] public bool Controller { get; private set; }

    /// <summary>How long the record stays up after a song starts, in "On song change".</summary>
    const double ShownFor = 8;

    MediaSession? media;
    RecordHud? hud;
    PhoneApp? app;
    AlbumArt? art;
    History? history;
    Thread? loop;
    volatile bool stop;
    /// <summary>
    /// Whether a text box has the keyboard, read on the main thread each frame: <c>Input.Typing</c>
    /// asks the game's UI, and calling the game from this mod's own thread corrupts its memory manager.
    /// </summary>
    volatile bool typing;

    public override void OnLoad()
    {
        history = new History(History.DefaultFile);
        media = new MediaSession(Log, () => SpotifyOnly);
        hud = new RecordHud();
        app = new PhoneApp(history);
        art = new AlbumArt();
        loop = new Thread(Loop) { IsBackground = true, Name = "Spotify record" };
        loop.Start();
        Log("Spotify ready");
    }

    public override void OnUnload()
    {
        stop = true;
        media?.Dispose();
        var (h, a, c) = (hud, app, art);
        hud = null;
        app = null;
        art = null;
        try
        {
            MainThread.Run(() => Cleanup.Run(() => a?.Dispose(), () => h?.Dispose(), () => c?.Dispose()));
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
        }
    }

    /// <summary>About 30 times a second: the controls and history here, then the drawing on the main thread.</summary>
    void Loop()
    {
        var was = new bool[8];
        var angle = 0f;
        var lastError = "";
        var songKey = "";
        var changedAt = DateTime.MinValue;
        var wasPlaying = false;
        var bothSticks = false;
        var nextButtonCheck = DateTime.MinValue;
        var buttonSaid = "";
        while (!stop)
        {
            Thread.Sleep(33);
            try
            {
                Step();
            }
            catch (Exception e)
            {
                // Anything escaping this thread would end the game.
                var problem = $"{e.GetType().Name}: {e.Message}";
                if (problem != lastError) Log($"record loop: {lastError = problem}");
                Thread.Sleep(1000);
            }
        }

        void Step()
        {
            var np = Enabled ? media!.Now : null;
            var time = DateTime.UtcNow;
            if (np is not null)
            {
                if (np.ArtKey != songKey)
                {
                    songKey = np.ArtKey;
                    changedAt = time;
                    history!.Add(np.Title, np.Artist, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                if (np.Playing != wasPlaying) changedAt = time; // pausing and resuming show it again too
                wasPlaying = np.Playing;
                if (np.Playing)
                {
                    angle = (angle + 0.045f * (float)(SpinSpeed / 100)) % (2 * MathF.PI);
                    history!.Listened(0.033);
                }
            }
            var since = (time - changedAt).TotalSeconds;

            // Hotkeys and the controller.
            var focused = Enabled && Input.GameFocused && !typing;
            var pad = Controller && focused ? Gamepad.Read() : default;
            bool r3 = pad.Held(PadButton.RightStick), l3 = pad.Held(PadButton.LeftStick);
            // Both sticks together is play / pause; a stick alone skips when it's let go.
            if (r3 && l3) bothSticks = true;
            var held = new[]
            {
                focused && Input.IsDown(NextKey), focused && Input.IsDown(PreviousKey), focused && Input.IsDown(PauseKey),
                focused && Input.IsDown(SeekBackKey), focused && Input.IsDown(SeekForwardKey), r3, l3, focused && Input.IsDown(Key.MouseLeft),
            };
            var keys = new (Command, double)[] { (Command.Next, 0), (Command.Previous, 0), (Command.PlayPause, 0), (Command.SeekBy, -10), (Command.SeekBy, 10) };
            for (var i = 0; i < keys.Length; i++)
                if (held[i] && !was[i]) media!.Send(keys[i].Item1, keys[i].Item2);
            if (!r3 && !l3 && (was[5] || was[6]))
            {
                if (bothSticks) media!.Send(Command.PlayPause);
                else media!.Send(was[5] ? Command.Next : Command.Previous);
                bothSticks = false;
            }
            var click = held[7] && !was[7];
            Array.Copy(held, was, held.Length);

            var show = np is not null && ShowRecord != "Never" && !(HideWhenPaused && !np.Playing) && (ShowRecord == "Always" || since < ShownFor);
            var pop = Pop && since < 0.5 ? 0.12f * MathF.Exp(-(float)since * 9) : 0;
            var look = new RecordLook(Corner, (float)Size, Style == "Record only", (float)(Opacity / 100), ArtColour, angle, pop);
            var checkButton = time >= nextButtonCheck;
            if (checkButton) nextButtonCheck = time.AddSeconds(1);
            try
            {
                MainThread.Run(() =>
                {
                    typing = Input.Typing;
                    if (hud is null || app is null || art is null) return;
                    if (checkButton && Enabled)
                    {
                        // Each map builds a new phone: add the button whenever the current one lacks it.
                        var said = PhoneButton.Ensure(app.Show);
                        if (said != buttonSaid && said.Contains("added")) Log(said);
                        buttonSaid = said;
                    }
                    if (!Enabled) app.Close();
                    if (np is not null) art.Update(np);
                    if (click && Ui.Mouse() is { } mouse)
                    {
                        var fromApp = app.Click(mouse, np, out var handled);
                        if (fromApp is { } c) media!.Send(c.Command, c.Value);
                        else if (!handled)
                            foreach (var (command, x, y, hw, hh) in hud.Buttons)
                                if (MathF.Abs(mouse.X - x) <= hw && MathF.Abs(mouse.Y - y) <= hh) media!.Send(command);
                    }
                    app.Frame(np, art, ArtColour);
                    hud.Show(show ? np : null, art, look);
                });
                lastError = "";
            }
            catch (Exception e)
            {
                // The game is loading or minimised (a timeout), or something failed: say so once. Anything
                // escaping this thread would end the game, so everything is caught here.
                var problem = $"{e.GetType().Name}: {e.Message} {e.StackTrace?.Split(Environment.NewLine).Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Spotify."))}".Trim();
                if (problem != lastError && e is not TimeoutException) Log($"drawing failed: {lastError = problem}");
            }
        }
    }
}
