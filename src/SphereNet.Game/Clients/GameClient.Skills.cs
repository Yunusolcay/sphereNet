using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>Skills handler (decomposition phase 3) — the members below
    /// delegate so every call site stays unchanged. The logic lives in
    /// <see cref="ClientSkillsHandler"/>.</summary>
    internal ClientSkillsHandler SkillUse => _skillUse ??= new ClientSkillsHandler(this);
    private ClientSkillsHandler? _skillUse;

    internal void BeginInfoSkill(SkillType skill, int skillId) => SkillUse.BeginInfoSkill(skill, skillId);

    internal void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind) =>
        SkillUse.BeginActiveSkill(skill, skillId, kind);

    internal void BeginTargetedSkill(SkillType skill, int skillId, Core.Types.Serial targetUid) =>
        SkillUse.BeginTargetedSkill(skill, skillId, targetUid);

    /// <summary>Advance delayed active skills (@SkillStroke loop + completion).</summary>
    public void TickPendingSkill() => SkillUse.TickPendingSkill();

    /// <summary>Source-X addObjMessage: overhead speech over any ObjBase.</summary>
    internal void ObjectMessage(Objects.ObjBase target, string text) => SkillUse.ObjectMessage(target, text);

    public void HandleHelpRequest() => SkillUse.HandleHelpRequest();

    public void HandleAOSTooltip(uint serial) => SkillUse.HandleAOSTooltip(serial);

    public void SendAosTooltip(Objects.ObjBase obj, bool requested, bool invalidate = false) =>
        SkillUse.SendAosTooltip(obj, requested, invalidate);

    public void HandleTradeRequest(uint targetUid) => SkillUse.HandleTradeRequest(targetUid);

    public void HandlePartyInvite(uint targetUid) => SkillUse.HandlePartyInvite(targetUid);

    public void HandlePartyLeave() => SkillUse.HandlePartyLeave();

    public void HandleClientVersion(string version) => SkillUse.HandleClientVersion(version);

    /// <summary>
    /// Glue between the skill engines and the client's network layer.
    /// Implements both <see cref="Skills.Information.IInfoSkillSink"/> and
    /// <see cref="Skills.Information.IActiveSkillSink"/> so the engines can
    /// emit overhead text, emote poses, sounds, and consume backpack items.
    /// </summary>
    internal sealed class InfoSkillSink : Skills.Information.IActiveSkillSink
    {
        private readonly IClientContext _client;
        public InfoSkillSink(IClientContext client, Character self) { _client = client; Self = self; }
        public Character Self { get; }
        public Random Random => System.Random.Shared;
        public Game.World.GameWorld World => _client.World;
        public Crafting.CraftingEngine? Crafting => _client.CraftE;

        public void SysMessage(string text) => _client.SysMessage(text);
        public void ObjectMessage(Objects.ObjBase target, string text) => _client.ObjectMessage(target, text);
        public void Emote(string text) => _client.NpcSpeech(Self, text);
        public void Sound(ushort soundId) =>
            _client.BroadcastNearby?.Invoke(Self.Position, 18,
                new PacketSound(soundId, (short)Self.Position.X, (short)Self.Position.Y, Self.Position.Z), 0);

        public void Animation(ushort animId) =>
            _client.BroadcastNearby?.Invoke(Self.Position, 18,
                new PacketAnimation(Self.Uid.Value, animId), 0);

        public Item? FindBackpackItem(Core.Enums.ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            return FindRecursive(pack, type, 32, []);
        }

        private static Item? FindRecursive(Item container, Core.Enums.ItemType type,
            int depth, HashSet<uint> seen)
        {
            if (depth < 0 || !seen.Add(container.Uid.Value)) return null;
            foreach (var item in container.Contents)
            {
                if (item.IsDeleted) continue;
                if (item.ItemType == type) return item;
                var nested = FindRecursive(item, type, depth - 1, seen);
                if (nested != null) return nested;
            }
            return null;
        }

        public void ConsumeAmount(Item item, ushort amount = 1)
        {
            if (item.Amount > amount)
            {
                item.Amount = (ushort)(item.Amount - amount);
                return;
            }
            _client.World.RemoveItem(item);
        }

        public void DeliverItem(Item item)
        {
            var pack = Self.Backpack;
            // Source-X CanCarry: a gathered/crafted item that would overload the actor
            // bounces to the ground instead of going into the pack. Staff bypass.
            if (pack != null && Self.PrivLevel < SphereNet.Core.Enums.PrivLevel.GM && !Self.CanCarry(item))
                pack = null;
            if (pack != null)
            {
                var actual = pack.AddItemWithStack(item);
                bool stacked = actual != item;                        // merged into a pile
                bool placed = stacked || item.ContainedIn == pack.Uid; // or newly added
                if (placed)
                {
                    if (stacked)
                        _client.World.RemoveItem(item);
                    _client.NetState.Send(new PacketContainerItem(
                        actual.Uid.Value, actual.DispIdFull, 0,
                        actual.Amount, actual.X, actual.Y,
                        pack.Uid.Value, actual.Hue,
                        _client.NetState.IsClientPost6017));
                    return;
                }
                // Pack is full (no room and nothing to stack onto): Source-X
                // ItemBounce drops the item at the actor's feet rather than
                // silently losing it (the old code "added" it to a 500-item-full
                // pack — a no-op — then told the client it was there: a desync).
            }

            _client.World.PlaceItemWithDecay(item, Self.Position);
        }

        public void OpenContainer(Item container) => _client.SendOpenContainer(container);

        public void ResurrectTarget(Objects.Characters.Character target)
        {
            if (!target.IsDead) return;
            _client.OnResurrectOther?.Invoke(target);
        }
    }

}
