namespace Arable;

/// <summary>
/// What the player has built on a cell. This is the *placement* layer — it is
/// owned by the player and can always be cleared back to <see cref="Empty"/>,
/// which never touches the terrain underneath (see <see cref="TerrainType"/>).
/// </summary>
public enum TileType
{
    Empty = 0,

    /// <summary>Traversable by machines.</summary>
    Road = 1,

    /// <summary>Workable land.</summary>
    Field = 2,
}

/// <summary>
/// What the land *is*. This is the *terrain* layer — generated from the world
/// seed and never edited by the player. Soil cells also carry a fertility
/// scalar (see <see cref="WorldGrid.GetFertility"/>); rock and water are
/// unusable ground and have no fertility.
/// </summary>
public enum TerrainType
{
    /// <summary>
    /// Not a terrain kind: the cell lies outside the generated map. Returned by
    /// <see cref="WorldGrid.GetTerrain"/> so callers can tell "off the map" from
    /// any real terrain without a second bounds call.
    /// </summary>
    OutOfBounds = 0,

    /// <summary>Buildable, farmable ground; carries a fertility value.</summary>
    Soil = 1,

    /// <summary>Unusable ground: bare rock.</summary>
    Rock = 2,

    /// <summary>Unusable ground: open water.</summary>
    Water = 3,
}
