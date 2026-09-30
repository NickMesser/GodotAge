using System.Collections;

namespace AAEmu.GodotViewer;

/// <summary>A compact 8 m raster for ocean surface and swimming queries.</summary>
internal sealed class OceanMaskCell(bool[,] tiles)
{
    public const int StepMeters = 8;
    public const int TilesPerSide = WorldStreamer.CellSize / StepMeters;
    private readonly bool[,] _tiles = tiles;

    public bool HasTiles
    {
        get
        {
            foreach (var tile in _tiles)
                if (tile)
                    return true;
            return false;
        }
    }

    public bool Contains(float localX, float localY)
    {
        if (localX < 0 || localY < 0 || localX >= WorldStreamer.CellSize || localY >= WorldStreamer.CellSize)
            return false;
        return _tiles[(int)(localX / StepMeters), (int)(localY / StepMeters)];
    }

    public IEnumerable<(int X, int Y)> WetTiles()
    {
        for (var y = 0; y < TilesPerSide; y++)
        for (var x = 0; x < TilesPerSide; x++)
            if (_tiles[x, y])
                yield return (x, y);
    }
}

internal static class OceanMaskBuilder
{
    /// <summary>
    /// Builds a deterministic fallback from authored coral seabed surfaces. This is an inferred data proxy where
    /// sparse cell Ocean records do not describe a coast; no native per-cell ocean flag was found in inspected files.
    /// </summary>
    public static OceanMaskCell Build(float[,] heights, int[,] surfaceIds, CellSurfaceTypes? surfaces, float oceanLevel)
    {
        var tiles = new bool[OceanMaskCell.TilesPerSide, OceanMaskCell.TilesPerSide];
        if (surfaces == null)
            return new OceanMaskCell(tiles);

        for (var y = 0; y < OceanMaskCell.TilesPerSide; y++)
        for (var x = 0; x < OceanMaskCell.TilesPerSide; x++)
        {
            // Tile centers are 4 samples (8 m) apart on the 2 m height/surface grid.
            var sx = x * 4 + 2;
            var sy = y * 4 + 2;
            var id = surfaceIds[sx, sy];
            if (heights[sx, sy] >= oceanLevel || id == TerrainSurfaceReader.None || !surfaces.ById.TryGetValue(id, out var surface))
                continue;

            var detail = (surface.DetailMaterial ?? "").Replace('\\', '/');
            tiles[x, y] = surface.Name.Contains("coral", StringComparison.OrdinalIgnoreCase) &&
                          detail.Contains("/wet/", StringComparison.OrdinalIgnoreCase);
        }
        return new OceanMaskCell(tiles);
    }
}
