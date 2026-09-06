using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.State;

namespace SphereNet.Tests;

internal static class TestHarness
{
    public static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(_ => { });

    public static GameWorld CreateWorld()
    {
        var world = new GameWorld(CreateLoggerFactory());
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>Seed every skill with the classic sphere_skills.scp ADV_RATE
    /// curve (2.5,50.0,200.0). Skill gain strictly follows the curve — no
    /// curve means no gain (Source-X GetChancePercent) — so any test that
    /// expects gains must seed defs first.</summary>
    public static void SeedSkillAdvRates()
    {
        for (int i = 0; i < (int)SphereNet.Core.Enums.SkillType.Qty; i++)
        {
            var def = new SphereNet.Scripting.Definitions.SkillDef(ResourceId.Invalid);
            def.LoadFromKey("ADV_RATE", "2.5,50.0,200.0");
            SphereNet.Game.Definitions.DefinitionLoader.SetSkillDef(i, def);
        }
    }

    public static NetState CreateActiveNetState(ILoggerFactory loggerFactory, int id)
    {
        var state = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = id };
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        return state;
    }

    public static GameClient CreateClient(ILoggerFactory loggerFactory, GameWorld world, AccountManager accounts, int id)
    {
        var state = CreateActiveNetState(loggerFactory, id);
        return new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
    }

    public static Queue<PacketBuffer> GetQueuedPackets(NetState state)
    {
        // Combined outbound snapshot in flush order: priority queues drain
        // Highest → Idle. _queues is indexed by (int)PacketPriority.
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;

        var combined = new Queue<PacketBuffer>();
        for (int p = queues.Length - 1; p >= 0; p--)
            foreach (var pkt in queues[p])
                combined.Enqueue(pkt);
        return combined;
    }

    public static void AttachCharacter(GameClient client, Character ch, Account? account = null)
    {
        typeof(GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        if (account != null)
        {
            typeof(GameClient)
                .GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, account);
        }
    }

    /// <summary>Run the world's TIMERF sweep (private: it is driven by the main loop).</summary>
    public static void PumpTimerF(GameWorld world, long nowMs) =>
        typeof(GameWorld)
            .GetMethod("TickTimerF", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(world, [nowMs]);

    public static void SetPrivateField<T>(GameClient client, string fieldName, T value)
    {
        typeof(GameClient)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, value);
    }
}
