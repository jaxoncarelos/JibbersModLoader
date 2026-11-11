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

    /// <summary>
    /// Build 26596007+: one-shots are FMOD events, <c>static void (EventReference, Vector3,
    /// (string, float)[] parameters)</c>. The 16-byte EventReference and 12-byte Vector3 are passed
    /// by reference (Windows x64), so both arrive as pointers.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void EventFunction(nint eventReference, nint position, nint parameters, nint method);

    [StructLayout(LayoutKind.Sequential)]
    record struct EventGuid(long A, long B);

    /// <summary>The game's named FMOD events (fields of its AudioEvents asset) and the sound names Rumble knows them by.</summary>
    static readonly (string Field, string Sound)[] NamedEvents =
    [
        ("jump", "skijumpsnow"), ("land", "skilandsnow"), ("bodyHit", "bodyhitsoft"), ("knockout", "knockout"), ("skiDetach", "ski_binding_detach"),
    ];
    const string EventsClass = "AudioEvents", EventReferenceType = "FMODUnity.EventReference";
    /// <summary>Body hits at or above this event intensity are felt as hard hits.</summary>
    internal const float HardHitIntensity = 0.6f;
    Detour<EventFunction>? oneShot;
    volatile Dictionary<EventGuid, string>? eventNames;
    bool eventNamesFailed;
    float eventNamesRetry;
    readonly HashSet<string> describedEvents = new();

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
            if (!method.IsNull && method.IsStatic)
            {
                play = Detour<PlayFunction>.Create(method.NativeCode, OnPlay);
                play.Enable();
                log("Rumble: following the game's sounds.");
            }
            else
            {
                var fmod = managerClass.Methods.FirstOrDefault(m => m.IsStatic && m.ReturnType == "System.Void"
                    && m.ParameterTypes.SequenceEqual(new[] { EventReferenceType, "UnityEngine.Vector3", "System.ValueTuple<System.String,System.Single>[]" }));
                if (fmod.IsNull || fmod.NativeCode == 0)
                    throw new InvalidOperationException("neither AudioManager.Play(string, float, float, float) nor its FMOD event one-shot was found");
                // Event names are read from the game's AudioEvents asset on the main thread, in Played().
                oneShot = Detour<EventFunction>.Create(fmod.NativeCode, OnEvent);
                oneShot.Enable();
            }
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

    /// <summary>GUIDs of the AudioEvents fields Rumble feels, read from the game's AudioEvents asset (main thread).</summary>
    Dictionary<EventGuid, string> EventNames()
    {
        var accessor = managerClass.Methods.FirstOrDefault(m => m.IsStatic && m.ParameterCount == 0 && m.ReturnType == EventsClass);
        if (accessor.IsNull)
            throw new InvalidOperationException("AudioManager's AudioEvents accessor not found");
        var events = accessor.Invoke(0);
        if (events == 0)
            throw new InvalidOperationException("the game's AudioEvents asset is not loaded");
        var type = Il2Cpp.ClassOf(events);
        var names = new Dictionary<EventGuid, string>();
        foreach (var (field, sound) in NamedEvents)
        {
            var f = type.Fields.FirstOrDefault(x => !x.IsStatic && x.Name == field && x.TypeName == EventReferenceType);
            if (f.IsNull)
                continue;
            var guid = f.Get<EventGuid>(events);
            if (guid != default)
                names.TryAdd(guid, sound);
        }
        if (names.Count == 0)
            throw new InvalidOperationException("none of the game's landing/crash events were found");
        return names;
    }

    /// <summary>Main thread (Played runs in the frame update): reads the event GUIDs once the asset is loaded.</summary>
    void ResolveEventNames()
    {
        if (oneShot is null || eventNames is not null || eventNamesFailed)
            return;
        if ((eventNamesRetry -= 1) > 0)
            return;
        eventNamesRetry = 120; // the asset loads with the game's audio: try again about every two seconds
        try
        {
            var names = EventNames();
            eventNames = names;
            log($"Rumble: following the game's FMOD events ({string.Join(", ", names.Values.Distinct())}).");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("not loaded"))
        {
        }
        catch (Exception exception)
        {
            eventNamesFailed = true;
            log("Rumble: the game's events can't be identified, so landings and crashes are measured instead: " + exception.Message);
        }
    }

    void OnEvent(nint eventReference, nint position, nint parameters, nint method)
    {
        try
        {
            if (eventReference != 0 && eventNames is { } names && names.TryGetValue(*(EventGuid*)eventReference, out var sound) && played.Count < 64)
            {
                var values = EventParameters(parameters).ToList();
                if (describedEvents.Count < 16 && describedEvents.Add(sound))
                    log($"Rumble: game event \"{sound}\" parameters [{string.Join(", ", values.Select(p => $"{p.Name}={p.Value:0.00}"))}].");
                // The event's normalized intensity (PLAYER_Landing_Intensity, PLAYER_Hit_Intensity) is
                // what the old one-shots passed as volume, and decides a soft or hard body hit.
                var intensity = values.Where(p => p.Name.Contains("Intensity", StringComparison.OrdinalIgnoreCase)).Select(p => (float?)p.Value).FirstOrDefault();
                var volume = intensity is { } i && float.IsFinite(i) ? Math.Clamp(i, 0, 1) : 1f;
                if (sound == "bodyhitsoft" && volume >= HardHitIntensity)
                    sound = "bodyhithard";
                played.Enqueue((sound, volume));
            }
        }
        catch (Exception)
        {
            // Never let a sound fail because of the vibration.
        }
        oneShot!.Original(eventReference, position, parameters, method);
    }

    /// <summary>The (name, value) pairs of a <c>(string, float)[]</c>: 16-byte elements, string then float.</summary>
    static IEnumerable<(string Name, float Value)> EventParameters(nint array)
    {
        var count = Math.Min(Il2Cpp.ArrayLength(array), 8);
        var list = new List<(string, float)>(count);
        for (var i = 0; i < count; i++)
        {
            var element = Il2Cpp.ArrayData(array) + i * 16;
            var name = *(nint*)element;
            list.Add((name == 0 ? "" : Il2Cpp.ReadString(name), *(float*)(element + 8)));
        }
        return list;
    }

    /// <summary>The sounds played since the last call, in order. The first time each is heard, it is logged.</summary>
    public IEnumerable<(string Name, float Volume)> Played()
    {
        ResolveEventNames();
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
        oneShot?.Dispose();
        oneShot = null;
        manager?.Dispose();
        manager = null;
    }
}
