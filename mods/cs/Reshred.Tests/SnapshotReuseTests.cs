using Reshred;

static unsafe class SnapshotReuseTests
{
    [Test]
    static void RecycledBuffersRemainIndependentAndCopyOnlyCapturedFields()
    {
        byte* memory = stackalloc byte[32];
        new Span<byte>(memory,32).Fill(7);
        (int Offset,int Size)[] fields = [(4,4),(16,8)];
        var first = RiderStateCapture.CaptureValues((nint)memory,fields,null);
        var second = RiderStateCapture.CaptureValues((nint)memory,fields,null);
        Assert.True(!ReferenceEquals(first,second));
        Assert.Equal(12,first.Length);
        new Span<byte>(memory,32).Fill(9);
        var recycled = RiderStateCapture.CaptureValues((nint)memory,fields,first);
        Assert.True(ReferenceEquals(first,recycled));
        Assert.True(recycled.All(b=>b==9));
        Assert.True(second.All(b=>b==7),"other history frames must not change");
        var resized = RiderStateCapture.CaptureValues((nint)memory,[(0,4)],recycled);
        Assert.Equal(4,resized.Length);
        Assert.True(!ReferenceEquals(resized,recycled));
    }
}
