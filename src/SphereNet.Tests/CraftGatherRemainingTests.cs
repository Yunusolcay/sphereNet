using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

// Crafting/gathering parity (wiki/11.txt remainder): weight-based CanCarry, so a
// gathered/crafted item bounces to the ground when it would overload the actor.
//
// This file used to pin a partial resource-node regen - a vein slowly refilling
// over time - as Source-X behaviour. It is not: the reference hands back the node
// it finds untouched (CWorldMap.cpp:71) and lets the one timeout set at creation
// (:148) delete it, after which the next search rolls a fresh node. The node
// lifecycle is now pinned by GatherParity05CTests.
public class CraftGatherRemainingTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CanCarry_GatesByWeight()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 10;
        Assert.Equal(75, ch.MaxWeight); // Str*7/2 + 40

        // Default item weight is 1 tenth/unit in-test, so Amount scales the weight.
        var light = world.CreateItem();
        light.Amount = 100; // 10 stones — fits the 75-stone cap
        Assert.True(ch.CanCarry(light));

        var heavy = world.CreateItem();
        heavy.Amount = 800; // 80 stones — over the cap
        Assert.False(ch.CanCarry(heavy));
    }
}
