using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RickBackflip;

/// <summary>
/// Joue un mp3 ou un wav : décodé en C# (SoundClip), volume appliqué sur les échantillons, puis envoyé à
/// Windows avec PlaySound (winmm.dll), qui existe sur tous les Windows. Tout se passe sur un thread à part :
/// le jeu n'est jamais bloqué.
/// </summary>
internal sealed class AudioPlayer
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    sealed class Entry
    {
        public DateTime Stamp;
        public Task<SoundClip> Clip = null!;
    }

    sealed class Playback
    {
        public readonly CancellationTokenSource Cancel = new();
        public volatile bool Done;
        public Thread? Thread;
    }

    readonly ConcurrentDictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    static readonly object playbackGate = new();
    static Playback? activePlayback;
    Playback? current;

    /// <summary>Vrai tant que le son joue (ou qu'il est en train de démarrer).</summary>
    public bool Playing => current is { Done: false };

    /// <summary>Décode le fichier à l'avance (en arrière-plan) pour que le son parte sans délai.</summary>
    public void Preload(string path) => GetEntry(path);

    Entry GetEntry(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (cache)
        {
            if (cache.TryGetValue(path, out var existing) && existing.Stamp == stamp) return existing;
            var entry = new Entry { Stamp = stamp, Clip = Task.Run(() => SoundClip.Load(path)) };
            cache[path] = entry;
            return entry;
        }
    }

    /// <summary>Lance le son depuis le début. Volume de 0 à 100.</summary>
    public void Play(string path, int volumePercent, Action<string>? log = null)
    {
        Stop();
        var entry = GetEntry(path);
        var playback = new Playback();
        var volume = Math.Clamp(volumePercent, 0, 100);
        lock (gate)
        {
            current = playback;
            playback.Thread = new Thread(() => Run(playback, entry, Path.GetFileName(path), volume, log))
            {
                IsBackground = true,
                Name = "RickBackflip audio"
            };
            playback.Thread.Start();
        }
    }

    public void Stop()
    {
        Playback? old;
        lock (gate)
        {
            old = current;
            current = null;
        }
        if (old is null) return;
        old.Cancel.Cancel();
        // The audio worker owns shutdown and memory. Never join it on a gameplay frame.
    }

    static void Run(Playback playback, Entry entry, string name, int volume, Action<string>? log)
    {
        var memory = IntPtr.Zero;
        var started = false;
        try
        {
            SoundClip clip;
            try
            {
                entry.Clip.Wait(playback.Cancel.Token);
                clip = entry.Clip.Result;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AggregateException e)
            {
                log?.Invoke($"Audio: cannot read {name}: {e.InnerException?.Message ?? e.Message}");
                return;
            }

            var wav = clip.BuildWav(volume / 100f);
            memory = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, memory, wav.Length);
            lock (playbackGate)
            {
                if (playback.Cancel.IsCancellationRequested) return;
                if (!PlaySound(memory, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT)) return;
                activePlayback = playback;
                started = true;
            }
            log?.Invoke($"Audio: playing {name} ({clip.Seconds:0.0} s, volume {volume})");

            // Attend la fin du son (sa durée est connue) ou l'annulation.
            playback.Cancel.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(clip.Seconds + 0.3));
        }
        catch (Exception e)
        {
            log?.Invoke("Audio: " + e.Message);
        }
        finally
        {
            lock (playbackGate)
            {
                // A canceled older worker must never stop a newer sound.
                if (started && ReferenceEquals(activePlayback, playback))
                {
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
                    activePlayback = null;
                }
            }
            if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
            playback.Done = true;
        }
    }
}
