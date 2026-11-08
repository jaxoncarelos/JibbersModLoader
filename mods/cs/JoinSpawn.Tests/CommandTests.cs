using System.Text.Json.Nodes;

static class CommandTests
{
    [Test]
    static void DisabledJoinSpawnCannotOpenFromAnotherMod()
    {
        var mod = new JoinSpawn.JoinSpawnMod();
        var reply = mod.OnCommand("join_spawn.open", new JsonObject());
        Assert.True(reply is not null);
        Assert.Equal(false, reply!["ok"]!.GetValue<bool>());
    }

    [Test]
    static void UnrelatedCommandsAreNotHandled()
    {
        var mod = new JoinSpawn.JoinSpawnMod();
        Assert.True(mod.OnCommand("another_mod.open", new JsonObject()) is null);
    }
}
