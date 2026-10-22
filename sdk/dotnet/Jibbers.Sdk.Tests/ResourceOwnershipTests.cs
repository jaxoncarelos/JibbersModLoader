using Jibbers.Sdk;

static class ResourceOwnershipTests
{
    sealed class Resource(List<int> released, int identity, bool fail = false) : IDisposable
    {
        public void Dispose()
        {
            released.Add(identity);
            if (fail)
                throw new InvalidOperationException();
        }
    }

    [Test]
    static void FailedConstructionReleasesEveryResourceInReverseOrder()
    {
        var released = new List<int>();
        var ownership = new ResourceScope();
        ownership.Own(new Resource(released, 1));
        ownership.Own(new Resource(released, 2, true));
        ownership.Own(new Resource(released, 3));
        Assert.Throws<AggregateException>(ownership.Dispose);
        Assert.Equal("3,2,1", string.Join(',', released));
        ownership.Dispose();
        Assert.Equal(3, released.Count);
    }

    [Test]
    static void TransferredResourcesBelongToTheirNewOwner()
    {
        var released = new List<int>();
        using var ownership = new ResourceScope();
        var resource = ownership.Own(new Resource(released, 1));
        ownership.TransferOwnership();
        ownership.Dispose();
        Assert.Equal(0, released.Count);
        resource.Dispose();
        Assert.Equal(1, released.Count);
        Assert.Throws<ObjectDisposedException>(() => ownership.Own(resource));
    }
}
