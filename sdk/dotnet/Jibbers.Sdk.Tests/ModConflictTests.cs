using Jibbers.Sdk;

static class ModConflictTests
{
    sealed class Mod(string id, params string[] conflicts) : JibbersMod
    {
        public override string Id => id;
        public override string Name => id;
        public override IReadOnlyList<string> Conflicts => conflicts;
    }
    [Test] static void ReplacementLoadsBeforeLegacyRegardlessOfFileOrder()
    {
        var legacy=new Mod("atmosphere");
        var alpine=new Mod("alpinevision","atmosphere","raytracing");
        var first=ModConflicts.Order(new[]{legacy,alpine,new Mod("raytracing")}).First();
        Assert.Equal<JibbersMod>(alpine,first);
        Assert.True(ModConflicts.Find(legacy,new[]{first}) is not null);
    }
    [Test] static void ConflictIsSymmetricAndBlocksLegacyCopies()
    {
        var alpine = new Mod("alpinevision", "atmosphere", "raytracing");
        foreach(var id in new[]{"atmosphere","raytracing"})
        {
            var legacy = new Mod(id);
            Assert.Equal<JibbersMod?>(alpine, ModConflicts.Find(legacy,new[]{alpine}));
            Assert.Equal<JibbersMod?>(legacy, ModConflicts.Find(alpine,new[]{legacy}));
        }
        Assert.True(ModConflicts.Find(new Mod("fpv"),new[]{alpine}) is null);
        Assert.True(ModConflicts.Find(alpine,Array.Empty<JibbersMod>()) is null);
    }
}
