using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

/// <summary>Readonly visibility delta produced by the (parallelizable) build
/// phase and consumed by the single-threaded apply phase.</summary>
public sealed class ClientViewDelta
{
    public HashSet<uint> CurrentChars { get; } = [];
    public HashSet<uint> CurrentItems { get; } = [];
    public List<(Character Character, bool HiddenAsAllShow)> NewChars { get; } = [];
    public List<Character> UpdatedChars { get; } = [];
    public List<(Item Item, bool HiddenAsAllShow)> NewItems { get; } = [];
    public List<Item> UpdatedItems { get; } = [];
}

/// <summary>
/// View-update handler extracted from the GameClient.ViewUpdate partial
/// (decomposition phase 3 — see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Owns the view-delta build/apply pipeline and the known-object
/// notifications; the per-client state lives in ClientViewCache
/// (GameClient.View). GameClient keeps thin delegating members, so every
/// call site is unchanged — the logic moved verbatim, with field accesses
/// routed through the GameClient context (Character/NetState/World/View and
/// the now-internal Send* packet helpers).
/// </summary>
public sealed class ClientViewUpdater
{
    private const int MaxItemsPerViewTile = 80;

    private readonly IClientContext _client;

    internal ClientViewUpdater(IClientContext client)
    {
        _client = client;
    }

    private ClientViewCache View => _client.View;
    private GameWorld WorldRef => _client.World;

    /// <summary>
    /// Source-X CClient::addObjMessage loop. Sends newly visible objects and
    /// removes objects that went out of range. Called each server tick.
    /// </summary>
    public void UpdateClientView()
    {
        var delta = BuildViewDelta();
        if (delta != null)
        {
            ApplyViewDelta(delta);
            SyncOpenMapStaticDoors();
        }
    }

    /// <summary>
    /// Build a readonly visibility delta. Safe for parallel build phase.
    /// Only runs for clients with ViewNeedsRefresh — idle clients skip entirely.
    /// </summary>
    public ClientViewDelta? BuildViewDelta()
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return null;
        if (me.IsReplaySpectator) return null;

        int range = _client.NetState.ViewRange;
        var center = me.Position;
        var delta = new ClientViewDelta();

        Dictionary<Point3D, int>? itemTileCounts = null;

        WorldRef.VisitInRange(center, range, ch =>
        {
            if (ch == me || ch.IsDeleted) return;
            if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline && !ch.IsClientLingering;
            if (isOfflinePlayer && !me.AllShow)
                return;

            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = me.AllShow ||
                (me.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 me.PrivLevel >= ch.PrivLevel);

            if (isHidden && !canSeeHidden)
                return;

            // Source-X: the manifest state IS STATF_INSUBSTANTIAL (war toggle
            // flips it; scripts may set/clear it too). War mode kept as a
            // fallback for ghosts saved before the flag existed.
            bool ghostManifested = ch.IsDead &&
                (!ch.IsStatFlag(Core.Enums.StatFlag.Insubstantial) || ch.IsInWarMode);
            if (ch.IsDead && !me.IsDead && !ghostManifested)
            {
                bool canSeeGhosts = me.AllShow ||
                    me.PrivLevel >= Core.Enums.PrivLevel.Counsel ||
                    me.IsStatFlag(Core.Enums.StatFlag.SpiritSpeak);
                if (!canSeeGhosts)
                    return;
            }

            // The mirror rule (sphere.ini DEADCANNOTSEELIVING): a ghost loses
            // sight of ordinary living NPCs, keeping its own pets and healers.
            // AllShow still overrides, as it does for every other view gate.
            if (me.IsDead && !me.AllShow && !me.CanSeeAsDead(ch))
                return;

            uint uid = ch.Uid.Value;
            delta.CurrentChars.Add(uid);

            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);
            if (!View.KnownChars.Contains(uid))
                delta.NewChars.Add((ch, hiddenAsAllShow));
            else
                delta.UpdatedChars.Add(ch);
        },
        item =>
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) return;
            bool isInvis = item.IsAttr(Core.Enums.ObjAttributes.Invis);
            // Invisible items (spawn worldgems, triggers, etc.) render for AllShow;
            // GM+ staff also see them dimmed without toggling AllShow, so a GM can
            // audit spawners on sight.
            bool canSeeInvisItems = me.AllShow || me.PrivLevel >= Core.Enums.PrivLevel.GM;
            if (isInvis && !canSeeInvisItems)
                return;

            itemTileCounts ??= [];
            var tile = new Point3D(item.X, item.Y, item.Z, item.MapIndex);
            int tileCount = itemTileCounts.GetValueOrDefault(tile);
            if (tileCount >= MaxItemsPerViewTile)
                return;
            itemTileCounts[tile] = tileCount + 1;

            uint uid = item.Uid.Value;
            delta.CurrentItems.Add(uid);
            if (!View.KnownItems.Contains(uid))
                delta.NewItems.Add((item, isInvis && canSeeInvisItems));
            else
                delta.UpdatedItems.Add(item);
        });

        return delta;
    }

    /// <summary>
    /// Apply previously built delta and perform packet I/O + known-set mutation.
    /// Must run on single-thread apply phase.
    /// </summary>
    public void ApplyViewDelta(ClientViewDelta delta)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;

        foreach (var (ch, hiddenAsAllShow) in delta.NewChars)
        {
            if (hiddenAsAllShow)
                _client.SendDrawObjectHidden(ch);
            else
                _client.SendDrawObject(ch);

            uint uid = ch.Uid.Value;
            View.KnownChars.Add(uid);
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch));
            _client.SendAosTooltip(ch, requested: false);
        }

        foreach (var ch in delta.UpdatedChars)
        {
            uint uid = ch.Uid.Value;
            bool posChanged = false;
            bool bodyChanged = false;
            bool visChanged = false;
            byte curVis = ComputeVisKey(ch);
            if (View.LastKnownPos.TryGetValue(uid, out var last))
            {
                posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                bodyChanged = last.Body != ch.BodyId || last.Hue != ch.Hue;
                visChanged = last.Vis != curVis;
            }
            else
            {
                posChanged = true;
            }

            bool manifestGhost = ch.IsDead && ch.IsInWarMode &&
                !me.AllShow &&
                me.PrivLevel < Core.Enums.PrivLevel.Counsel &&
                !me.IsDead;
            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline && !ch.IsClientLingering;
            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = me.AllShow ||
                (me.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 me.PrivLevel >= ch.PrivLevel);
            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);

            if (bodyChanged || visChanged)
            {
                if (hiddenAsAllShow)
                    _client.SendDrawObjectHidden(ch);
                else if (manifestGhost)
                    _client.SendDrawObjectWithHue(ch, 0x4001);
                else
                    _client.SendDrawObject(ch);
            }
            else if (posChanged)
            {
                if (hiddenAsAllShow)
                    _client.SendUpdateMobileHidden(ch);
                else if (manifestGhost)
                    _client.SendUpdateMobileWithHue(ch, 0x4001);
                else
                    _client.SendUpdateMobile(ch);
            }

            if (posChanged || bodyChanged || visChanged)
                View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, curVis);
        }

        foreach (var (item, hiddenAsAllShow) in delta.NewItems)
        {
            if (hiddenAsAllShow)
                _client.SendWorldItemAllShow(item);
            else
                _client.SendWorldItem(item);
            uint nuid = item.Uid.Value;
            View.KnownItems.Add(nuid);
            View.LastKnownItemState[nuid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            _client.SendAosTooltip(item, requested: false);
        }

        foreach (var item in delta.UpdatedItems)
        {
            uint uid = item.Uid.Value;
            if (View.LastKnownItemState.TryGetValue(uid, out var prev))
            {
                bool changed = prev.X != item.X || prev.Y != item.Y || prev.Z != item.Z ||
                               prev.DispId != item.DispIdFull || prev.Hue != item.Hue ||
                               prev.Amount != item.Amount || prev.Direction != item.Direction;
                if (changed)
                {
                    _client.SendWorldItem(item);
                    View.LastKnownItemState[uid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
                }
            }
            else
            {
                View.LastKnownItemState[uid] = (item.X, item.Y, item.Z, item.DispIdFull, item.Hue, item.Amount, item.Direction);
            }
        }

        var staleChars = new List<uint>();
        foreach (uint uid in View.KnownChars)
        {
            if (!delta.CurrentChars.Contains(uid))
            {
                _client.NetState.Send(new PacketDeleteObject(uid));
                staleChars.Add(uid);
            }
        }
        foreach (uint uid in staleChars)
        {
            View.KnownChars.Remove(uid);
            View.LastKnownPos.Remove(uid);
            // The tooltip cache survives view-exit deliberately (V2): the built
            // OPL lives on the object with a pure TTL (Source-X model).
        }

        var staleItems = new List<uint>();
        foreach (uint uid in View.KnownItems)
        {
            if (delta.CurrentItems.Contains(uid))
                continue;

            // An item that dropped out of the ground view because it was
            // equipped onto a mobile has already been re-homed client-side by
            // the 0x2E worn-item packet. A 0x1D here would delete the now-worn
            // item from the client — the classic "recolour/equip a worn item
            // and it vanishes until a resync (teleport)" bug. Forget it from the
            // ground-known set without deleting; the wearer's draw owns it now.
            var existing = WorldRef.FindItem(new Serial(uid));
            if (existing is { IsDeleted: false, IsEquipped: true })
            {
                staleItems.Add(uid);
                continue;
            }

            _client.NetState.Send(new PacketDeleteObject(uid));
            staleItems.Add(uid);
        }
        foreach (uint uid in staleItems)
        {
            View.KnownItems.Remove(uid);
            View.LastKnownItemState.Remove(uid);
            // Tooltip caches kept across view-exit — see the stale-chars loop.
        }
    }

    public void SyncOpenMapStaticDoors()
    {
        var me = _client.Character;
        if (me == null || WorldRef.MapData == null) return;

        int range = _client.NetState.ViewRange;
        byte mapId = me.MapIndex;
        short cx = me.X, cy = me.Y;

        var activeDoors = new HashSet<uint>();

        foreach (var (map, x, y, z) in WorldRef.OpenMapStaticDoors)
        {
            if (map != mapId) continue;
            if (Math.Abs(x - cx) > range || Math.Abs(y - cy) > range) continue;

            uint serial = (uint)(Serial.ItemFlag |
                (uint)((x & 0x7FFF) << 16) |
                (uint)((y & 0x3FFF) << 3) |
                (uint)(z & 0x07));
            activeDoors.Add(serial);

            if (View.KnownDoorOverrides.Add(serial))
            {
                ushort openTile = 0;
                ushort hue = 0;
                foreach (var s in WorldRef.MapData.GetStatics(mapId, x, y))
                {
                    if (s.Z == z && DoorHelper.IsDoorGraphic(WorldRef.MapData, s.TileId))
                    {
                        openTile = (ushort)(s.TileId + 1);
                        hue = s.Hue;
                        break;
                    }
                }
                if (openTile != 0)
                    _client.NetState.Send(new PacketWorldItem(serial, openTile, 1, x, y, z, hue));
            }
        }

        var staleDoors = new List<uint>();
        foreach (uint serial in View.KnownDoorOverrides)
        {
            if (!activeDoors.Contains(serial))
            {
                _client.NetState.Send(new PacketDeleteObject(serial));
                staleDoors.Add(serial);
            }
        }
        foreach (uint s in staleDoors)
            View.KnownDoorOverrides.Remove(s);
    }

    internal static byte ComputeVisKey(Character ch)
    {
        byte vis = 0;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Hidden)) vis |= 1;
        if (ch.IsInvisible) vis |= 2;
        if (ch.IsDead) vis |= 4;
        if (ch.IsInWarMode) vis |= 8;
        // Notoriety state drives the 0x77/0x78 noto byte (grey criminal / red
        // murderer highlight). Without these bits the view-delta never re-sent
        // an observer's 0x78 when only the criminal/murderer flag flipped, so a
        // stationary attacker stayed blue/green on screen until they moved.
        // MakeCriminal → SetStatFlag marks the char dirty, which flags nearby
        // clients for refresh; the changed vis key then triggers the redraw.
        if (ch.IsCriminal) vis |= 16;
        if (ch.IsMurderer) vis |= 32;
        return vis;
    }

    /// <summary>
    /// Update this client's View.LastKnownPos for a character that was just broadcast via 0x77.
    /// Prevents the view delta from sending a duplicate 0x77 for the same position.
    /// </summary>
    public void UpdateKnownCharPosition(Character ch)
    {
        uint uid = ch.Uid.Value;
        if (View.KnownChars.Contains(uid))
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch));
    }

    /// <summary>Returns true if this client already tracks the given mobile (has sent 0x78 spawn).</summary>
    public bool HasKnownChar(uint uid) => View.KnownChars.Contains(uid);

    /// <summary>Update the known-character cache to reflect a body/hue change
    /// broadcast out-of-band (death ghost transition / resurrect restore) so
    /// the next BuildViewDelta does not re-emit a duplicate 0x78. No-op when
    /// the UID is not currently known.</summary>
    public void UpdateKnownCharRender(uint uid, ushort newBody, ushort newHue, byte direction, short x, short y, sbyte z, byte visKey = 0)
    {
        if (View.KnownChars.Contains(uid))
            View.LastKnownPos[uid] = (x, y, z, direction, newBody, newHue, visKey);
    }

    /// <summary>Drop the character from the known set; optionally emit 0x1D.
    /// sendDelete=false is for the death dispatch where 0xAF already re-keyed
    /// the mobile client-side. Idempotent.</summary>
    public void RemoveKnownChar(uint uid, bool sendDelete = true)
    {
        if (View.KnownChars.Remove(uid))
        {
            View.LastKnownPos.Remove(uid);
            if (sendDelete)
                _client.NetState.Send(new PacketDeleteObject(uid));
        }
    }

    /// <summary>
    /// Called by BroadcastCharacterAppear to immediately show a character on this client.
    /// Each client renders from its own perspective (notoriety, AllShow, etc.).
    /// </summary>
    public void NotifyCharacterAppear(Character ch)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.Position.Map != me.Position.Map) return;
        if (!InRange(me.Position, ch.Position, _client.NetState.ViewRange)) return;

        // === Source-X ghost visibility (mirror of BuildViewDelta filter) ===
        // A dead/ghost character is invisible to LIVING observers unless
        // the observer is staff (Counsel+) or has AllShow toggled, OR the
        // ghost has manifested (war mode).
        bool isStaffViewer = me.AllShow ||
            me.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        bool ghostManifested = ch.IsDead && ch.IsInWarMode;

        if (ch.IsDead && !me.IsDead && !ghostManifested && !isStaffViewer)
            return;

        bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
        if (isHidden && !isStaffViewer)
            return;

        uint uid = ch.Uid.Value;
        // Manifested ghost renders translucent grey (hue 0x4001) for plain
        // observers; staff already see ghosts in their normal hue (HUE_DEFAULT).
        if (ghostManifested && !isStaffViewer && !me.IsDead)
            _client.SendDrawObjectWithHue(ch, 0x4001);
        else
            _client.SendDrawObject(ch);

        View.KnownChars.Add(uid);
        View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch));
    }

    /// <summary>
    /// Object-centric move notification for NPC movement. Handles enter-range (0x78),
    /// leave-range (0x1D), and position-update (0x77).
    /// </summary>
    public void NotifyCharMoved(Character ch, Point3D oldPos)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _client.NetState.ViewRange;
        bool wasInRange = InRange(me.Position, oldPos, range) && oldPos.Map == me.Position.Map;
        bool nowInRange = InRange(me.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
        {
            NotifyCharacterAppear(ch);
        }
        else if (wasInRange && !nowInRange)
        {
            RemoveKnownChar(uid, sendDelete: true);
        }
        else if (wasInRange && nowInRange && View.KnownChars.Contains(uid))
        {
            bool isHiddenNow = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = me.AllShow ||
                (me.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 me.PrivLevel >= ch.PrivLevel);
            if (isHiddenNow && !canSeeHidden)
            {
                RemoveKnownChar(uid, sendDelete: true);
                return;
            }

            if (View.LastKnownPos.TryGetValue(uid, out var last))
            {
                bool posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                if (!posChanged) return;
            }
            _client.SendUpdateMobile(ch);
            View.LastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue, ComputeVisKey(ch));
        }
        else if (nowInRange)
        {
            // In range on both ends but not yet tracked — e.g. the NPC
            // unhid after a scout-hide, or its initial appear was filtered
            // at spawn time. Without this branch the mobile would move
            // invisibly until the observer's next full view refresh.
            // NotifyCharacterAppear re-applies the hidden/ghost filters.
            NotifyCharacterAppear(ch);
        }
    }

    /// <summary>
    /// Player enter/leave range notification. Only handles enter-range (0x78) and
    /// leave-range (0x1D). Still-in-range 0x77 is handled by BroadcastMoveNearby.
    /// </summary>
    public void NotifyCharEnterLeave(Character ch, Point3D oldPos)
    {
        var me = _client.Character;
        if (me == null || !_client.IsPlaying) return;
        if (ch == me) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _client.NetState.ViewRange;
        bool wasInRange = InRange(me.Position, oldPos, range) && oldPos.Map == me.Position.Map;
        bool nowInRange = InRange(me.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
            NotifyCharacterAppear(ch);
        else if (wasInRange && !nowInRange)
            RemoveKnownChar(uid, sendDelete: true);
        else if (nowInRange && !View.KnownChars.Contains(uid))
            NotifyCharacterAppear(ch);
    }

    private static bool InRange(Point3D a, Point3D b, int range)
    {
        if (a.Map != b.Map) return false;
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx <= range && dy <= range;
    }
}
