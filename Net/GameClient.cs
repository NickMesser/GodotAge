#nullable enable
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using AAEmu.GodotViewer.Ui;
using System.Text.Json;

namespace AAEmu.GodotViewer.Net;

public sealed class GameClientOptions
{
    public string LoginHost { get; init; } = "127.0.0.1";
    public int LoginPort { get; init; } = 1237;

    /// <summary>Launcher account name (-StrUserName). AAEmu's web auth trusts it and creates the account on first use.</summary>
    public required string UserName { get; init; }

    /// <summary>Launcher token (-strUserToken). Sent inside the passport JSON; never logged.</summary>
    public required string UserToken { get; init; }

    /// <summary>-gameId from the launcher.</summary>
    public int GameId { get; init; } = 1;

    /// <summary>Protocol range sent in CARequestWebAuth/X2EnterWorld. The real client's values are unknown; AAEmu ignores them.</summary>
    public uint ProtocolFrom { get; init; }
    public uint ProtocolTo { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Movement packet cadence while moving (the real client sends every 93-125 ms).</summary>
    public TimeSpan MoveInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Stationary position heartbeat (the real client sends one about every 3 s while idle). Zero disables.</summary>
    public TimeSpan IdleMoveInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Delay between the server's in-game burst and CSNotifyInGameCompleted (the real client's loading screen took ~5 s).</summary>
    public TimeSpan InGameSettleTime { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Queue S->C packets this client does not decode as <see cref="RawPacketEvent"/>.</summary>
    public bool EmitUnhandledPackets { get; init; }

    /// <summary>Ordered decoders for server-to-client game opcodes not handled by the core client.</summary>
    public IReadOnlyList<IPacketParserFamily> PacketParserFamilies { get; init; } = AAEmu.GodotViewer.Net.PacketParserFamilies.CreateDefault();

    /// <summary>Oldest events are dropped beyond this many undelivered events.</summary>
    public int MaxQueuedEvents { get; init; } = 200_000;

    /// <summary>Optional diagnostic log (called on network threads). Never receives credentials.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Headless ArcheAge 10.0.2.13 client for AAEmu: login (:1237), world handshake and key exchange (:1239),
/// character select, enter world, unit/movement events, walking and logout.
/// </summary>
/// <remarks>
/// <para>Threading: two background receive loops (login, world) plus a keepalive loop decode packets
/// and push <see cref="GameEvent"/>s into a concurrent queue. Consumers (e.g. Godot's _Process) call
/// <see cref="TryDequeue"/> from any thread. <see cref="EventRaised"/> fires on the network thread.
/// All send methods are thread-safe.</para>
/// <para>Flow: <see cref="LoginAsync"/> → <see cref="ConnectWorldAsync"/> → <see cref="EnterWorldAsync"/>
/// → <see cref="SendMovement"/> / <see cref="WalkToAsync"/> → <see cref="LogoutAsync"/>.</para>
/// </remarks>
public sealed class GameClient : IAsyncDisposable, IDisposable
{
    private readonly GameClientOptions _options;
    private readonly ConcurrentQueue<GameEvent> _events = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _life = new();
    private readonly object _waitLock = new();
    private readonly List<Waiter> _waiters = [];
    private readonly object _worldSendLock = new();
    private readonly SemaphoreSlim _moveGate = new(1, 1);

    private FrameSocket? _login;
    private FrameSocket? _world;
    private ClientCipher? _cipher;
    private bool _encrypted;
    private byte _csCount;
    private int _changeState = -1;
    private short _enterWorldReason = -1;
    private byte _worldId;
    private bool _quitting;
    private bool _awaitingLobby;
    private ulong _selectedCharacterId;
    private Task? _keepAlive;
    private long _lastMoveSentMs;
    private UnitMovement? _lastMovement;
    private readonly long _pingTm;
    private readonly List<LobbyCharacter> _pendingCharacters = [];
    private TaskCompletionSource<List<LobbyCharacter>>? _characterListTcs;
    private TaskCompletionSource<int>? _statesDoneTcs;
    private TaskCompletionSource<bool>? _leaveGrantedTcs;
    private readonly ConcurrentDictionary<ushort, int> _unhandled = new();

    public GameClient(GameClientOptions options)
    {
        _options = options;
        _pingTm = Environment.TickCount64;
    }

    // ------------------------------------------------------------------ public state

    public ClientStage Stage { get; private set; } = ClientStage.Disconnected;
    public ulong AccountId { get; private set; }
    public IReadOnlyList<WorldServerInfo> Worlds { get; private set; } = [];
    public IReadOnlyList<LobbyCharacter> Characters { get; private set; } = [];

    /// <summary>Object id of the own character once spawned (0 before).</summary>
    public uint OwnUnitId { get; private set; }

    /// <summary>Last position this client reported for its own character (Cry world metres).</summary>
    public Vector3 OwnPosition { get; private set; }

    /// <summary>Last yaw this client reported (radians about +Z, 0 = north).</summary>
    public float OwnYaw { get; private set; }
    public uint OwnZoneId { get; private set; }

    /// <summary>The own character's physics clock (ms) stamped on movement packets.</summary>
    public uint PhysicsTime => (uint)(_clock.ElapsedMilliseconds + 1000);

    /// <summary>Counts of S->C opcodes received but not decoded, for diagnostics.</summary>
    public IReadOnlyDictionary<ushort, int> UnhandledOpcodes => _unhandled;

    /// <summary>Raised on the network thread for every event, after it is queued.</summary>
    public event Action<GameEvent>? EventRaised;

    public bool TryDequeue(out GameEvent? ev) => _events.TryDequeue(out ev);

    public int PendingEvents => _events.Count;

    // ------------------------------------------------------------------ login server

    /// <summary>
    /// Connects to the login server, authenticates with the launcher passport (CARequestWebAuth) and
    /// requests the world list (CAListWorld). Returns the worlds.
    /// </summary>
    public async Task<IReadOnlyList<WorldServerInfo>> LoginAsync(CancellationToken ct = default)
    {
        SetStage(ClientStage.ConnectingLogin, $"connecting to login {_options.LoginHost}:{_options.LoginPort}");
        _login = await FrameSocket.ConnectAsync(_options.LoginHost, _options.LoginPort, ct).ConfigureAwait(false);
        _ = Task.Factory.StartNew(() => LoginLoopAsync(_login), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        SetStage(ClientStage.Authenticating, $"authenticating as {_options.UserName} (web auth)");
        var auth = Wait(Channel.Login, [Opcodes.ACAuthResponse, Opcodes.ACLoginDenied], ct);
        var passport = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["source"] = "launcher",
            ["StrUserName"] = _options.UserName,
            ["strUserToken"] = _options.UserToken,
            ["gameId"] = _options.GameId.ToString(),
        });
        SendLogin(Opcodes.CARequestWebAuth, new WireWriter()
            .U32(_options.ProtocolFrom).U32(_options.ProtocolTo)
            .U8(0)        // svc
            .Bool(true)   // dev (the launcher starts the dev client)
            .Str(passport)
            .Bytes(new byte[8]).Bytes(new byte[8]) // mac, mac2
            .U64(0)       // cpu
            .Bool(true)   // is64Bit
            .Bool(false)  // isMultiClient
            .U8(0));      // clientSerial
        var (op, body) = await auth.ConfigureAwait(false);
        if (op == Opcodes.ACLoginDenied)
            throw new InvalidOperationException($"login denied, reason {body[0]}");
        var r = new WireReader(body);
        AccountId = r.U64();
        SetStage(ClientStage.Authenticated, $"authenticated, account {AccountId}");

        var list = Wait(Channel.Login, [Opcodes.ACWorldList], ct);
        SendLogin(Opcodes.CAListWorld, new WireWriter().U64(0));
        var (_, worldBody) = await list.ConfigureAwait(false);
        var ev = ParseWorldList(worldBody);
        Worlds = ev.Worlds;
        Emit(ev);
        return ev.Worlds;
    }

    private static WorldListEvent ParseWorldList(byte[] body)
    {
        var r = new WireReader(body);
        r.Bool(); // privacy policy state
        var worlds = new List<WorldServerInfo>();
        var count = r.U8();
        for (var i = 0; i < count; i++)
        {
            var id = r.U8();
            r.U8(); r.U16(); r.U8(); // parent, type, color
            var name = r.Str();
            r.U8(); // entry
            var available = r.U8() != 0;
            byte con = 0;
            if (available)
            {
                con = r.U8();
                r.Bytes(10); // per-race congestion
            }
            worlds.Add(new WorldServerInfo(id, name, available, con));
        }
        var chars = new List<LoginCharacterSummary>();
        var n = r.U8();
        for (var i = 0; i < n; i++)
        {
            var acc = r.U64();
            var gs = r.U8();
            var cid = r.U32();
            var name = r.Str();
            byte race = r.U8(), gender = r.U8();
            r.Blob();
            r.U64();
            chars.Add(new LoginCharacterSummary(acc, gs, cid, name, race, gender));
        }
        return new WorldListEvent(worlds, chars);
    }

    // ------------------------------------------------------------------ world server

    /// <summary>
    /// Requests entry to a world (CAEnterWorld → ACWorldCookie), connects to the game server, performs
    /// X2EnterWorld, the ChangeState/FinishState handshake and the RSA key exchange (CSAesXorKey), and
    /// returns the character list (SCCharacterList).
    /// </summary>
    public async Task<IReadOnlyList<LobbyCharacter>> ConnectWorldAsync(byte worldId, CancellationToken ct = default)
    {
        if (_login is null)
            throw new InvalidOperationException("LoginAsync first");
        _worldId = worldId;
        SetStage(ClientStage.RequestingWorld, $"requesting world {worldId}");
        var cookieWait = Wait(Channel.Login, [Opcodes.ACWorldCookie, Opcodes.ACEnterWorldDenied], ct);
        SendLogin(Opcodes.CAEnterWorld, new WireWriter().U64(0).U8(worldId));
        var (op, body) = await cookieWait.ConfigureAwait(false);
        if (op == Opcodes.ACEnterWorldDenied)
            throw new InvalidOperationException($"enter world denied, reason {body[0]}");
        var r = new WireReader(body);
        var cookie = r.U32();
        var ipBytes = r.Bytes(4);
        Array.Reverse(ipBytes);
        var ip = new IPAddress(ipBytes);
        var port = r.U16();

        SetStage(ClientStage.ConnectingWorld, $"connecting to world {ip}:{port}");
        _world = await FrameSocket.ConnectAsync(ip.ToString(), port, ct).ConfigureAwait(false);
        _characterListTcs = new TaskCompletionSource<List<LobbyCharacter>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Factory.StartNew(() => WorldLoopAsync(_world), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var response = Wait(Channel.Game, [Opcodes.X2EnterWorldResponse], ct);
        SendGame(Opcodes.X2EnterWorld, new WireWriter()
            .U32(_options.ProtocolFrom).U32(_options.ProtocolTo)
            .U64(AccountId)
            .U32(cookie)
            .S32(-1)   // zoneId (X2World:EnterWorld(worldIndex, -1) in the recovered client scripts)
            .U8(0)     // tb
            .U64(0));  // revision
        // The receive thread decodes the response and builds the cipher itself (OnEnterWorldResponse), so the
        // key reply can go out on ChangeState(2) without racing this continuation.
        await response.ConfigureAwait(false);
        if (_enterWorldReason != 0 || _cipher is null)
            throw new InvalidOperationException($"X2EnterWorldResponse reason {_enterWorldReason}");
        _keepAlive ??= Task.Run(KeepAliveLoopAsync);

        // The login connection has done its job (the real client leaves the auth server here too).
        _login?.Dispose();
        _login = null;

        var list = await _characterListTcs.Task.WaitAsync(_options.RequestTimeout, ct).ConfigureAwait(false);
        Characters = list;
        SetStage(ClientStage.CharacterSelect, $"{list.Count} character(s) on this world");
        return list;
    }

    /// <summary>
    /// Selects a character and enters the world: CSRestrictCheck, CSCheckRaceCongestion, CSSelectCharacter,
    /// FinishState 2..7 with CSSpawnCharacter at state 6, CSNotifyInGame, CSNotifyInGameCompleted and a first
    /// stationary CSMoveUnit. Returns once the server has spawned the character in its zone.
    /// </summary>
    public async Task<EnteredWorldEvent> EnterWorldAsync(ulong characterId, CancellationToken ct = default)
    {
        if (_world is null || !_encrypted)
            throw new InvalidOperationException("ConnectWorldAsync first");
        _selectedCharacterId = characterId;
        _awaitingLobby = false;
        SetStage(ClientStage.EnteringWorld, $"selecting character {characterId}");

        var restrict = Wait(Channel.Game, [Opcodes.SCResultRestrictCheck], ct);
        SendGame(Opcodes.CSRestrictCheck, new WireWriter().U64(characterId).U8(0));
        await restrict.ConfigureAwait(false);

        var congestion = Wait(Channel.Game, [Opcodes.SCCheckRaceCongestionResponse], ct);
        SendGame(Opcodes.CSCheckRaceCongestion, new WireWriter().U64(characterId));
        var (_, cbody) = await congestion.ConfigureAwait(false);
        if (cbody.Length < 11 || cbody[10] == 0)
            throw new InvalidOperationException("server refused the character (race congestion check)");

        var stateWait = Wait(Channel.Game, [Opcodes.SCCharacterState], ct);
        var selfWait = Wait(Channel.Game, [Opcodes.SCUnitState], ct, body => IsOwnUnitState(body, characterId));
        _statesDoneTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _leaveGrantedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SendGame(Opcodes.CSSelectCharacter, new WireWriter().U64(characterId).Bool(false));
        var (_, sbody) = await stateWait.ConfigureAwait(false);
        var (iid, info, _) = PacketParsers.ParseCharacterState(sbody);
        OwnPosition = info.Position;
        OwnZoneId = info.ZoneId;
        Progress($"character state: {info.Name} lv{info.Level} zone {info.ZoneId} at {Fmt(info.Position)}");

        // Lobby state (2) is finished; the server now drives ChangeState 3..7 (see OnChangeState).
        SendProxy(Opcodes.FinishState, new WireWriter().S32(2));
        var (_, selfBody) = await selfWait.ConfigureAwait(false);
        var self = PacketParsers.ParseUnitState(selfBody);
        OwnUnitId = self.UnitId;
        if (self.HasYaw)
            OwnYaw = self.Yaw;
        OwnPosition = self.Position;
        await _statesDoneTcs.Task.WaitAsync(_options.RequestTimeout, ct).ConfigureAwait(false);

        // Load finished on our side: ask the server to put us in the zone. World answers NotifyInGame with
        // SCSystemFeatureStateList (its first packet once the Zone accepted the character), or returns the
        // character to the lobby (SCLeaveWorldGranted) when no Zone hosts its zone.
        var inGame = Wait(Channel.Game, [Opcodes.SCSystemFeatureStateList, Opcodes.SCWorldLevelInfo], ct);
        SendGame(Opcodes.CSNotifyInGame, new WireWriter());
        var first = await Task.WhenAny(inGame, _leaveGrantedTcs.Task).ConfigureAwait(false);
        if (first == _leaveGrantedTcs.Task)
            throw new InvalidOperationException("the server sent the character back to character select (zone not available?)");
        await inGame.ConfigureAwait(false);

        // The real client sits on its loading screen while the in-game burst arrives, then reports completion.
        await Task.Delay(_options.InGameSettleTime, ct).ConfigureAwait(false);
        SendGame(Opcodes.CSNotifyInGameCompleted, new WireWriter());
        Stage = ClientStage.InWorld;
        SendMovement(OwnPosition, OwnYaw, Vector3.Zero, MoveFlags.None, 0);
        var entered = new EnteredWorldEvent(OwnUnitId, characterId, info.Name, OwnPosition, OwnYaw, info.ZoneId, iid,
            _worldId, self, info.Experience);
        Emit(new ProgressEvent(ClientStage.InWorld, $"in world as unit {OwnUnitId}"));
        Emit(entered);
        return entered;
    }

    /// <summary>Creates a lobby character and returns the complete lobby record sent by the server.</summary>
    public async Task<LobbyCharacter> CreateCharacterAsync(CharacterCreateRequest request,
        CancellationToken ct = default)
    {
        var wait = Wait(Channel.Game,
            [Opcodes.SCCreateCharacterResponse, Opcodes.SCCharacterCreationFailed], ct);
        SendGame(CharacterLobbyPacketWriters.CreateCharacter(request, introZoneId: request.IntroZoneId));
        var (op, body) = await wait.ConfigureAwait(false);
        if (op == Opcodes.SCCharacterCreationFailed)
            throw new InvalidOperationException($"character creation rejected (reason {(body.Length > 0 ? body[0] : 0)})");
        return PacketParsers.ReadLobbyCharacter(new WireReader(body));
    }

    /// <summary>Requests deletion and returns the server's status and schedule timestamps.</summary>
    public async Task<CharacterDeleteResponse> DeleteCharacterAsync(ulong characterId,
        CancellationToken ct = default)
    {
        var wait = Wait(Channel.Game, [Opcodes.SCDeleteCharacterResponse], ct,
            body => body.Length >= 8 && new WireReader(body).U64() == characterId);
        SendGame(CharacterLobbyPacketWriters.DeleteCharacter(characterId));
        return CharacterLobbyPacketWriters.ReadDeleteResponse((await wait.ConfigureAwait(false)).Body);
    }

    /// <summary>Cancels scheduled character deletion and returns the resulting status.</summary>
    public async Task<CharacterDeleteCancelResponse> CancelCharacterDeleteAsync(ulong characterId,
        CancellationToken ct = default)
    {
        var wait = Wait(Channel.Game, [Opcodes.SCCancelCharacterDeleteResponse], ct,
            body => body.Length >= 8 && new WireReader(body).U64() == characterId);
        SendGame(CharacterLobbyPacketWriters.CancelCharacterDelete(characterId));
        return CharacterLobbyPacketWriters.ReadCancelDeleteResponse((await wait.ConfigureAwait(false)).Body);
    }

    private static bool IsOwnUnitState(byte[] body, ulong characterId)
    {
        try
        {
            var r = new WireReader(body);
            r.Bc();
            r.Str();
            r.S8(); r.S8(); r.Bool();
            return r.U8() == (byte)UnitKind.Character && r.U64() == characterId;
        }
        catch (WireException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ movement

    /// <summary>
    /// Sends one CSMoveUnit (type Unit) for the own character, laid out exactly like the real client's:
    /// bc id, 1, time, flags, position, velocity (short, 32767 = 60 m/s), rotation (0, 0, heading), delta
    /// input, stance, alertness, actor flags, trailing 0. Position/yaw/velocity use Cry world axes (see
    /// <see cref="WorldCoords"/>).
    /// </summary>
    /// <param name="flags">MoveFlags.Moving while walking, None when standing.</param>
    /// <param name="forwardInput">Input delta on the forward axis: 127 while walking forward, 0 when stopped.</param>
    /// <param name="stance">1 = normal/idle, 2 = swimming (as the captures show).</param>
    /// <param name="actorFlags">4 = on ground (walking captures), 0 = in water/air.</param>
    public void SendMovement(Vector3 position, float yaw, Vector3 velocity, MoveFlags flags, sbyte forwardInput,
        sbyte stance = 1, byte alertness = 0, ushort actorFlags = 4, sbyte verticalInput = 0, uint unitId = 0,
        sbyte strafeInput = 0)
    {
        if (OwnUnitId == 0)
            throw new InvalidOperationException("not in world");
        var move = new UnitMovement
        {
            UnitId = unitId == 0 ? OwnUnitId : unitId, Kind = MoveKind.Unit, Time = PhysicsTime, Flags = (byte)flags,
            Position = position, Velocity = velocity, Yaw = yaw,
            DeltaX = strafeInput, DeltaY = forwardInput, DeltaZ = verticalInput,
            Stance = stance, Alertness = alertness, ActorFlags = actorFlags,
        };
        SendGame(Opcodes.CSMoveUnit, new WireWriter().Bytes(move.ToCsMoveUnitBody()));
        _lastMovement = move;
        if (move.UnitId == OwnUnitId)
        {
            OwnPosition = position;
            OwnYaw = yaw;
        }
        Interlocked.Exchange(ref _lastMoveSentMs, _clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Sends a movement body the caller built (vehicle type 2, ship request type 5) and counts it as movement, so the idle
    /// type 1 heartbeat stays quiet while the player drives: the real client sends only the vehicle stream when seated.
    /// </summary>
    public void SendVehicleMovement(CombatOutboundPacket packet)
    {
        SendGame(packet);
        Interlocked.Exchange(ref _lastMoveSentMs, _clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// CSTeleportEnded (0x0F5) after an SCTeleportUnit has been applied: fixed-point X/Y s64, Z f32 and the character's
    /// orientation quaternion (0, 0, sin(yaw/2), cos(yaw/2)). Real-client capture after a GM /move:
    /// <c>0000000000 38d803 000000000070c203 2433cd42 00000000 00000000 534858bf 9af5083f</c> (yaw -115.3°).
    /// World (AAEmu CSTeleportEndedPacket) only clears the post-teleport movement lock on this packet; until then it
    /// drops every CSMoveUnit silently, the vehicle stream included.
    /// </summary>
    public void SendTeleportEnded(Vector3 position, float yaw)
    {
        SendGame(0x0F5, new WireWriter()
            .S64(((long)(position.X * 4096f)) << 32).S64(((long)(position.Y * 4096f)) << 32).F32(position.Z)
            .F32(0).F32(0).F32(MathF.Sin(yaw / 2)).F32(MathF.Cos(yaw / 2)));
        OwnPosition = position;
        OwnYaw = yaw;
    }

    /// <summary>Returns idle heartbeats to the player after server-confirmed detachment from a controlled unit.</summary>
    public void ResetMovementHeartbeat(Vector3 position, float yaw)
    {
        OwnPosition = position;
        OwnYaw = yaw;
        _lastMovement = new UnitMovement
        {
            UnitId = OwnUnitId, Kind = MoveKind.Unit, Position = position, Yaw = yaw,
            Stance = 1, ActorFlags = 4,
        };
    }

    /// <summary>
    /// Walks the own character in a straight line to <paramref name="target"/> the way the real client
    /// reports it: a start packet, then one packet per <see cref="GameClientOptions.MoveInterval"/> with the
    /// advancing position, then a stop packet at the target. Z is interpolated linearly (this client has no
    /// terrain), so pass a target on walkable ground. Returns when the stop packet is sent.
    /// </summary>
    public async Task WalkToAsync(Vector3 target, float speed = 5.4f, CancellationToken ct = default)
    {
        await _moveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var start = OwnPosition;
            var delta = target - start;
            var flat = new Vector2(delta.X, delta.Y).Length();
            if (flat < 0.01f)
                return;
            var yaw = WorldCoords.YawFromDirection(delta.X, delta.Y);
            var duration = flat / speed;
            var velocity = delta / duration;
            SendMovement(start, yaw, Vector3.Zero, MoveFlags.Moving, 127);
            var t0 = _clock.Elapsed.TotalSeconds;
            while (true)
            {
                await Task.Delay(_options.MoveInterval, ct).ConfigureAwait(false);
                var t = (float)(_clock.Elapsed.TotalSeconds - t0);
                if (t >= duration)
                    break;
                SendMovement(start + velocity * t, yaw, velocity, MoveFlags.Moving, 127);
            }
            SendMovement(target, yaw, Vector3.Zero, MoveFlags.None, 0);
        }
        finally
        {
            _moveGate.Release();
        }
    }

    // ------------------------------------------------------------------ chat / logout

    /// <summary>
    /// Leaves the world the way the real client quits: CSLeaveWorld(0), wait for SCPrepareLeaveWorld's
    /// timer and SCLeaveWorldGranted, then close the connection.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (_world is null)
            return;
        if (Stage == ClientStage.InWorld || Stage == ClientStage.EnteringWorld)
        {
            _quitting = true;
            SetStage(ClientStage.LeavingWorld, "leaving world (quit)");
            _leaveGrantedTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prepare = Wait(Channel.Game, [Opcodes.SCPrepareLeaveWorld], ct);
            SendGame(Opcodes.CSLeaveWorld, new WireWriter().U8(0));
            var (_, pbody) = await prepare.ConfigureAwait(false);
            var delay = BitConverter.ToInt32(pbody, 0);
            await _leaveGrantedTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(delay) + _options.RequestTimeout, ct)
                .ConfigureAwait(false);
        }
        Close("logged out");
    }

    // ------------------------------------------------------------------ receive loops

    private async Task LoginLoopAsync(FrameSocket socket)
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var frame = await socket.ReadFrameAsync(_life.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                if (frame.Length < 2)
                    continue;
                var op = BitConverter.ToUInt16(frame, 0);
                var body = frame.AsSpan(2).ToArray();
                if (op == Opcodes.ACPing)
                    SendLogin(Opcodes.CAPong, new WireWriter().Bytes(body.AsSpan(0, Math.Min(8, body.Length))));
                else if (op == Opcodes.ACLoginDenied)
                    Emit(new ErrorEvent($"login denied, reason {body[0]}", false));
                Complete(Channel.Login, op, body);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        catch (Exception e)
        {
            Emit(new ErrorEvent("login receive loop failed: " + e.Message, false, e));
        }
        FailWaiters(Channel.Login, "login connection closed");
    }

    private async Task WorldLoopAsync(FrameSocket socket)
    {
        var reason = "world connection closed by server";
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var frame = await socket.ReadFrameAsync(_life.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                HandleWorldFrame(frame);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            reason = "closed";
        }
        catch (IOException e)
        {
            reason = "world connection lost: " + e.Message;
        }
        catch (Exception e)
        {
            reason = "world receive loop failed: " + e.Message;
            Emit(new ErrorEvent(reason, true, e));
        }
        FailWaiters(Channel.Game, reason);
        FailWaiters(Channel.Proxy, reason);
        _characterListTcs?.TrySetException(new IOException(reason));
        _statesDoneTcs?.TrySetException(new IOException(reason));
        if (_quitting)
            _leaveGrantedTcs?.TrySetResult(true);
        else
            _leaveGrantedTcs?.TrySetException(new IOException(reason));
        Close(reason);
    }

    private void HandleWorldFrame(byte[] frame)
    {
        if (frame.Length < 2 || frame[0] != 0xDD)
        {
            Log($"ignored frame of {frame.Length} bytes (no 0xDD signature)");
            return;
        }
        var level = frame[1];
        byte[] plain;
        int opOffset;
        switch (level)
        {
            case 1:
                plain = frame;
                opOffset = 4; // dd 01 crc count
                break;
            case 2:
                plain = frame;
                opOffset = 2;
                break;
            case 5:
                plain = GameCrypto.StoC(frame.AsSpan(2));
                opOffset = 2; // crc count
                break;
            default:
                Log($"ignored level-{level} frame ({frame.Length} bytes)");
                return;
        }
        if (plain.Length < opOffset + 2)
            return;
        var op = BitConverter.ToUInt16(plain, opOffset);
        var body = plain.AsSpan(opOffset + 2).ToArray();
        try
        {
            if (level == 2)
                HandleProxy(op, body);
            else
                HandleGame(op, level, body);
        }
        catch (WireException e)
        {
            Emit(new ErrorEvent($"decode of opcode 0x{op:X3} failed: {e.Message}", false, e));
        }
        Complete(level == 2 ? Channel.Proxy : Channel.Game, op, body);
    }

    private void HandleProxy(ushort op, byte[] body)
    {
        switch (op)
        {
            case Opcodes.ChangeState:
                OnChangeState(BitConverter.ToInt32(body, 0));
                break;
            case Opcodes.Ping:
            {
                // Pong: echo tm, when and local; elapsed 0; remote/world from our clock.
                var r = new WireReader(body);
                var tm = r.S64();
                var when = r.S64();
                var local = r.U32();
                var now = (uint)(_clock.ElapsedMilliseconds & int.MaxValue);
                SendProxy(Opcodes.Pong, new WireWriter().S64(tm).S64(when).S64(0).S64((long)now * 1000).U32(local).U32(now));
                break;
            }
            case Opcodes.Pong:
            case Opcodes.SetGameType:
                break;
            default:
                NoteUnhandled(op, 2, body);
                break;
        }
    }

    private void OnChangeState(int state)
    {
        _changeState = state;
        Log($"ChangeState({state})");
        switch (state)
        {
            case 0:
                if (_quitting)
                    return; // quitting: the real client closes here
                _awaitingLobby = true;
                SendProxy(Opcodes.FinishState, new WireWriter().S32(0));
                break;
            case 1:
                SendProxy(Opcodes.FinishState, new WireWriter().S32(1));
                break;
            case 2:
                // Lobby. The first time, answer with the RSA key reply; FinishState(2) is sent on select.
                if (!_encrypted && _cipher is not null)
                {
                    SendGame(Opcodes.CSAesXorKey, new WireWriter()
                        .S32(_cipher.EncryptedAesKey.Length)
                        .S16((short)_cipher.EncryptedXorKey.Length)
                        .Bytes(_cipher.EncryptedAesKey)
                        .Bytes(_cipher.EncryptedXorKey));
                    _encrypted = true;
                }
                else if (_awaitingLobby)
                {
                    // Back in the lobby after leaving the world: the encrypted session re-requests the list.
                    SendGame(Opcodes.CSAesXorKey, new WireWriter());
                }
                break;
            case 3:
            case 4:
            case 5:
                SendProxy(Opcodes.FinishState, new WireWriter().S32(state));
                break;
            case 6:
                // The real client spawns here: CSSpawnCharacter carries the visual options (flag byte 0 = none set).
                SendGame(Opcodes.CSSpawnCharacter, new WireWriter().U8(0));
                SendProxy(Opcodes.FinishState, new WireWriter().S32(6));
                break;
            case 7:
                SendGame(Opcodes.CSBroadcastOpenEquipInfo, new WireWriter().Bool(false));
                SendProxy(Opcodes.FinishState, new WireWriter().S32(7));
                _statesDoneTcs?.TrySetResult(7);
                break;
        }
    }

    private static string TraceDetail(GameEvent ev) => ev switch
    {
        UnitAppearedEvent a => $"unit {a.Unit.UnitId} t{a.Unit.TemplateId} {a.Unit.Kind} at {a.Unit.Position.X:F0},{a.Unit.Position.Y:F0}",
        UnitsRemovedEvent r => $"removed {r.UnitIds.Count} [{string.Join(' ', r.UnitIds.Take(12))}]",
        TeleportedEvent t => $"{t}",
        ServerErrorEvent err => $"error {err.Error}/{err.Error2} type {err.Type} notify {err.IsNotify}",
        _ => "",
    };

    // X2_TRACE_PACKETS=1 (diagnostics): logs every game packet (opcode name, size) and every event it produces, with a timestamp
    private static readonly bool TracePackets = Environment.GetEnvironmentVariable("X2_TRACE_PACKETS") == "1";
    private static readonly Lazy<Dictionary<ushort, string>> OpcodeNames = new(() =>
    {
        var names = new Dictionary<ushort, string>();
        foreach (var f in typeof(Opcodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (f.FieldType == typeof(ushort) && f.GetValue(null) is ushort v && f.Name.StartsWith("SC", StringComparison.Ordinal)) names.TryAdd(v, f.Name);
        return names;
    });
    private static readonly System.Diagnostics.Stopwatch TraceClock = System.Diagnostics.Stopwatch.StartNew();

    private void HandleGame(ushort op, byte level, byte[] body)
    {
        if (TracePackets)
            Console.WriteLine($"[pkt] {TraceClock.Elapsed.TotalSeconds:F3} 0x{op:X3} {OpcodeNames.Value.GetValueOrDefault(op, "?")} L{level} {body.Length}B");
        switch (op)
        {
            case Opcodes.SCCreateCharacterResponse:
                Emit(new CharacterCreatedEvent(PacketParsers.ReadLobbyCharacter(new WireReader(body))));
                break;
            case Opcodes.SCCharacterCreationFailed:
                Emit(new CharacterCreationFailedEvent(body.Length > 0 ? body[0] : (byte)0));
                break;
            case Opcodes.SCDeleteCharacterResponse:
                Emit(new CharacterDeleteResponseEvent(CharacterLobbyPacketWriters.ReadDeleteResponse(body)));
                break;
            case Opcodes.SCCharacterDeleted:
            {
                var r = new WireReader(body);
                var characterId = r.U64();
                var characterName = r.Str();
                Characters = Characters.Where(c => c.Id != characterId).ToList();
                Emit(new CharacterDeletedEvent(characterId, characterName));
                break;
            }
            case Opcodes.SCCancelCharacterDeleteResponse:
                Emit(new CharacterDeleteCancelResponseEvent(CharacterLobbyPacketWriters.ReadCancelDeleteResponse(body)));
                break;
            case Opcodes.SCCharacterList:
            {
                var (last, list) = PacketParsers.ParseCharacterList(body);
                lock (_pendingCharacters)
                {
                    _pendingCharacters.AddRange(list);
                    if (!last)
                        break;
                    var all = _pendingCharacters.ToList();
                    _pendingCharacters.Clear();
                    Characters = all;
                    Emit(new CharacterListEvent(all));
                    _characterListTcs?.TrySetResult(all);
                }
                break;
            }
            case Opcodes.SCUnitState:
            {
                var unit = PacketParsers.ParseUnitState(body);
                if (unit.DecodeError is not null)
                    Log($"unit {unit.UnitId} ({unit.Kind} {unit.TemplateId}) partially decoded: {unit.DecodeError}");
                Emit(new UnitAppearedEvent(unit));
                // Keep the established scene-spawn event, while the combat family contributes
                // the complete unit-state event and its active/passive skill lists.
                RoutePacketFamilies(op, body);
                break;
            }
            case Opcodes.SCUnitMovements:
                foreach (var m in PacketParsers.ParseUnitMovements(body))
                    Emit(new UnitMovedEvent(m));
                break;
            case Opcodes.SCOneUnitMovement:
                Emit(new UnitMovedEvent(PacketParsers.ParseOneUnitMovement(body)));
                break;
            case Opcodes.SCUnitAttached:
            {
                var r = new WireReader(body);
                var child = r.Bc();
                var point = r.U8();
                var parent = point == 0xFF ? 0u : r.Bc();
                Emit(new UnitAttachedEvent(child, point, parent, r.U8()));
                break;
            }
            case Opcodes.SCUnitDetached:
            {
                var r = new WireReader(body);
                Emit(new UnitDetachedEvent(r.Bc(), r.U8()));
                break;
            }
            case Opcodes.SCUnitFlyingStateChanged:
            {
                var r = new WireReader(body);
                Emit(new UnitFlyingStateChangedEvent(r.Bc(), r.Bool()));
                break;
            }
            case Opcodes.SCUnitsRemoved:
                Emit(new UnitsRemovedEvent(PacketParsers.ParseUnitsRemoved(body), false));
                break;
            case Opcodes.SCErrorMsg when body.Length >= 9:
            {
                var r = new WireReader(body);
                Emit(new ServerErrorEvent(r.S16(), r.S16(), r.U32(), r.Bool()));
                break;
            }
            case Opcodes.SCDoodadsCreated:
                foreach (var d in PacketParsers.ParseDoodadsCreated(body))
                    Emit(new UnitAppearedEvent(d));
                break;
            case Opcodes.SCDoodadsRemoved:
                Emit(new UnitsRemovedEvent(PacketParsers.ParseDoodadsRemoved(body), true));
                break;
            case Opcodes.SCDoodadCreated:
                Emit(new UnitAppearedEvent(PacketParsers.ParseDoodadCreated(body)));
                break;
            case Opcodes.SCDoodadRemoved:
                Emit(new UnitsRemovedEvent([PacketParsers.ParseDoodadRemoved(body)], true));
                break;
            case Opcodes.SCUnitPoints:
            {
                var r = new WireReader(body);
                var id = r.Bc();
                Emit(new UnitPointsEvent(id, (int)(r.S64() / 100), (int)(r.S64() / 100)));
                break;
            }
            case Opcodes.SCTeleportUnit:
            {
                var r = new WireReader(body);
                var reason = r.U8();
                r.S16();
                var pos = r.Position();
                OwnPosition = pos;
                Emit(new TeleportedEvent(reason, pos));
                break;
            }
            case Opcodes.SCChatMessage:
                if (!RoutePacketFamilies(op, body))
                    Emit(PacketParsers.ParseChat(body));
                break;
            case Opcodes.SCPrepareLeaveWorld:
            {
                var r = new WireReader(body);
                Emit(new LeavingWorldEvent(r.S32(), r.U8()));
                break;
            }
            case Opcodes.SCLeaveWorldGranted:
                Progress($"leave world granted (target {body[0]})");
                _leaveGrantedTcs?.TrySetResult(true);
                if (!_quitting)
                {
                    OwnUnitId = 0;
                    Stage = ClientStage.CharacterSelect;
                }
                break;
            case Opcodes.SCLeaveWorldCanceled:
                Progress("leave world canceled by server");
                break;
            case Opcodes.X2EnterWorldResponse:
                OnEnterWorldResponse(body);
                break;
            case Opcodes.SCResultRestrictCheck:
            case Opcodes.SCCheckRaceCongestionResponse:
            case Opcodes.SCCharacterState:
                break; // consumed by waiters
            default:
                if (!RoutePacketFamilies(op, body))
                    NoteUnhandled(op, level, body);
                break;
        }
    }

    private bool RoutePacketFamilies(ushort op, byte[] body)
    {
        foreach (var family in _options.PacketParserFamilies)
        {
            if (!family.TryParse(op, body, out var events))
                continue;
            foreach (var gameEvent in events)
            {
                Emit(gameEvent);
                // Keep the established HUD/X2 bridge contract while exposing the richer social event.
                if (gameEvent is SocialProtocolGameEvent { Value: SocialChatMessageEvent chat })
                    Emit(new ChatEvent(chat.Channel.Type, chat.SenderObjectId, chat.SenderName, chat.Message));
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// X2EnterWorldResponse: reason i16, stream token u32, stream port u16, server time u64, tz u32,
    /// pubKeySize u16, [u16 len][RSA public key blob], nat addr i32, nat port u16, authority i32.
    /// </summary>
    private void OnEnterWorldResponse(byte[] body)
    {
        var r = new WireReader(body);
        _enterWorldReason = r.S16();
        if (_enterWorldReason != 0)
            return;
        r.U32();
        var streamPort = r.U16();
        r.U64();
        r.U32();
        r.U16();
        var pubKey = r.Blob();
        _cipher = ClientCipher.Create(pubKey);
        SetStage(ClientStage.KeyExchange,
            $"world accepted the cookie; RSA-{BitConverter.ToInt32(pubKey, 0)} public key received (stream channel :{streamPort} not used)");
    }

    private void NoteUnhandled(ushort op, byte level, byte[] body)
    {
        _unhandled.AddOrUpdate(op, 1, (_, n) => n + 1);
        if (_options.EmitUnhandledPackets)
            Emit(new RawPacketEvent(op, level, body));
    }

    // ------------------------------------------------------------------ keepalive

    private async Task KeepAliveLoopAsync()
    {
        // The real client pings in bursts: five level-2 pings one second apart, then about six seconds quiet.
        var burst = 0;
        try
        {
            while (!_life.IsCancellationRequested && _world is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(burst < 5 ? 1 : 6), _life.Token).ConfigureAwait(false);
                burst = burst < 5 ? burst + 1 : 0;
                if (burst != 0)
                {
                    SendProxy(Opcodes.Ping, new WireWriter()
                        .S64(_pingTm)
                        .S64(_clock.ElapsedMilliseconds)
                        .U32((uint)Environment.TickCount));
                }
                if (Stage == ClientStage.InWorld && _options.IdleMoveInterval > TimeSpan.Zero && _moveGate.CurrentCount > 0)
                {
                    var idleMs = _clock.ElapsedMilliseconds - Interlocked.Read(ref _lastMoveSentMs);
                    if (idleMs >= _options.IdleMoveInterval.TotalMilliseconds)
                    {
                        var last = _lastMovement;
                        if (last == null)
                            SendMovement(OwnPosition, OwnYaw, Vector3.Zero, MoveFlags.None, 0);
                        else
                        {
                            var actorFlags = last.Flags == (byte)MoveFlags.Jumping ? (ushort)0
                                : last.Flags == (byte)MoveFlags.Stopping && last.Stance == 1 ? (ushort)4
                                : last.ActorFlags;
                            SendMovement(last.Position, last.Yaw, Vector3.Zero, MoveFlags.None, 0,
                                last.Stance, last.Alertness, actorFlags, unitId: last.UnitId);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log("keepalive stopped: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ sending

    private void SendLogin(ushort op, WireWriter body)
    {
        var login = _login ?? throw new InvalidOperationException("no login connection");
        var payload = new WireWriter().U16(op).Bytes(body.ToArray()).ToArray();
        login.WriteFrame(payload);
    }

    /// <summary>Proxy (level 2) packet: [0xDD][0x02][opcode][body], never encrypted.</summary>
    private void SendProxy(ushort op, WireWriter body)
    {
        var world = _world ?? throw new InvalidOperationException("no world connection");
        var payload = new WireWriter().U8(0xDD).U8(2).U16(op).Bytes(body.ToArray()).ToArray();
        world.WriteFrame(payload);
    }

    /// <summary>
    /// Game packet: level 1 [0xDD][0x01][crc8][count][opcode][body] before the key exchange, level 5
    /// [0xDD][0x05][hash][AES+XOR([crc8][count][opcode][body])] after it.
    /// </summary>
    private void SendGame(ushort op, WireWriter body)
    {
        var world = _world ?? throw new InvalidOperationException("no world connection");
        lock (_worldSendLock)
        {
            var inner = new WireWriter().U8(_csCount).U16(op).Bytes(body.ToArray()).ToArray();
            _csCount++;
            var plain = new byte[inner.Length + 1];
            plain[0] = GameCrypto.Crc8(inner);
            inner.CopyTo(plain, 1);
            byte[] payload;
            if (_encrypted && _cipher is not null)
                payload = new WireWriter().U8(0xDD).U8(5).Bytes(_cipher.Encrypt(plain)).ToArray();
            else
                payload = new WireWriter().U8(0xDD).U8(1).Bytes(plain).ToArray();
            world.WriteFrame(payload);
        }
    }

    /// <summary>Sends a verified protocol writer's opcode and body through normal game framing and encryption.</summary>
    public void SendGame(ushort opcode, ReadOnlySpan<byte> body)
    {
        SendGame(opcode, new WireWriter().Bytes(body));
    }

    /// <summary>Equip an owned title (CSChangeAppellation, 0x14C).</summary>
    public void SendChangeAppellation(uint appellationId) =>
        SendGame(AchievementProtocol.CSChangeAppellation, new WireWriter().U32(appellationId));

    public void SendRequestTodayAssignment(uint realStep, sbyte request) =>
        SendGame(AchievementProtocol.CSRequestTodayAssignment,
            AchievementProtocol.WriteRequestTodayAssignment(realStep, request));

    public void SendResetTodayAssignment(uint realStep, ulong moneyAmount) =>
        SendGame(AchievementProtocol.CSResetTodayAssignment,
            AchievementProtocol.WriteResetTodayAssignment(realStep, moneyAmount));

    public void SendAcceptAllTodayAssignment(sbyte todayType, IReadOnlyList<uint> realSteps) =>
        SendGame(AchievementProtocol.CSTodayAssignmentAcceptAll,
            AchievementProtocol.WriteAcceptAllTodayAssignment(todayType, realSteps));

    /// <summary>Sends a proven combat request body through the normal game-channel framing and encryption.</summary>
    public void SendGame(CombatOutboundPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        SendGame(packet.Opcode, new WireWriter().Bytes(packet.Body));
    }

    /// <summary>Sends a verified character-lobby request through normal game framing and encryption.</summary>
    public void SendGame(LobbyOutboundPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        SendGame(packet.Opcode, new WireWriter().Bytes(packet.Body));
    }

    // ------------------------------------------------------------------ waiters

    private enum Channel { Login, Game, Proxy }

    private sealed record Waiter(Channel Channel, ushort[] Opcodes, Func<byte[], bool>? Match,
        TaskCompletionSource<(ushort, byte[])> Tcs);

    private Task<(ushort Op, byte[] Body)> Wait(Channel channel, ushort[] opcodes, CancellationToken ct,
        Func<byte[], bool>? match = null)
    {
        var tcs = new TaskCompletionSource<(ushort, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new Waiter(channel, opcodes, match, tcs);
        lock (_waitLock)
            _waiters.Add(waiter);
        return tcs.Task.WaitAsync(_options.RequestTimeout, ct).ContinueWith(t =>
        {
            lock (_waitLock)
                _waiters.Remove(waiter);
            if (t.IsFaulted && t.Exception!.InnerException is TimeoutException)
                throw new TimeoutException($"no {string.Join("/", opcodes.Select(o => $"0x{o:X3}"))} from {channel} within {_options.RequestTimeout.TotalSeconds:0}s");
            return t.GetAwaiter().GetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Complete(Channel channel, ushort op, byte[] body)
    {
        List<Waiter>? hit = null;
        lock (_waitLock)
        {
            foreach (var w in _waiters)
            {
                if (w.Channel == channel && Array.IndexOf(w.Opcodes, op) >= 0 && (w.Match is null || w.Match(body)))
                    (hit ??= []).Add(w);
            }
            if (hit is not null)
                foreach (var w in hit)
                    _waiters.Remove(w);
        }
        if (hit is not null)
            foreach (var w in hit)
                w.Tcs.TrySetResult((op, body));
    }

    private void FailWaiters(Channel channel, string reason)
    {
        List<Waiter> failed;
        lock (_waitLock)
        {
            failed = _waiters.Where(w => w.Channel == channel).ToList();
            foreach (var w in failed)
                _waiters.Remove(w);
        }
        foreach (var w in failed)
            w.Tcs.TrySetException(new IOException(reason));
    }

    // ------------------------------------------------------------------ helpers

    private void SetStage(ClientStage stage, string message)
    {
        Stage = stage;
        Emit(new ProgressEvent(stage, message));
    }

    private void Progress(string message) => Emit(new ProgressEvent(Stage, message));

    private void Emit(GameEvent ev)
    {
        if (TracePackets)
            Console.WriteLine($"[pkt-ev] {TraceClock.Elapsed.TotalSeconds:F3} {ev.GetType().Name} {TraceDetail(ev)}");
        _events.Enqueue(ev);
        while (_events.Count > _options.MaxQueuedEvents && _events.TryDequeue(out _))
        {
        }
        try
        {
            EventRaised?.Invoke(ev);
        }
        catch (Exception e)
        {
            Log("event handler threw: " + e.Message);
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

    private void Close(string reason)
    {
        var hadConnection = _world is not null || _login is not null;
        _life.Cancel();
        _world?.Dispose();
        _login?.Dispose();
        _world = null;
        _login = null;
        OwnUnitId = 0;
        if (hadConnection)
        {
            Stage = ClientStage.Disconnected;
            Emit(new DisconnectedEvent(reason));
        }
    }

    public void Dispose() => Close("disposed");

    public ValueTask DisposeAsync()
    {
        Close("disposed");
        return ValueTask.CompletedTask;
    }
}

/// <summary>MoveTypeFlags on CSMoveUnit.</summary>
[Flags]
public enum MoveFlags : byte
{
    None = 0x00,
    Moving = 0x02,
    Stopping = 0x04,
    Jumping = 0x06,
    InCombat = 0x08,
    StandingOnObject = 0x40,
}
