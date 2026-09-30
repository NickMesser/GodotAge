#nullable enable annotations
namespace AAEmu.GodotViewer.Ui;

public sealed record LoginResult(bool Ok, string Error);
public sealed record WorldInfo(int Id, string Name, bool Online, string Load);
public sealed record CharacterInfo(ulong Id, string Name, int Level, string Race, string Gender, string Zone,
    long ModelId = 0, IReadOnlyDictionary<int, long>? Equipment = null, LoginCharacterAppearance? Appearance = null,
    uint FactionId = 0, string FactionName = "");

/// <summary>Renderer-neutral character customization state used by login previews.</summary>
public sealed class LoginCharacterAppearance
{
    public long HairColorId, SkinColorId, FaceNormalMapId, BodyNormalMapId;
    public float FaceNormalMapWeight = 1, BodyNormalMapWeight = 1;
    public uint DefaultHairColor, LipColor, LeftPupilColor, RightPupilColor, EyebrowColor, DecoColor;
    public float MovableDecalWeight = 1, MovableDecalScale, MovableDecalRotate;
    public short MovableDecalMoveX, MovableDecalMoveY;
    public long[] DecalIds { get; } = new long[7];
    public float[] DecalWeights { get; } = [1, 1, 1, 1, 1, 1, 1];
    public byte[] Modifier = [];
    public long HairItemId, FaceItemId, BodyItemId, HornItemId, TailItemId;
    /// <summary>Horn colour (customizing_item_asset_colors id) and palette-hair two-tone colour (0xAABBGGRR) with its band widths.</summary>
    public long HornColorId;
    public uint TwoToneHairColor;
    public float TwoToneFirstWidth, TwoToneSecondWidth;
}

/// <summary>The completed character-creation choices passed to a login backend.</summary>
public sealed record CharacterCreateRequest(string Name, string Race, string Gender, IReadOnlyList<int> Abilities,
    long ModelId, IReadOnlyDictionary<int, long> Equipment, LoginCharacterAppearance Appearance, uint IntroZoneId = 0);
public sealed record CharacterDeleteResult(byte Status, DateTimeOffset? RequestedAt = null, DateTimeOffset? DeleteAt = null);

public interface ILoginBackend
{
    event Action<ulong>? CharacterDeleted;
    Task<LoginResult> LoginAsync(string host, int port, string account, string password, CancellationToken ct);
    Task<IReadOnlyList<WorldInfo>> GetWorldsAsync(CancellationToken ct);
    Task<IReadOnlyList<CharacterInfo>> SelectWorldAsync(int worldId, CancellationToken ct);
    Task<CharacterInfo> CreateCharacterAsync(CharacterCreateRequest request, CancellationToken ct);
    Task<CharacterDeleteResult> DeleteCharacterAsync(ulong characterId, CancellationToken ct);
    Task<CharacterDeleteResult> CancelCharacterDeleteAsync(ulong characterId, CancellationToken ct);
    Task EnterWorldAsync(ulong characterId, CancellationToken ct);
}
