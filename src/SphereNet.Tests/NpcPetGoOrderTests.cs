using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Regression: the "all go" pet order (GO_TARGET tag + Come mode) must override
// following. Running the follow step and the GO step in the same AI tick made
// the two moves cancel each other out once the pet was more than two tiles
// from its owner — the pet oscillated between the owner and the ordered spot
// and never arrived.
public class NpcPetGoOrderTests
{
    [Fact]
    public void GoOrder_PetWalksToOrderedSpot_AndStays()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var ai = new NpcAI(world, new SphereConfig());

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.IsOnline = true;
        owner.Hits = owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        world.AddOnlinePlayer(owner);

        var pet = world.CreateCharacter();
        pet.NpcMaster = owner.Uid;
        pet.Hits = pet.MaxHits = 50;
        pet.Stam = pet.MaxStam = 50;
        pet.Dex = 100;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        // "all go" wiring: ordered location stored on the pet, mode = Come.
        pet.PetAIMode = PetAIMode.Come;
        pet.SetTag("GO_TARGET", "120,100,0,0");

        var goal = new Point3D(120, 100, 0, 0);
        for (int i = 0; i < 60 && pet.Position.GetDistanceTo(goal) > 0; i++)
        {
            pet.NextNpcActionTime = 0;
            ai.OnTickAction(pet);
        }

        // Onto the tile itself. This used to accept being one short, which is how a GO
        // to an adjacent tile moved the pet nowhere at all: Source-X keeps stepping
        // while a direction remains (NPC_WalkToPoint, CCharNPCAct.cpp:437).
        Assert.Equal(goal, pet.Position);

        // The arrival tick clears the order and parks the pet at the spot
        // instead of resuming the follow (Source-X NPCACT_GOTO semantics).
        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
        Assert.False(pet.TryGetTag("GO_TARGET", out _));
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }
}
