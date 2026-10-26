using Jibbers.Sdk;

namespace Fpv;

/// <summary>The persistence boundary: stable phone-menu keys are translated into typed flight options here.</summary>
internal static class DroneModSettings
{
    public static IEnumerable<Setting> Schema => new[]
    {
        Setting.Choice("mode", "Flight mode", new[] { "Acro", "Angle", "Cine" }, "Angle").InSection("Flight"),
        Setting.Slider("twr", "Thrust to weight", 4.5, 2, 8, 0.1).InSection("Flight"),
        Setting.Slider("top_speed", "Top speed (m/s)", 38, 15, 60, 1).InSection("Flight"),
        Setting.Slider("max_angle", "Angle mode max tilt", 50, 20, 80, 1).InSection("Flight"),
        Setting.Toggle("altitude_hold", "Angle mode altitude hold", true).InSection("Flight"),
        Setting.Slider("uptilt", "Camera uptilt", 25, 0, 60, 1).InSection("Flight"),
        Setting.Slider("fov", "Field of view", 85, 50, 130, 1).InSection("Flight"),
        Setting.Toggle("hud", "Show the OSD", true).InSection("Display"),
        Setting.Toggle("sound", "Drone sound", true).InSection("Display"),
        Setting.Slider("volume", "Drone volume", 0.7, 0, 1, 0.05).InSection("Display"),
        Setting.Slider("sensitivity", "Stick sensitivity", 0.6, 0.1, 1.5, 0.05).InSection("Rates"),
        Setting.Slider("rate_center", "Centre rate (deg/s)", 150, 50, 500, 5).InSection("Rates"),
        Setting.Slider("rate_max", "Max rate (deg/s)", 550, 200, 1500, 10).InSection("Rates"),
        Setting.Slider("expo", "Expo", 0.54, 0, 1, 0.01).InSection("Rates"),
        Setting.Slider("cine_speed", "Cine speed (m/s)", 12, 3, 30, 0.5).InSection("Cine"),
        Setting.Toggle("follow", "Follow the locked rider", true).InSection("Cine"),
        Setting.Toggle("chase", "Chase from behind", false).InSection("Cine"),
        Setting.Slider("follow_time", "Follow smoothing (seconds)", 0.4, 0.1, 2, 0.05).InSection("Cine"),
        Setting.Slider("max_accel", "Max acceleration (m/s²)", 25, 5, 60, 1).InSection("Cine"),
        Setting.Slider("turn_speed", "Gimbal turn speed", 4, 1, 12, 0.5).InSection("Cine"),
        Setting.Toggle("collisions", "Collide with the map", true).InSection("Physics"),
        Setting.Toggle("crash", "Disarm on hard crashes", false).InSection("Physics"),
        Setting.Slider("vibration", "Prop vibration", 0.3, 0, 1, 0.05).InSection("Physics"),
        Setting.Choice("throttle", "Gamepad throttle", new[] { "Left stick (centre hover)", "Left stick (full range)", "Right trigger" }, "Left stick (centre hover)").InSection("Input"),
        Setting.Toggle("invert", "Invert pitch", false).InSection("Input"),
        Setting.Toggle("mouse_look", "Mouse look", true).InSection("Input"),
        Setting.Slider("mouse_sens", "Mouse sensitivity", 0.03, 0.005, 0.3, 0.005).InSection("Input"),
        Setting.Slider("deadzone", "Stick deadzone", 0.08, 0, 0.3, 0.01).InSection("Input"),
        Setting.KeyBinding("toggle_key", "Take off / land", (int)Key.F9).InSection("Hotkeys"),
        Setting.KeyBinding("mode_key", "Cycle flight mode", (int)Key.F11).InSection("Hotkeys"),
        Setting.KeyBinding("lock_key", "Lock target (hold: release)", (int)Key.MouseMiddle).InSection("Hotkeys"),
        Setting.KeyBinding("recall_key", "Return to rider", (int)Key.Home).InSection("Hotkeys"),
        Setting.KeyBinding("tilt_up_key", "Tilt camera up", (int)Key.PageUp).InSection("Hotkeys"),
        Setting.KeyBinding("tilt_down_key", "Tilt camera down", (int)Key.PageDown).InSection("Hotkeys"),
        Setting.KeyBinding("fov_up_key", "Widen view", (int)Key.Add).InSection("Hotkeys"),
        Setting.KeyBinding("fov_down_key", "Narrow view", (int)Key.Subtract).InSection("Hotkeys"),
    };

    public static DroneOptions Read(JibbersMod mod)
    {
        float ReadFloatSetting(string key) => (float)mod.GetDouble(key);
        var flightSettings = new DroneFlightSettings
        {
            ThrustToWeight = ReadFloatSetting("twr"),
            TopSpeed = ReadFloatSetting("top_speed"),
            RateCenter = ReadFloatSetting("rate_center"),
            RateMax = ReadFloatSetting("rate_max"),
            Expo = ReadFloatSetting("expo"),
            Sensitivity = ReadFloatSetting("sensitivity"),
            MaxAngle = ReadFloatSetting("max_angle"),
            AltitudeHold = mod.GetBool("altitude_hold"),
            CineSpeed = ReadFloatSetting("cine_speed"),
            FollowTime = ReadFloatSetting("follow_time"),
            MaxAcceleration = ReadFloatSetting("max_accel"),
            TurnSpeed = ReadFloatSetting("turn_speed"),
            FollowTarget = mod.GetBool("follow"),
            Chase = mod.GetBool("chase"),
            Uptilt = ReadFloatSetting("uptilt"),
            Collisions = mod.GetBool("collisions"),
            Crash = mod.GetBool("crash")
        };
        var throttle = mod.GetString("throttle");
        var inputSettings = new PilotSettings
        {
            Throttle = throttle == "Right trigger" ? Throttle.Trigger : throttle == "Left stick (full range)" ? Throttle.FullRange : Throttle.CentreHover,
            InvertPitch = mod.GetBool("invert"),
            MouseLook = mod.GetBool("mouse_look"),
            MouseSensitivity = ReadFloatSetting("mouse_sens"),
            Deadzone = ReadFloatSetting("deadzone"),
            Keys = new[]
            {
                "toggle_key",
                "mode_key",
                "lock_key",
                "recall_key",
                "tilt_up_key",
                "tilt_down_key",
                "fov_up_key",
                "fov_down_key"
            }.Select(mod.GetKey).ToArray()
        };
        var mode = Enum.TryParse<FlightMode>(mod.GetString("mode"), out var parsedMode) ? parsedMode : FlightMode.Angle;
        var fieldOfView = ReadFloatSetting("fov");
        var vibration = ReadFloatSetting("vibration");
        return new(flightSettings, inputSettings, mode, fieldOfView, vibration, mod.GetBool("hud"), mod.GetBool("sound"), (float)mod.GetDouble("volume"));
    }
}
