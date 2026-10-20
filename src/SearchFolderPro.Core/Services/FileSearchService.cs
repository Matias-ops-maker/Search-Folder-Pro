using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SearchFolderPro.Core.Models;

namespace SearchFolderPro.Core.Services;

public sealed class FileSearchService
{
    private const int ResultLimit = 1000;
    private readonly string _databasePath;

    public FileSearchService(string? databasePath = null)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SearchFolderPro",
            "index.db");

        _databasePath = Path.GetFullPath(databasePath ?? defaultPath);
    }

    public async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        FileSearchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<FileSearchResult>();
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Name, f.Path, f.DirectoryPath, f.Extension, f.SizeBytes, f.ModifiedUtcTicks
            FROM Files AS f
            WHERE ($match = ''
                OR f.Id IN (SELECT rowid FROM FileSearch WHERE FileSearch MATCH $match)
                OR ($contents = 1 AND f.Id IN (SELECT rowid FROM ContentSearch WHERE ContentSearch MATCH $match)))
              AND ($extension = '' OR lower(f.Extension) = lower($extension))
              AND ($minimumSize IS NULL OR f.SizeBytes >= $minimumSize)
              AND ($maximumSize IS NULL OR f.SizeBytes <= $maximumSize)
              AND ($modifiedAfter IS NULL OR f.ModifiedUtcTicks >= $modifiedAfter)
            ORDER BY f.Name COLLATE NOCASE
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$match", BuildMatchQuery(options.SearchText));
        command.Parameters.AddWithValue("$contents", options.SearchContents ? 1 : 0);
        command.Parameters.AddWithValue("$extension", NormalizeExtension(options.Extension));
        command.Parameters.AddWithValue("$minimumSize", (object?)options.MinimumSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$maximumSize", (object?)options.MaximumSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$modifiedAfter",
            options.ModifiedAfter is null
                ? DBNull.Value
                : options.ModifiedAfter.Value.ToUniversalTime().Ticks);
        command.Parameters.AddWithValue("$limit", ResultLimit);

        var results = new List<FileSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FileSearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                new DateTime(reader.GetInt64(5), DateTimeKind.Utc)));
        }

        return results;
    }

    private static string BuildMatchQuery(string searchText)
    {
        var terms = Regex.Matches(searchText ?? string.Empty, @"[\p{L}\p{N}_]+")
            .Select(match => $"\"{match.Value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*");
        return string.Join(" AND ", terms);
    }

    private static string NormalizeExtension(string? extension)
    {
        var normalized = extension?.Trim() ?? string.Empty;
        return normalized.StartsWith('.') ? normalized : normalized.Length == 0 ? string.Empty : $".{normalized}";
    }
}