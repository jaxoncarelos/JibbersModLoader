using System.Threading.Channels;

namespace JibbersAsyncContent.Preparation;

/// <summary>At most two retained imports, one CPU decoder, with cancellation and nonblocking admission.</summary>
internal sealed class PreparationQueue
{
    readonly SemaphoreSlim worker = new(1, 1);
    readonly Func<byte[], Action<PreparedEntry>, CancellationToken, PreparationSummary> decode;
    int retained;
    public int Retained => Volatile.Read(ref retained);
    public PreparationQueue(Func<byte[], Action<PreparedEntry>, CancellationToken, PreparationSummary>? decode = null) => this.decode = decode ?? ArchiveDecoder.Prepare;

    public Lease? TryStart(int byteCount, Func<byte[]> snapshot)
    {
        if (byteCount <= 0 || byteCount > ContentLimits.ArchiveBytes) return null;
        if (Interlocked.Increment(ref retained) > 2)
        {
            Interlocked.Decrement(ref retained);
            return null;
        }
        try
        {
            var bytes = snapshot(); // Caller owns the main-thread native-to-managed copy.
            if (bytes.Length != byteCount) throw new InvalidOperationException("Archive changed while copying.");
            return new Lease(this, bytes);
        }
        catch { Interlocked.Decrement(ref retained); throw; }
    }

    internal sealed class Lease : IDisposable
    {
        readonly PreparationQueue owner;
        readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        readonly Channel<PreparedEntry> entries = Channel.CreateBounded<PreparedEntry>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        int disposed;
        public Task<PreparationSummary> Completion { get; }
        public bool TryRead(out PreparedEntry? entry) => entries.Reader.TryRead(out entry);
        public Lease(PreparationQueue owner, byte[] bytes)
        {
            this.owner = owner;
            Completion = Task.Run(async () =>
            {
                await owner.worker.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    return owner.decode(bytes, entry =>
                    {
                        // Backpressure waits only on the decoder worker, never on Unity's thread.
                        entries.Writer.WriteAsync(entry, cancellation.Token).AsTask().GetAwaiter().GetResult();
                    }, cancellation.Token);
                }
                finally { entries.Writer.TryComplete(); owner.worker.Release(); }
            });
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation.Cancel();
            _ = Completion.ContinueWith(completed =>
            {
                _ = completed.Exception; // Observe failures even if an import is abandoned.
                cancellation.Dispose();
                Interlocked.Decrement(ref owner.retained);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
