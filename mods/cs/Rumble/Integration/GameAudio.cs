using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Interop;

namespace Rumble;

/// <summary>
/// What the game is playing, from its AudioManager: every one-shot sound as it starts (a detour of
/// the static <c>AudioManager.Play(name, volume, pitch, ...)</c>, which all of them go through), and
/// how loud its rail-slide loop is (the manager's array of loop volumes, indexed by its
/// <c>AudioManager.Loop</c> enum: SkiSlow, SkiFast, SkiRailSlide, Airborne, Powder).
/// </summary>
internal sealed unsafe class GameAudio : IDisposable
{
    const int RailSlideLoop = 2, LoopCount = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void PlayFunction(nint name, float volume, float pitch, float extra, nint method);

    readonly Action<string> log;
    readonly ConcurrentQueue<(string Name, float Volume)> played = new();
    readonly HashSet<string> named = new();
    readonly Il2CppClass managerClass;
    readonly Il2CppField loopVolumes;
    Detour<PlayFunction>? play;
    GameRef? manager;
    float rescanSeconds;
    bool loopsFailed;
    int railLogs;
    bool wasRailing;

    public GameAudio(Action<string> log)
    {
        this.log = log;
        managerClass = Il2Cpp.RequireClass("Assembly-CSharp.dll", "AudioManager");
        loopVolumes = managerClass.Fields.FirstOrDefault(f => !f.IsStatic && f.TypeName == "System.Single[]");
        try
        {
            var method = managerClass.Method("Play", "System.String", "System.Single", "System.Single", "System.Single");
            if (method.IsNull || !method.IsStatic)
                throw new InvalidOperationException("AudioManager.Play(string, float, float, float) not found");
            play = Detour<PlayFunction>.Create(method.NativeCode, OnPlay);
            play.Enable();
            log("Rumble: following the game's sounds.");
        }
        catch (Exception exception)
        {
            play?.Dispose();
            play = null;
            log("Rumble: the game's sounds can't be followed, so landings and crashes are measured instead: " + exception.Message);
        }
    }

    void OnPlay(nint name, float volume, float pitch, float extra, nint method)
    {
        try
        {
            if (name != 0 && played.Count < 64)
                played.Enqueue((Il2Cpp.ReadString(name), volume));
        }
        catch (Exception)
        {
            // Never let a sound fail because of the vibration.
        }
        play!.Original(name, volume, pitch, extra, method);
    }

    /// <summary>The sounds played since the last call, in order. The first time each is heard, it is logged.</summary>
    public IEnumerable<(string Name, float Volume)> Played()
    {
        while (played.TryDequeue(out var sound))
        {
            if (named.Count < 40 && named.Add(sound.Name))
                log($"Rumble: heard \"{sound.Name}\" at volume {sound.Volume:0.00}{(GameSounds.Classify(sound.Name) is { } kind ? $", felt as {kind}" : "")}.");
            yield return sound;
        }
    }

    /// <summary>Drops sounds heard while nothing is felt (paused, in a replay).</summary>
    public void Discard() => played.Clear();

    /// <summary>How loud the game is playing its rail-slide loop right now (0..1), or null if that can't be read.</summary>
    public float? RailSlide(float seconds)
    {
        if (loopsFailed || loopVolumes.IsNull)
            return null;
        try
        {
            if (manager is null || Unity.IsNull(manager.Target))
            {
                if ((rescanSeconds -= seconds) > 0)
                    return null;
                rescanSeconds = 2;
                manager?.Dispose();
                var found = Unity.FindObjects(managerClass).FirstOrDefault();
                manager = found == 0 ? null : new GameRef(found);
                if (manager is null)
                    return null;
            }
            var volumes = loopVolumes.GetObject(manager.Target);
            var count = Il2Cpp.ArrayLength(volumes);
            if (count != LoopCount)
            {
                loopsFailed = true;
                log($"Rumble: the game has {count} looped sounds, not {LoopCount}; grinds are detected from tricks instead.");
                return null;
            }
            var rail = ((float*)Il2Cpp.ArrayData(volumes))[RailSlideLoop];
            if (!float.IsFinite(rail))
                return 0;
            var railing = rail > 0.05f;
            if (railing && !wasRailing && railLogs++ < 4)
                log($"Rumble: rail sound playing at {rail:0.00}.");
            wasRailing = railing;
            return Math.Clamp(rail, 0, 1);
        }
        catch (Exception exception)
        {
            loopsFailed = true;
            log("Rumble: the game's rail sound can't be read; grinds are detected from tricks instead: " + exception.Message);
            return null;
        }
    }

    public void Dispose()
    {
        play?.Dispose();
        play = null;
        manager?.Dispose();
        manager = null;
    }
}
