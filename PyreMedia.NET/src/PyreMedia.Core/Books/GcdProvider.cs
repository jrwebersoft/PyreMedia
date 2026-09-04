using Microsoft.Data.Sqlite;

namespace PyreMedia.Core.Books;

/// <summary>
/// The Grand Comics Database, read from its own SQLite dump.
///
/// First in the chain because asking it costs nothing. It publishes no API at
/// all - the data comes as a file you download once, about six gigabytes of it -
/// which turns out to be the better arrangement for this: no key, no rate limit,
/// no network, and an answer in milliseconds. Every other source is rationed.
///
/// The file is not bundled and could not be: the data is CC BY-SA 4.0, which
/// wants attribution and carries a share-alike obligation, and six gigabytes has
/// no business inside an installer. It is downloaded from comics.org with a free
/// account, exactly as ffmpeg is fetched from its own author.
///
/// Opened read-only. This is somebody's copy of a public dataset and there is no
/// reason for this program to be able to write to it.
/// </summary>
public sealed class GcdProvider(string? databasePath) : IComicProvider
{
    public string Name => "Grand Comics Database";

    public bool Ready =>
        !string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath);

    public string? NotReadyBecause => Ready
        ? null
        : string.IsNullOrWhiteSpace(databasePath)
            ? "no database file chosen"
            : "the database file is not where Settings says it is";

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());

        connection.Open();
        return connection;
    }

    /// <summary>
    /// Series whose name matches, most-published first.
    ///
    /// Matched with LIKE rather than the full-text index, because the index is
    /// something tools build on top of the dump rather than something the dump
    /// contains - assuming it is there would fail on a file straight from
    /// comics.org, which is the only file anybody will have.
    /// </summary>
    public async Task<IReadOnlyList<ComicSeries>> SearchAsync(
        string series, string? year, CancellationToken ct = default)
    {
        if (!Ready || string.IsNullOrWhiteSpace(series)) return [];

        await using var db = Open();
        await using var cmd = db.CreateCommand();

        cmd.CommandText = """
            SELECT  s.id, s.name, s.year_began, s.issue_count, p.name
            FROM    gcd_series s
            LEFT JOIN gcd_publisher p ON p.id = s.publisher_id
            WHERE   s.name LIKE $name
              AND  ($year IS NULL OR s.year_began = $year)
            ORDER BY s.issue_count DESC
            LIMIT 40
            """;

        cmd.Parameters.AddWithValue("$name", series.Trim());
        cmd.Parameters.AddWithValue("$year",
            int.TryParse(year, out var y) ? y : (object)DBNull.Value);

        var found = new List<ComicSeries>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new ComicSeries
            {
                Id = reader.GetInt64(0).ToString(),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                YearBegan = reader.IsDBNull(2) ? null : reader.GetInt32(2).ToString(),
                IssueCount = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Publisher = reader.IsDBNull(4) ? null : reader.GetString(4),
                Source = Name
            });
        }

        // Nothing exact - widen it. Done as a second query rather than always
        // using a wildcard, because "Batman%" returns two hundred series and
        // buries the one actually called Batman.
        if (found.Count == 0)
        {
            cmd.Parameters["$name"].Value = series.Trim() + "%";

            await using var wider = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await wider.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(new ComicSeries
                {
                    Id = wider.GetInt64(0).ToString(),
                    Name = wider.IsDBNull(1) ? "" : wider.GetString(1),
                    YearBegan = wider.IsDBNull(2) ? null : wider.GetInt32(2).ToString(),
                    IssueCount = wider.IsDBNull(3) ? null : wider.GetInt32(3),
                    Publisher = wider.IsDBNull(4) ? null : wider.GetString(4),
                    Source = Name
                });
            }
        }

        return found;
    }

    public async Task<IReadOnlyList<ComicIssue>> IssuesAsync(
        string seriesId, CancellationToken ct = default)
    {
        if (!Ready || !long.TryParse(seriesId, out var id)) return [];

        await using var db = Open();
        await using var cmd = db.CreateCommand();

        // variant_of_id filters out the second, third and fourth covers of the
        // same issue, which the database records as separate rows. A collection
        // holds one file per issue, not one per cover.
        cmd.CommandText = """
            SELECT  i.number, i.title, i.key_date
            FROM    gcd_issue i
            WHERE   i.series_id = $id
              AND   i.variant_of_id IS NULL
            ORDER BY i.sort_code
            """;

        cmd.Parameters.AddWithValue("$id", id);

        var issues = new List<ComicIssue>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var number = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
            if (number.Length == 0) continue;

            issues.Add(new ComicIssue
            {
                Number = number,
                Title = reader.IsDBNull(1) || reader.GetString(1).Length == 0 ? null : reader.GetString(1),
                Released = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return issues;
    }

    /// <summary>
    /// Whether a file really is a GCD dump, so a wrong choice is caught when it
    /// is made rather than at the first search.
    /// </summary>
    public static string? Check(string path)
    {
        if (!File.Exists(path)) return "There is no file there.";

        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

            db.Open();

            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('gcd_series','gcd_issue','gcd_publisher')";

            return Convert.ToInt32(cmd.ExecuteScalar()) == 3
                ? null
                : "That is a database, but not the Grand Comics Database - it has no gcd_series table.";
        }
        catch (Exception ex)
        {
            return $"That file could not be opened as a database: {ex.Message}";
        }
    }
}
