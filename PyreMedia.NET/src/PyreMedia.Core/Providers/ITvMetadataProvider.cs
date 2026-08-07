using PyreMedia.Core.Models;

namespace PyreMedia.Core.Providers;

public interface IShowSearchProvider
{
    Task<IReadOnlyList<ShowSearchResult>> SearchAsync(
        string name, string? year, string language, CancellationToken ct = default);
}

public interface IEpisodeProvider
{
    /// <summary>Full show with seasons/episodes, or null if not found.</summary>
    Task<TvShow?> GetShowAsync(ShowSearchResult show, string language, CancellationToken ct = default);
}

/// <summary>Raised when a provider fails in a way worth telling the user about.</summary>
public sealed class MetadataException(string message, Exception? inner = null)
    : Exception(message, inner);
