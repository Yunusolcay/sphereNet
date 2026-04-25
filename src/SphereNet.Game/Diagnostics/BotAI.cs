namespace SphereNet.Game.Diagnostics;

public enum BotAIState
{
    Idle,
    Wandering,
    Fighting,
    Fleeing,
    Dead,
    SeekingHealer,
    Recovering,
    SeekingMount,
    ExploringCity,
    Traveling,
}

public sealed class BotAI
{
    private readonly BotWorldModel _world;
    private readonly Random _rng;
    private BotAIState _state = BotAIState.Idle;
    private uint _currentTarget;
    private long _stateEnteredMs;
    private int _wanderSteps;
    private int _fleeSteps;
    private int _stuckCounter;
    private byte _lastDir;
    private int _reverseSteps;

    private const int FleeHpPercent = 25;
    private const int RecoverHpPercent = 70;
    private const int MaxFleeSteps = 15;
    private const int MaxWanderSteps = 20;
    private const int PoiArrivalRange = 3;
    private const int WaypointArrivalRange = 5;
    private const int PoiVisitsBeforeTravel = 4;
    private const int RunDistanceThreshold = 30;
    private long _lastPruneMs;
    private long _lastSpeechMs;

    public BotAIState State => _state;

    private static readonly string[] IdleChats =
    [
        "Hail!", "Hello!", "Good day!", "Greetings!",
        "Anyone need help?", "Nice weather", "Stay safe!",
        "Looking for a group", "Selling regs!", "Guards!"
    ];

    private static readonly string[] TravelChats =
    [
        "Off to town!", "Long road ahead", "Need supplies",
        "Heading out", "Safe travels!", "Watch the roads",
    ];

    #region City & POI Data

    private static readonly CityData[] Cities =
    [
        new("Britain", 1470, 1640, [
            (1428, 1696, "Bank"),
            (1494, 1630, "Market"),
            (1385, 1640, "West Gate"),
            (1414, 1610, "Blacksmith"),
            (1520, 1560, "Mage Shop"),
            (1490, 1740, "South Road"),
        ]),
        new("Trinsic", 1935, 2775, [
            (1823, 2822, "Bank"),
            (1883, 2870, "South Gate"),
            (1930, 2780, "Tailor"),
            (2057, 2820, "Docks"),
            (1970, 2760, "Provisioner"),
        ]),
        new("Moonglow", 4480, 1130, [
            (4460, 1172, "Bank"),
            (4538, 1044, "Mage Shop"),
            (4408, 1060, "Docks"),
            (4500, 1140, "Market"),
        ]),
        new("Yew", 560, 910, [
            (542, 985, "Inn"),
            (631, 860, "Provisioner"),
            (518, 874, "Mill"),
            (580, 920, "Center"),
        ]),
        new("Minoc", 2520, 475, [
            (2498, 406, "Bank"),
            (2573, 490, "Smithy"),
            (2467, 540, "Mines"),
            (2525, 420, "Provisioner"),
        ]),
        new("Vesper", 2925, 765, [
            (2899, 676, "Bank"),
            (2982, 818, "Tavern"),
            (2894, 740, "Bridge"),
            (2948, 852, "Docks"),
            (2960, 700, "Market"),
        ]),
        new("Skara Brae", 615, 2135, [
            (596, 2138, "Bank"),
            (610, 2090, "Mage"),
            (640, 2160, "Healer"),
            (575, 2200, "Docks"),
        ]),
        new("Jhelom", 1380, 3805, [
            (1324, 3773, "Bank"),
            (1374, 3826, "Arena"),
            (1418, 3850, "Docks"),
            (1290, 3800, "Provisioner"),
        ]),
    ];

    private static readonly (int From, int To, (short X, short Y)[] Waypoints)[] Routes =
    [
        // Britain (0) <-> Trinsic (1)
        (0, 1, [(1470, 1800), (1550, 2000), (1650, 2250), (1750, 2500), (1830, 2700)]),
        (1, 0, [(1830, 2700), (1750, 2500), (1650, 2250), (1550, 2000), (1470, 1800)]),

        // Britain (0) <-> Yew (3)
        (0, 3, [(1350, 1600), (1100, 1400), (850, 1200), (650, 1000), (560, 910)]),
        (3, 0, [(560, 910), (650, 1000), (850, 1200), (1100, 1400), (1350, 1600)]),

        // Britain (0) <-> Minoc (4)
        (0, 4, [(1500, 1500), (1700, 1200), (1900, 900), (2100, 700), (2300, 550), (2520, 475)]),
        (4, 0, [(2520, 475), (2300, 550), (2100, 700), (1900, 900), (1700, 1200), (1500, 1500)]),

        // Britain (0) <-> Vesper (5)
        (0, 5, [(1550, 1550), (1800, 1300), (2100, 1100), (2400, 900), (2700, 800), (2925, 765)]),
        (5, 0, [(2925, 765), (2700, 800), (2400, 900), (2100, 1100), (1800, 1300), (1550, 1550)]),

        // Britain (0) <-> Skara Brae (6)
        (0, 6, [(1400, 1700), (1200, 1800), (1000, 1900), (800, 2000), (615, 2135)]),
        (6, 0, [(615, 2135), (800, 2000), (1000, 1900), (1200, 1800), (1400, 1700)]),

        // Trinsic (1) <-> Jhelom (7)
        (1, 7, [(1900, 2900), (1800, 3100), (1700, 3300), (1500, 3600), (1380, 3805)]),
        (7, 1, [(1380, 3805), (1500, 3600), (1700, 3300), (1800, 3100), (1900, 2900)]),

        // Minoc (4) <-> Vesper (5)
        (4, 5, [(2550, 520), (2600, 580), (2700, 650), (2850, 720), (2925, 765)]),
        (5, 4, [(2925, 765), (2850, 720), (2700, 650), (2600, 580), (2550, 520)]),

        // Skara Brae (6) <-> Yew (3)
        (6, 3, [(615, 2100), (580, 1800), (560, 1500), (550, 1200), (560, 910)]),
        (3, 6, [(560, 910), (550, 1200), (560, 1500), (580, 1800), (615, 2100)]),
    ];

    #endregion

    public BotAI(BotWorldModel world, Random rng)
    {
        _world = world;
        _rng = rng;
        _stateEnteredMs = Environment.TickCount64;
        DetectCurrentCity();
    }

    public BotAction Tick()
    {
        long now = Environment.TickCount64;

        if (now - _lastPruneMs > 10_000)
        {
            _world.RemoveStale(now);
            _lastPruneMs = now;
        }

        if (HandleStuck(now) is { } stuckAction)
            return stuckAction;

        // Priority 1: Dead
        if (_world.IsDead)
        {
            if (_state != BotAIState.Dead && _state != BotAIState.SeekingHealer)
                ChangeState(BotAIState.Dead);
            return HandleDead();
        }

        // Just resurrected
        if (_state is BotAIState.Dead or BotAIState.SeekingHealer)
        {
            ChangeState(BotAIState.Recovering);
            _world.IsMounted = false;
            return BotAction.DisableWarMode();
        }

        // Priority 2: Recovering
        if (_state == BotAIState.Recovering)
        {
            if (_world.MaxHits > 0 && _world.Hits >= _world.MaxHits * RecoverHpPercent / 100)
                ChangeState(BotAIState.Idle);
            else
                return BotAction.Wait(_rng.Next(1500, 3000));
        }

        // Priority 3: Flee when HP low
        if (_world.MaxHits > 0 && _world.Hits > 0
            && _world.Hits < _world.MaxHits * FleeHpPercent / 100
            && _state != BotAIState.Fleeing)
        {
            ChangeState(BotAIState.Fleeing);
            _fleeSteps = 0;
        }

        if (_state == BotAIState.Fleeing)
            return HandleFlee();

        // Priority 4: Mount if available and not mounted
        if (!_world.IsMounted && _state != BotAIState.SeekingMount)
        {
            var mount = _world.FindNearest(m => m.IsMountBody && m.Notoriety <= 3, 12);
            if (mount != null)
            {
                ChangeState(BotAIState.SeekingMount);
                _currentTarget = mount.Serial;
            }
        }

        if (_state == BotAIState.SeekingMount)
            return HandleSeekMount();

        // Priority 5: Fight nearby enemies
        var enemy = _world.FindNearest(m => m.IsMonster, 14);
        if (enemy != null)
        {
            if (_state != BotAIState.Fighting || _currentTarget != enemy.Serial)
            {
                ChangeState(BotAIState.Fighting);
                _currentTarget = enemy.Serial;
            }
            return HandleFight(enemy);
        }

        // Priority 6: City exploration / travel
        if (_state == BotAIState.Traveling)
            return HandleTravel(now);

        if (_state == BotAIState.ExploringCity)
            return HandleExploreCity(now);

        // Decide: explore or travel?
        if (_world.TravelPhase == BotTravelPhase.TravelingToCity && _world.TargetCityIndex >= 0)
        {
            ChangeState(BotAIState.Traveling);
            return HandleTravel(now);
        }

        // Default: start exploring current city
        DetectCurrentCity();
        if (_world.CurrentCityIndex >= 0)
        {
            ChangeState(BotAIState.ExploringCity);
            PickNextPoi();
            return HandleExploreCity(now);
        }

        // Fallback: no city detected, wander toward nearest city
        return HandleWanderToNearestCity(now);
    }

    #region Stuck Detection & Recovery

    private BotAction? HandleStuck(long now)
    {
        if (_world.MoveRejectCount <= 0)
        {
            _stuckCounter = 0;
            _world.ConsecutiveStuck = 0;
            _reverseSteps = 0;
            return null;
        }

        _stuckCounter += _world.MoveRejectCount;
        _world.MoveRejectCount = 0;

        if (_reverseSteps > 0)
        {
            _reverseSteps--;
            byte reverseDir = (byte)((_lastDir + 4) % 8);
            if (_rng.Next(2) == 0)
                reverseDir = (byte)((reverseDir + (_rng.Next(2) == 0 ? 1 : 7)) % 8);
            return BotAction.Move(reverseDir);
        }

        if (_stuckCounter >= 20)
        {
            _stuckCounter = 0;
            _world.ConsecutiveStuck++;
            if (_world.ConsecutiveStuck >= 3)
            {
                _world.ConsecutiveStuck = 0;
                _world.HasDestination = false;
                if (_world.TravelPhase == BotTravelPhase.TravelingToCity && _world.WaypointIndex < GetCurrentRoute()?.Length - 1)
                    _world.WaypointIndex++;
                return BotAction.Wait(500);
            }
            PickRandomNearbyDestination();
            return BotAction.Wait(300);
        }

        if (_stuckCounter >= 10)
        {
            _reverseSteps = _rng.Next(5, 10);
            byte reverseDir = (byte)((_lastDir + 4) % 8);
            return BotAction.Move(reverseDir);
        }

        if (_stuckCounter >= 6)
        {
            byte dir = _world.HasDestination
                ? _world.GetDirectionTo(_world.DestX, _world.DestY)
                : _lastDir;
            int offset = _rng.Next(2) == 0 ? 2 : 6;
            dir = (byte)((dir + offset) % 8);
            _lastDir = dir;
            return BotAction.Move(dir);
        }

        if (_stuckCounter >= 3)
        {
            byte dir = _world.HasDestination
                ? _world.GetDirectionTo(_world.DestX, _world.DestY)
                : _lastDir;
            int offset = _rng.Next(2) == 0 ? 1 : 7;
            dir = (byte)((dir + offset) % 8);
            _lastDir = dir;
            return BotAction.Move(dir);
        }

        return null;
    }

    #endregion

    #region Combat & Survival (unchanged logic)

    private BotAction HandleDead()
    {
        var healer = _world.FindNearest(m => m.IsLikelyHealer, 30);
        if (healer != null)
        {
            ChangeState(BotAIState.SeekingHealer);
            int dist = _world.DistanceTo(healer.X, healer.Y);
            if (dist <= 2)
                return BotAction.Wait(_rng.Next(500, 1500));
            byte dir = _world.GetDirectionTo(healer.X, healer.Y);
            return BotAction.Move(dir);
        }
        byte wanderDir = (byte)_rng.Next(8);
        return BotAction.Move(wanderDir);
    }

    private BotAction HandleFlee()
    {
        _fleeSteps++;
        if (_fleeSteps >= MaxFleeSteps)
        {
            ChangeState(BotAIState.Recovering);
            return BotAction.DisableWarMode();
        }

        if (_world.Mobiles.TryGetValue(_currentTarget, out var enemy))
        {
            byte dir = _world.GetDirectionTo(enemy.X, enemy.Y);
            dir = (byte)((dir + 4) % 8);
            return BotAction.Move((byte)(dir | 0x80));
        }

        return BotAction.Move((byte)(_rng.Next(8) | 0x80));
    }

    private BotAction HandleSeekMount()
    {
        if (!_world.Mobiles.TryGetValue(_currentTarget, out var mount) || !mount.IsMountBody)
        {
            ChangeState(BotAIState.Idle);
            return BotAction.Wait(200);
        }

        int dist = _world.DistanceTo(mount.X, mount.Y);
        if (dist <= 1)
        {
            _world.IsMounted = true;
            ChangeState(BotAIState.Idle);
            return BotAction.DoubleClick(mount.Serial);
        }

        byte dir = _world.GetDirectionTo(mount.X, mount.Y);
        return BotAction.Move(dir);
    }

    private BotAction HandleFight(KnownMobile enemy)
    {
        int dist = _world.DistanceTo(enemy.X, enemy.Y);

        if (!_world.IsWarMode)
            return BotAction.EnableWarMode();

        if (dist <= 1)
            return BotAction.Attack(enemy.Serial);

        byte dir = _world.GetDirectionTo(enemy.X, enemy.Y);
        return BotAction.Move(dir);
    }

    #endregion

    #region City Exploration

    private BotAction HandleExploreCity(long now)
    {
        if (!_world.HasDestination)
            PickNextPoi();

        if (!_world.HasDestination)
        {
            ChangeState(BotAIState.Wandering);
            return HandleWander(now);
        }

        int dist = _world.DistanceTo(_world.DestX, _world.DestY);

        if (dist <= PoiArrivalRange)
        {
            _world.HasDestination = false;
            _world.LastDestReachedMs = now;
            _world.PoiVisitCount++;

            if (_world.PoiVisitCount >= PoiVisitsBeforeTravel + _rng.Next(3))
            {
                StartTravelToNewCity();
                if (_world.TravelPhase == BotTravelPhase.TravelingToCity)
                {
                    ChangeState(BotAIState.Traveling);
                    if (_rng.Next(3) == 0)
                        return BotAction.Say(TravelChats[_rng.Next(TravelChats.Length)]);
                    return HandleTravel(now);
                }
            }

            int waitMs = _rng.Next(3000, 8000);
            if (_rng.Next(5) == 0 && now - _lastSpeechMs > 30_000)
            {
                _lastSpeechMs = now;
                return BotAction.Say(IdleChats[_rng.Next(IdleChats.Length)]);
            }
            return BotAction.Wait(waitMs);
        }

        byte dir = _world.GetDirectionTo(_world.DestX, _world.DestY);
        _lastDir = dir;
        return BotAction.Move(dir);
    }

    private void PickNextPoi()
    {
        int cityIdx = _world.CurrentCityIndex;
        if (cityIdx < 0 || cityIdx >= Cities.Length)
        {
            _world.HasDestination = false;
            return;
        }

        var pois = Cities[cityIdx].Pois;
        if (pois.Length == 0)
        {
            _world.HasDestination = false;
            return;
        }

        var poi = pois[_rng.Next(pois.Length)];
        int jitterX = _rng.Next(-2, 3);
        int jitterY = _rng.Next(-2, 3);
        _world.DestX = (short)(poi.X + jitterX);
        _world.DestY = (short)(poi.Y + jitterY);
        _world.HasDestination = true;
    }

    private void PickRandomNearbyDestination()
    {
        int offsetX = _rng.Next(-20, 21);
        int offsetY = _rng.Next(-20, 21);
        _world.DestX = (short)(_world.X + offsetX);
        _world.DestY = (short)(_world.Y + offsetY);
        _world.HasDestination = true;
    }

    #endregion

    #region Inter-City Travel

    private BotAction HandleTravel(long now)
    {
        var route = GetCurrentRoute();

        if (route == null || route.Length == 0)
        {
            ArriveAtCity();
            return BotAction.Wait(500);
        }

        if (_world.WaypointIndex >= route.Length)
        {
            ArriveAtCity();
            return BotAction.Wait(500);
        }

        var wp = route[_world.WaypointIndex];
        int dist = _world.DistanceTo(wp.X, wp.Y);

        if (dist <= WaypointArrivalRange)
        {
            _world.WaypointIndex++;
            if (_world.WaypointIndex >= route.Length)
            {
                ArriveAtCity();
                return BotAction.Wait(500);
            }
            wp = route[_world.WaypointIndex];
        }

        byte dir = _world.GetDirectionTo(wp.X, wp.Y);
        _lastDir = dir;

        bool shouldRun = dist > RunDistanceThreshold || _world.TravelPhase == BotTravelPhase.TravelingToCity;
        if (shouldRun)
            dir = (byte)(dir | 0x80);

        if (_rng.Next(200) == 0 && now - _lastSpeechMs > 60_000)
        {
            _lastSpeechMs = now;
            return BotAction.Say(TravelChats[_rng.Next(TravelChats.Length)]);
        }

        return BotAction.Move(dir);
    }

    private void StartTravelToNewCity()
    {
        int currentCity = _world.CurrentCityIndex;
        if (currentCity < 0) currentCity = FindNearestCityIndex();

        int targetCity = PickNextCity(currentCity);
        if (targetCity < 0 || targetCity == currentCity)
        {
            _world.PoiVisitCount = 0;
            return;
        }

        var route = FindRoute(currentCity, targetCity);
        if (route == null)
        {
            int hubCity = 0; // Britain as hub
            if (currentCity == 0) hubCity = 1; // Trinsic if already in Britain
            route = FindRoute(currentCity, hubCity);
            targetCity = hubCity;
        }

        if (route == null)
        {
            _world.PoiVisitCount = 0;
            return;
        }

        _world.TravelPhase = BotTravelPhase.TravelingToCity;
        _world.TargetCityIndex = targetCity;
        _world.WaypointIndex = 0;
        _world.HasDestination = false;
        _world.PoiVisitCount = 0;
    }

    private void ArriveAtCity()
    {
        int targetCity = _world.TargetCityIndex;
        if (targetCity >= 0 && targetCity < Cities.Length)
            _world.CurrentCityIndex = targetCity;
        else
            DetectCurrentCity();

        _world.TravelPhase = BotTravelPhase.InCity;
        _world.TargetCityIndex = -1;
        _world.WaypointIndex = 0;
        _world.HasDestination = false;
        _world.PoiVisitCount = 0;
        ChangeState(BotAIState.ExploringCity);
    }

    private int PickNextCity(int currentCity)
    {
        var neighbors = new List<int>();
        for (int i = 0; i < Routes.Length; i++)
        {
            if (Routes[i].From == currentCity)
                neighbors.Add(Routes[i].To);
        }

        if (neighbors.Count == 0)
        {
            int idx = _rng.Next(Cities.Length);
            return idx == currentCity ? (idx + 1) % Cities.Length : idx;
        }

        return neighbors[_rng.Next(neighbors.Count)];
    }

    private (short X, short Y)[]? GetCurrentRoute()
    {
        int from = _world.CurrentCityIndex;
        int to = _world.TargetCityIndex;
        return FindRoute(from, to);
    }

    private static (short X, short Y)[]? FindRoute(int from, int to)
    {
        for (int i = 0; i < Routes.Length; i++)
        {
            if (Routes[i].From == from && Routes[i].To == to)
                return Routes[i].Waypoints;
        }
        return null;
    }

    #endregion

    #region City Detection

    private void DetectCurrentCity()
    {
        int bestCity = -1;
        int bestDist = int.MaxValue;

        for (int i = 0; i < Cities.Length; i++)
        {
            int dx = Math.Abs(_world.X - Cities[i].CenterX);
            int dy = Math.Abs(_world.Y - Cities[i].CenterY);
            int dist = Math.Max(dx, dy);

            if (dist < 200 && dist < bestDist)
            {
                bestDist = dist;
                bestCity = i;
            }
        }

        _world.CurrentCityIndex = bestCity;
    }

    private int FindNearestCityIndex()
    {
        int bestCity = 0;
        int bestDist = int.MaxValue;

        for (int i = 0; i < Cities.Length; i++)
        {
            int dx = Math.Abs(_world.X - Cities[i].CenterX);
            int dy = Math.Abs(_world.Y - Cities[i].CenterY);
            int dist = Math.Max(dx, dy);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestCity = i;
            }
        }

        return bestCity;
    }

    private BotAction HandleWanderToNearestCity(long now)
    {
        int nearestCity = FindNearestCityIndex();
        var city = Cities[nearestCity];

        byte dir = _world.GetDirectionTo(city.CenterX, city.CenterY);
        _lastDir = dir;

        int dist = _world.DistanceTo(city.CenterX, city.CenterY);
        if (dist <= 150)
        {
            _world.CurrentCityIndex = nearestCity;
            ChangeState(BotAIState.ExploringCity);
            PickNextPoi();
            return HandleExploreCity(now);
        }

        if (dist > RunDistanceThreshold)
            dir = (byte)(dir | 0x80);

        return BotAction.Move(dir);
    }

    #endregion

    #region Legacy Wander (fallback only)

    private BotAction HandleWander(long now)
    {
        _wanderSteps++;

        if (now - _lastSpeechMs > 60_000 && _rng.Next(30) == 0)
        {
            _lastSpeechMs = now;
            return BotAction.Say(IdleChats[_rng.Next(IdleChats.Length)]);
        }

        if (_wanderSteps >= MaxWanderSteps)
        {
            _wanderSteps = 0;
            DetectCurrentCity();
            if (_world.CurrentCityIndex >= 0)
            {
                ChangeState(BotAIState.ExploringCity);
                PickNextPoi();
                return HandleExploreCity(now);
            }
            return HandleWanderToNearestCity(now);
        }

        byte dir = (byte)_rng.Next(8);
        _lastDir = dir;
        return BotAction.Move(dir);
    }

    #endregion

    private void ChangeState(BotAIState newState)
    {
        _state = newState;
        _stateEnteredMs = Environment.TickCount64;
    }

    private sealed record CityData(string Name, short CenterX, short CenterY, (short X, short Y, string Name)[] Pois);
}

public readonly struct BotAction
{
    public BotAIActionType Type { get; init; }
    public byte Direction { get; init; }
    public uint TargetSerial { get; init; }
    public int DelayMs { get; init; }
    public string? Text { get; init; }

    public static BotAction Move(byte dir) => new() { Type = BotAIActionType.Move, Direction = dir };
    public static BotAction Attack(uint serial) => new() { Type = BotAIActionType.Attack, TargetSerial = serial };
    public static BotAction DoubleClick(uint serial) => new() { Type = BotAIActionType.DoubleClick, TargetSerial = serial };
    public static BotAction EnableWarMode() => new() { Type = BotAIActionType.EnableWarMode };
    public static BotAction DisableWarMode() => new() { Type = BotAIActionType.DisableWarMode };
    public static BotAction Wait(int ms) => new() { Type = BotAIActionType.Wait, DelayMs = ms };
    public static BotAction Say(string text) => new() { Type = BotAIActionType.Speech, Text = text };
    public static BotAction None() => new() { Type = BotAIActionType.None };
}

public enum BotAIActionType
{
    None,
    Move,
    Attack,
    DoubleClick,
    EnableWarMode,
    DisableWarMode,
    Wait,
    Speech,
}
