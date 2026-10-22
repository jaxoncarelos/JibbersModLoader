# FPV Drone

The mod entry point is [FPV.cs](FPV.cs). The complete C# implementation is in
[the SDK FPV feature folder](../../../sdk/dotnet/Jibbers.Sdk/Features/FPV):

- `DroneController.cs`: lifecycle, camera ownership, settings and target tracking.
- `Flight.cs` and `DroneMath.cs`: drone physics, flight modes and collision response.
- `PilotInput.cs` and `GameApi.cs`: controller, keyboard, mouse and game integration.
- `Effects.cs` and `Synth.cs`: HUD, audio and sound generation.
- `fpv_symbols.json`: game symbol definitions embedded in the SDK.

Build the SDK first, then this project. Use the matching SDK and mod DLLs together.
