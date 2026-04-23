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

    private const int FleeHpPercent = 25;
    private const int RecoverHpPercent = 70;
    private const int MaxFleeSteps = 15;
    private const int MaxWanderSteps = 20;
    private long _lastPruneMs;
    private long _lastSpeechMs;

    public BotAIState State => _state;

    private static readonly string[] IdleChats =
    [
        "Hail!", "Hello!", "Good day!", "Greetings!",
        "Anyone need help?", "Nice weather", "Stay safe!",
        "Looking for a group", "Selling regs!", "Guards!"
    ];

    public BotAI(BotWorldModel world, Random rng)
    {
        _world = world;
        _rng = rng;
        _stateEnteredMs = Environment.TickCount64;
    }

    public BotAction Tick()
    {
        long now = Environment.TickCount64;

        if (now - _lastPruneMs > 10_000)
        {
            _world.RemoveStale(now);
            _lastPruneMs = now;
        }

        // Handle stuck detection
        if (_world.MoveRejectCount > 3)
        {
            _stuckCounter++;
            _world.MoveRejectCount = 0;
            if (_stuckCounter > 5)
            {
                _stuckCounter = 0;
                _lastDir = (byte)((_lastDir + 2) % 8);
                return BotAction.Move(_lastDir);
            }
        }
        else
        {
            _stuckCounter = 0;
        }

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

        // Priority 6: Wander
        if (_state != BotAIState.Wandering)
        {
            ChangeState(BotAIState.Wandering);
            _wanderSteps = 0;
        }
        return HandleWander(now);
    }

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

    private BotAction HandleWander(long now)
    {
        _wanderSteps++;

        // Occasional idle chat
        if (now - _lastSpeechMs > 60_000 && _rng.Next(30) == 0)
        {
            _lastSpeechMs = now;
            return BotAction.Say(IdleChats[_rng.Next(IdleChats.Length)]);
        }

        if (_wanderSteps >= MaxWanderSteps)
        {
            _wanderSteps = 0;
            if (_rng.Next(4) == 0)
                return BotAction.Wait(_rng.Next(2000, 5000));
        }

        byte dir = (byte)_rng.Next(8);
        _lastDir = dir;
        return BotAction.Move(dir);
    }

    private void ChangeState(BotAIState newState)
    {
        _state = newState;
        _stateEnteredMs = Environment.TickCount64;
    }
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
