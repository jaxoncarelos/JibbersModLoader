using System.Reflection;
using System.Text.Json.Nodes;
using JoinSpawn;

static class ImprovedSpawnTests
{
    [Test]
    static void DisabledSpawnDoesNotOpenFromCommandsOrQuickMenu()
    {
        var spawn = new JoinSpawnMod();
        var reply = spawn.OnCommand("join_spawn.open", new JsonObject());
        Assert.Equal(false, reply!["ok"]!.GetValue<bool>());
        spawn.OpenFromMenu(); // Must remain inert without a loaded/enabled mod or native game.
        Assert.True(spawn.OnCommand("another_mod.open", new JsonObject()) is null);
    }

    [Test]
    static void DisableCleanupIsRepeatableAndKeepsTheSavedSettingsId()
    {
        var spawn = new JoinSpawnMod();
        Assert.Equal("join_spawn", spawn.Id);
        Assert.True(spawn.Conflicts.Contains("jibbers_join_spawn"));
        var deactivate = typeof(JoinSpawnMod).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!;
        deactivate.Invoke(spawn, null);
        deactivate.Invoke(spawn, null);
        Assert.Equal("Hank Mod Pack: Improved Spawn", spawn.Name);
    }
}
