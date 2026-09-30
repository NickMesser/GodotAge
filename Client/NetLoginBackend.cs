#nullable enable annotations
using AAEmu.GodotViewer.Net;
using AAEmu.GodotViewer.Ui;

namespace AAEmu.GodotViewer.Client;

/// <summary>
/// <see cref="ILoginBackend"/> over the real network client. AAEmu's web login trusts the launcher account name and
/// takes the launcher token in place of a password; with an empty account the dev account from the local client
/// launcher (the launcher setting, <see cref="ClientPaths.Launcher"/>) is used.
/// </summary>
internal sealed class NetLoginBackend : ILoginBackend, IDisposable
{
    private static readonly string[] RaceNames = ["none", "nuian", "fairy", "dwarf", "elf", "hariharan", "ferre", "returned", "warborn", "daru"];

    private IReadOnlyList<WorldServerInfo> _worlds = [];

    public GameClient Client { get; private set; }
    public EnteredWorldEvent Entered { get; private set; }
    public IReadOnlyList<LobbyCharacter> Characters { get; private set; } = [];
    public event Action<ulong>? CharacterDeleted;

    public async Task<LoginResult> LoginAsync(string host, int port, string account, string password, CancellationToken ct)
    {
        Client?.Dispose();
        var launcher = string.IsNullOrWhiteSpace(account) ? LauncherConfig.TryRead() : null;
        if (string.IsNullOrWhiteSpace(account) && launcher == null)
            return new LoginResult(false, "No account given and no launcher config found.");
        Client = new GameClient(new GameClientOptions
        {
            UserName = launcher?.UserName ?? account.Trim(),
            UserToken = launcher?.UserToken ?? password ?? "",
            GameId = launcher?.GameId ?? 1,
            LoginHost = host,
            LoginPort = port,
            EmitUnhandledPackets = true,
        });
        Client.EventRaised += OnClientEvent;
        try
        {
            _worlds = await Client.LoginAsync(ct);
            return new LoginResult(true, "");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new LoginResult(false, e.Message);
        }
    }

    public Task<IReadOnlyList<WorldInfo>> GetWorldsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorldInfo>>(_worlds
            .Select(w => new WorldInfo(w.Id, w.Name, w.Available, $"congestion {w.Congestion}"))
            .ToList());

    public async Task<IReadOnlyList<CharacterInfo>> SelectWorldAsync(int worldId, CancellationToken ct)
    {
        Characters = await Client.ConnectWorldAsync((byte)worldId, ct);
        return Characters.Select(ToCharacterInfo).ToList();
    }

    public async Task<CharacterInfo> CreateCharacterAsync(CharacterCreateRequest request, CancellationToken ct)
    {
        LobbyCharacter created;
        try
        {
            created = await Client.CreateCharacterAsync(request, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            var key = CreationErrorKey(e.Message);
            if (key != null) throw new InvalidOperationException(key, e);
            throw;
        }
        Characters = Characters.Where(c => c.Id != created.Id).Append(created).ToArray();
        return ToCharacterInfo(created);
    }

    public async Task<CharacterDeleteResult> DeleteCharacterAsync(ulong characterId, CancellationToken ct)
    {
        var response = await Client.DeleteCharacterAsync(characterId, ct).ConfigureAwait(false);
        if (response.Status == 1)
            Characters = Characters.Where(c => c.Id != characterId).ToArray();
        return new CharacterDeleteResult(response.Status, Unix(response.DeleteRequestedTime), Unix(response.DeleteDelay));
    }

    public async Task<CharacterDeleteResult> CancelCharacterDeleteAsync(ulong characterId, CancellationToken ct)
    {
        var response = await Client.CancelCharacterDeleteAsync(characterId, ct).ConfigureAwait(false);
        return new CharacterDeleteResult(response.Status);
    }

    public async Task EnterWorldAsync(ulong characterId, CancellationToken ct) =>
        Entered = await Client.EnterWorldAsync(characterId, ct);

    public void Dispose() => Client?.Dispose();

    private void OnClientEvent(GameEvent gameEvent)
    {
        if (gameEvent is not CharacterDeletedEvent deleted) return;
        Characters = Characters.Where(c => c.Id != deleted.CharacterId).ToArray();
        CharacterDeleted?.Invoke(deleted.CharacterId);
    }

    private static IReadOnlyDictionary<int, long> Equipment(LobbyCharacter c) => c.Equipment
        .Where(e => e.TemplateId != 0 || e.ImageTemplateId != 0)
        .ToDictionary(e => e.Slot, e => (long)(e.ImageTemplateId != 0 ? e.ImageTemplateId : e.TemplateId));

    private static CharacterInfo ToCharacterInfo(LobbyCharacter c) => new(c.Id, c.Name, c.Level,
        c.Race < RaceNames.Length ? RaceNames[c.Race] : $"race {c.Race}", c.Gender == 2 ? "female" : "male",
        $"zone {c.ZoneId}", 0, Equipment(c), Appearance(c.Appearance), c.FactionId, c.FactionName);

    private static DateTimeOffset? Unix(ulong value) => value is > 0 and <= long.MaxValue
        ? DateTimeOffset.FromUnixTimeSeconds((long)value) : null;

    private static string? CreationErrorKey(string message) => message switch
    {
        "character creation rejected (reason 4)" => "error_char_name_duplicate",
        "character creation rejected (reason 5)" => "error_char_name_forbidden",
        "character creation rejected (reason 3)" => "error_char_name_pending",
        _ => null,
    };

    private static LoginCharacterAppearance? Appearance(AppearanceParams? a)
    {
        if (a == null || a.Tier == AppearanceTier.None) return null;
        var look = new LoginCharacterAppearance { HairColorId = a.HairColor, SkinColorId = a.SkinColor,
            DefaultHairColor = a.DefaultHairColor, BodyNormalMapId = a.BodyNormalMap, BodyNormalMapWeight = a.BodyWeight,
            TwoToneHairColor = a.TwoToneHairColor, TwoToneFirstWidth = a.TwoToneFirstWidth, TwoToneSecondWidth = a.TwoToneSecondWidth };
        if (a.Face is not { } face) return look;
        look.FaceNormalMapId = face.NormalMapId; look.FaceNormalMapWeight = face.NormalMapWeight;
        look.DecalIds[0] = face.MovableDecalAssetId; look.DecalWeights[0] = face.MovableDecalWeight;
        look.MovableDecalWeight = face.MovableDecalWeight; look.MovableDecalScale = face.MovableDecalScale;
        look.MovableDecalRotate = face.MovableDecalRotate; look.MovableDecalMoveX = face.MovableDecalMoveX;
        look.MovableDecalMoveY = face.MovableDecalMoveY;
        for (var i = 0; i < face.FixedDecalAssetIds.Length && i + 1 < look.DecalIds.Length; i++)
        { look.DecalIds[i + 1] = face.FixedDecalAssetIds[i]; if (i < face.FixedDecalWeights.Length) look.DecalWeights[i + 1] = face.FixedDecalWeights[i]; }
        look.LipColor = face.LipColor; look.LeftPupilColor = face.LeftPupilColor; look.RightPupilColor = face.RightPupilColor;
        look.EyebrowColor = face.EyebrowColor; look.DecoColor = face.DecoColor; look.Modifier = face.Modifier;
        return look;
    }
}
