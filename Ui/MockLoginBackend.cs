namespace AAEmu.GodotViewer.Ui;

public sealed class MockLoginBackend : ILoginBackend
{
    public event Action<ulong> CharacterDeleted { add { } remove { } }
    private static readonly WorldInfo[] Worlds =
    [
        new(1, "Nuia", true, "42%"),
        new(2, "Haranya", false, "Offline")
    ];

    private readonly List<CharacterInfo> _characters =
    [
        new(1001, "Arielle", 55, "Elf", "Female", "Marianople"),
        new(1002, "Bram", 32, "Nuian", "Male", "Two Crowns"),
        new(1003, "Sora", 12, "Ferre", "Female", "Ynystere")
    ];

    public async Task<LoginResult> LoginAsync(string host, int port, string account, string password, CancellationToken ct)
    {
        await DelayAsync(ct);
        return string.Equals(account, "fail", StringComparison.OrdinalIgnoreCase)
            ? new LoginResult(false, "The account name was rejected.")
            : new LoginResult(true, string.Empty);
    }

    public async Task<IReadOnlyList<WorldInfo>> GetWorldsAsync(CancellationToken ct)
    {
        await DelayAsync(ct);
        return Worlds;
    }

    public async Task<IReadOnlyList<CharacterInfo>> SelectWorldAsync(int worldId, CancellationToken ct)
    {
        await DelayAsync(ct);
        return _characters.ToArray();
    }

    public async Task<CharacterInfo> CreateCharacterAsync(CharacterCreateRequest request, CancellationToken ct)
    {
        await DelayAsync(ct);
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidOperationException("Enter a character name.");
        if (_characters.Any(c => c.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That character name is already in use.");
        var character = new CharacterInfo((ulong)(_characters.Max(c => (long)c.Id) + 1), request.Name.Trim(), 1,
            request.Race, request.Gender, "Starting area", request.ModelId,
            new Dictionary<int, long>(request.Equipment), request.Appearance);
        _characters.Add(character);
        return character;
    }

    public async Task<CharacterDeleteResult> DeleteCharacterAsync(ulong characterId, CancellationToken ct)
    {
        await DelayAsync(ct);
        if (_characters.All(c => c.Id != characterId)) return new CharacterDeleteResult(0);
        var requested = DateTimeOffset.UtcNow;
        return new CharacterDeleteResult(2, requested, requested.AddDays(1));
    }

    public async Task<CharacterDeleteResult> CancelCharacterDeleteAsync(ulong characterId, CancellationToken ct)
    {
        await DelayAsync(ct);
        return new CharacterDeleteResult(_characters.Any(c => c.Id == characterId) ? (byte)3 : (byte)4);
    }

    public async Task EnterWorldAsync(ulong characterId, CancellationToken ct) => await DelayAsync(ct);

    private static Task DelayAsync(CancellationToken ct) => Task.Delay(Random.Shared.Next(200, 601), ct);
}
