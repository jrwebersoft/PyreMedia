namespace PyreMedia.Core.Organizing;

/// <summary>
/// Manual per-file episode assignments, set from the renumber screen.
///
/// Numbers in filenames are sometimes wrong from end to end, and no offset or
/// parsing rule can fix that - the mapping has to be stated. An override
/// replaces parsing and offset entirely for the files it covers; everything
/// else plans as normal.
/// </summary>
public sealed class EpisodeOverrides
{
    /// <summary>Full file path to the season/episode it should be treated as.</summary>
    public Dictionary<string, (int Season, int Episode)> Map { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files to leave alone entirely - no rename, no problem row.</summary>
    public HashSet<string> Skip { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Map.Count == 0 && Skip.Count == 0;

    public (int Season, int Episode)? For(string file) =>
        Map.TryGetValue(file, out var v) ? v : null;

    public bool IsSkipped(string file) => Skip.Contains(file);
}
