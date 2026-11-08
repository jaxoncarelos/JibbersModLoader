using RenderDistance;

static class ChoicesTests
{
    const string Game = "100,250,500,750,1000,1250,1500";

    [Test]
    static void AddsFartherChoicesUpToTheMax()
    {
        var o = Choices.Build(Game, 10000)!;
        Assert.Equal("100", o[0]);
        Assert.Equal("1500", o[6]);
        Assert.Equal("2000", o[7]);
        Assert.Equal("10000", o[^1]);
        Assert.True(o.Select(float.Parse).Zip(o.Skip(1).Select(float.Parse)).All(p => p.First < p.Second), "ascending");
    }

    [Test]
    static void ReuncappingAnAlreadyUncappedListDoesNotDuplicate()
    {
        var once = Choices.Build(Game, 50000)!;
        var twice = Choices.Build(string.Join(",", once), 50000)!;
        Assert.Equal(string.Join(",", once), string.Join(",", twice));
        var lower = Choices.Build(string.Join(",", once), 5000)!;
        Assert.Equal("5000", lower[^1]);
    }

    [Test]
    static void DisabledMaxKeepsTheGameList()
    {
        Assert.Equal(Game, string.Join(",", Choices.Build(Game, Choices.CapMeters)!));
    }

    [Test]
    static void NonNumericListsAreLeftAlone()
    {
        Assert.True(Choices.Build("Low,Medium,High", 50000) is null);
        Assert.True(Choices.Build("", 50000) is null);
    }

    [Test]
    static void NearestKeepsTheCurrentDistanceSelected()
    {
        var v = Choices.Build(Game, 50000)!.Select(float.Parse).ToArray();
        Assert.Equal(6, Choices.Nearest(v, 1500));
        Assert.Equal(Array.IndexOf(v, 5000f), Choices.Nearest(v, 5000));
        Assert.Equal(Array.IndexOf(v, 50000f), Choices.Nearest(v, 999999));
    }

    [Test]
    static void StateRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rd-{Guid.NewGuid():N}.json");
        try
        {
            State.Save(path, new Dictionary<string, float> { ["cull-terrains-distance"] = 7500 });
            Assert.Equal(7500f, State.Load(path)["cull-terrains-distance"]);
            Assert.Equal(0, State.Load(path + ".missing").Count);
        }
        finally { File.Delete(path); }
    }
}
