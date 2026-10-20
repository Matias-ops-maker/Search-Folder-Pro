using System.Security;
using Microsoft.Data.Sqlite;
using SearchFolderPro.Core.Models;

namespace SearchFolderPro.Core.Services;

public sealed class FileIndexer
{
    private const int BatchSize = 256;
    private const long MaximumTextFileBytes = 1024 * 1024;
    private const int MaximumIndexedTextCharacters = 256 * 1024;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm", ".yml", ".yaml",
        ".ini", ".cfg", ".cs", ".gd", ".py", ".js", ".ts", ".css", ".sql"
    };
    private readonly string _databasePath;
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public FileIndexer(string? databasePath = null)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SearchFolderPro",
            "index.db");

        _databasePath = Path.GetFullPath(databasePath ?? defaultPath);
    }

    public async Task<IndexingResult> IndexRootAsync(
        string rootPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"La carpeta seleccionada no existe: {normalizedRoot}");
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            return await IndexRootCoreAsync(normalizedRoot, normalizedRoot, progress, cancellationToken);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<IndexingResult> IndexDirectoryAsync(
        string rootPath,
        string directoryPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        EnsurePathBelongsToRoot(normalizedRoot, normalizedDirectory);
        if (!Directory.Exists(normalizedDirectory))
        {
            throw new DirectoryNotFoundException($"La carpeta seleccionada no existe: {normalizedDirectory}");
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            return await IndexRootCoreAsync(normalizedRoot, normalizedDirectory, progress, cancellationToken);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task IndexFileAsync(
        string rootPath,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedPath = Path.GetFullPath(filePath);
        EnsurePathBelongsToRoot(normalizedRoot, normalizedPath);

        if (!File.Exists(normalizedPath))
        {
            await RemovePathAsync(normalizedRoot, normalizedPath, cancellationToken);
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            var attributes = File.GetAttributes(normalizedPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return;
            }

            await using var connection = await OpenDatabaseAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);
            var fileInfo = new FileInfo(normalizedPath);
            var file = new IndexedFile(
                normalizedPath,
                fileInfo.Name,
                fileInfo.DirectoryName ?? normalizedRoot,
                fileInfo.Extension,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                await ReadTextContentAsync(normalizedPath, fileInfo.Length, cancellationToken));
            await PersistBatchAsync(connection, normalizedRoot, Guid.NewGuid().ToString("N"), [file], cancellationToken);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task RemovePathAsync(
        string rootPath,
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsurePathBelongsToRoot(normalizedRoot, normalizedPath);
        if (!File.Exists(_databasePath))
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenDatabaseAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "FileSearch", "ContentSearch" })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    DELETE FROM {table}
                    WHERE rowid IN (
                        SELECT Id FROM Files
                        WHERE RootPath = $root
                          AND (Path = $path OR substr(Path, 1, length($prefix)) = $prefix)
                    );
                    """;
                command.Parameters.AddWithValue("$root", normalizedRoot);
                command.Parameters.AddWithValue("$path", normalizedPath);
                command.Parameters.AddWithValue("$prefix", GetPathPrefix(normalizedPath));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var removeFiles = connection.CreateCommand();
            removeFiles.Transaction = transaction;
            removeFiles.CommandText = """
                DELETE FROM Files
                WHERE RootPath = $root
                  AND (Path = $path OR substr(Path, 1, length($prefix)) = $prefix);
                """;
            removeFiles.Parameters.AddWithValue("$root", normalizedRoot);
            removeFiles.Parameters.AddWithValue("$path", normalizedPath);
            removeFiles.Parameters.AddWithValue("$prefix", GetPathPrefix(normalizedPath));
            await removeFiles.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task RemoveRootAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!File.Exists(_databasePath))
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenDatabaseAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            foreach (var table in new[] { "FileSearch", "ContentSearch" })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DELETE FROM {table} WHERE rowid IN (SELECT Id FROM Files WHERE RootPath = $root);";
                command.Parameters.AddWithValue("$root", normalizedRoot);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var filesCommand = connection.CreateCommand();
            filesCommand.Transaction = transaction;
            filesCommand.CommandText = "DELETE FROM Files WHERE RootPath = $root; DELETE FROM IndexedRoots WHERE RootPath = $root;";
            filesCommand.Parameters.AddWithValue("$root", normalizedRoot);
            await filesCommand.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<IndexingResult> IndexRootCoreAsync(
        string normalizedRoot,
        string scanPath,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenDatabaseAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var scanId = Guid.NewGuid().ToString("N");
        var pendingFiles = new List<IndexedFile>(BatchSize);
        var directories = new Queue<string>();
        directories.Enqueue(scanPath);
        long filesIndexed = 0;
        long failures = 0;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = directories.Dequeue();
            progress?.Report(new IndexingProgress(normalizedRoot, currentDirectory, filesIndexed, failures));

            try
            {
                foreach (var entryPath in Directory.EnumerateFileSystemEntries(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entryPath);
                    }
                    catch (Exception exception) when (IsFilesystemError(exception))
                    {
                        failures++;
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Enqueue(entryPath);
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(entryPath);
                        pendingFiles.Add(new IndexedFile(
                            entryPath,
                            fileInfo.Name,
                            fileInfo.DirectoryName ?? currentDirectory,
                            fileInfo.Extension,
                            fileInfo.Length,
                            fileInfo.LastWriteTimeUtc.Ticks,
                            await ReadTextContentAsync(entryPath, fileInfo.Length, cancellationToken)));

                        if (pendingFiles.Count >= BatchSize)
                        {
                            await PersistBatchAsync(connection, normalizedRoot, scanId, pendingFiles, cancellationToken);
                            filesIndexed += pendingFiles.Count;
                            pendingFiles.Clear();
                            progress?.Report(new IndexingProgress(normalizedRoot, currentDirectory, filesIndexed, failures));
                        }
                    }
                    catch (Exception exception) when (IsFilesystemError(exception))
                    {
                        failures++;
                    }
                }
            }
            catch (Exception exception) when (IsFilesystemError(exception))
            {
                failures++;
            }
        }

        if (pendingFiles.Count > 0)
        {
            await PersistBatchAsync(connection, normalizedRoot, scanId, pendingFiles, cancellationToken);
            filesIndexed += pendingFiles.Count;
        }

        var isComplete = failures == 0;
        if (isComplete)
        {
            await FinalizeScanAsync(connection, normalizedRoot, scanPath, scanId, cancellationToken);
        }

        progress?.Report(new IndexingProgress(normalizedRoot, normalizedRoot, filesIndexed, failures));
        return new IndexingResult(normalizedRoot, filesIndexed, failures, isComplete);
    }

    private async Task<SqliteConnection> OpenDatabaseAsync(CancellationToken cancellationToken)
    {
        var databaseDirectory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("La ruta del índice debe incluir una carpeta.");
        Directory.CreateDirectory(databaseDirectory);
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<string> ReadTextContentAsync(
        string path,
        long fileSize,
        CancellationToken cancellationToken)
    {
        if (fileSize > MaximumTextFileBytes || !TextExtensions.Contains(Path.GetExtension(path)))
        {
            return string.Empty;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var builder = new System.Text.StringBuilder();
        var buffer = new char[8192];
        while (builder.Length < MaximumIndexedTextCharacters)
        {
            var charsToRead = Math.Min(buffer.Length, MaximumIndexedTextCharacters - builder.Length);
            var count = await reader.ReadAsync(buffer.AsMemory(0, charsToRead), cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (buffer.AsSpan(0, count).Contains('\0'))
            {
                return string.Empty;
            }

            builder.Append(buffer, 0, count);
        }

        return builder.ToString();
    }

    private static void EnsurePathBelongsToRoot(string rootPath, string path)
    {
        if (!path.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(GetPathPrefix(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("La ruta está fuera de la carpeta seleccionada.", nameof(path));
        }
    }

    private static string GetPathPrefix(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY,
                Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                RootPath TEXT NOT NULL COLLATE NOCASE,
                Name TEXT NOT NULL,
                DirectoryPath TEXT NOT NULL,
                Extension TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                ModifiedUtcTicks INTEGER NOT NULL,
                LastSeenScanId TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Files_RootPath ON Files(RootPath);
            CREATE INDEX IF NOT EXISTS IX_Files_Extension ON Files(Extension);
            CREATE INDEX IF NOT EXISTS IX_Files_SizeBytes ON Files(SizeBytes);
            CREATE INDEX IF NOT EXISTS IX_Files_ModifiedUtcTicks ON Files(ModifiedUtcTicks);
            CREATE TABLE IF NOT EXISTS IndexedRoots (
                RootPath TEXT PRIMARY KEY COLLATE NOCASE,
                LastCompletedScanUtc TEXT NOT NULL,
                FileCount INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearch USING fts5(
                Name,
                DirectoryPath,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS ContentSearch USING fts5(
                Content,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PersistBatchAsync(
        SqliteConnection connection,
        string rootPath,
        string scanId,
        IReadOnlyCollection<IndexedFile> files,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO Files (Path, RootPath, Name, DirectoryPath, Extension, SizeBytes, ModifiedUtcTicks, LastSeenScanId)
            VALUES ($path, $root, $name, $directory, $extension, $size, $modified, $scan)
            ON CONFLICT(Path) DO UPDATE SET
                RootPath = excluded.RootPath,
                Name = excluded.Name,
                DirectoryPath = excluded.DirectoryPath,
                Extension = excluded.Extension,
                SizeBytes = excluded.SizeBytes,
                ModifiedUtcTicks = excluded.ModifiedUtcTicks,
                LastSeenScanId = excluded.LastSeenScanId
            RETURNING Id;
            """;
        var pathParameter = upsert.Parameters.Add("$path", SqliteType.Text);
        var rootParameter = upsert.Parameters.Add("$root", SqliteType.Text);
        var nameParameter = upsert.Parameters.Add("$name", SqliteType.Text);
        var directoryParameter = upsert.Parameters.Add("$directory", SqliteType.Text);
        var extensionParameter = upsert.Parameters.Add("$extension", SqliteType.Text);
        var sizeParameter = upsert.Parameters.Add("$size", SqliteType.Integer);
        var modifiedParameter = upsert.Parameters.Add("$modified", SqliteType.Integer);
        var scanParameter = upsert.Parameters.Add("$scan", SqliteType.Text);

        await using var removeSearchEntry = connection.CreateCommand();
        removeSearchEntry.Transaction = transaction;
        removeSearchEntry.CommandText = "DELETE FROM FileSearch WHERE rowid = $id;";
        var removeIdParameter = removeSearchEntry.Parameters.Add("$id", SqliteType.Integer);

        await using var addSearchEntry = connection.CreateCommand();
        addSearchEntry.Transaction = transaction;
        addSearchEntry.CommandText = "INSERT INTO FileSearch (rowid, Name, DirectoryPath) VALUES ($id, $name, $directory);";
        var addIdParameter = addSearchEntry.Parameters.Add("$id", SqliteType.Integer);
        var addNameParameter = addSearchEntry.Parameters.Add("$name", SqliteType.Text);
        var addDirectoryParameter = addSearchEntry.Parameters.Add("$directory", SqliteType.Text);

        await using var removeContentEntry = connection.CreateCommand();
        removeContentEntry.Transaction = transaction;
        removeContentEntry.CommandText = "DELETE FROM ContentSearch WHERE rowid = $id;";
        var removeContentIdParameter = removeContentEntry.Parameters.Add("$id", SqliteType.Integer);

        await using var addContentEntry = connection.CreateCommand();
        addContentEntry.Transaction = transaction;
        addContentEntry.CommandText = "INSERT INTO ContentSearch (rowid, Content) VALUES ($id, $content);";
        var addContentIdParameter = addContentEntry.Parameters.Add("$id", SqliteType.Integer);
        var addContentParameter = addContentEntry.Parameters.Add("$content", SqliteType.Text);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathParameter.Value = file.Path;
            rootParameter.Value = rootPath;
            nameParameter.Value = file.Name;
            directoryParameter.Value = file.DirectoryPath;
            extensionParameter.Value = file.Extension;
            sizeParameter.Value = file.SizeBytes;
            modifiedParameter.Value = file.ModifiedUtcTicks;
            scanParameter.Value = scanId;

            var idValue = await upsert.ExecuteScalarAsync(cancellationToken);
            var id = Convert.ToInt64(idValue, System.Globalization.CultureInfo.InvariantCulture);

            removeIdParameter.Value = id;
            await removeSearchEntry.ExecuteNonQueryAsync(cancellationToken);

            addIdParameter.Value = id;
            addNameParameter.Value = file.Name;
            addDirectoryParameter.Value = file.DirectoryPath;
            await addSearchEntry.ExecuteNonQueryAsync(cancellationToken);

            removeContentIdParameter.Value = id;
            await removeContentEntry.ExecuteNonQueryAsync(cancellationToken);
            addContentIdParameter.Value = id;
            addContentParameter.Value = file.Content;
            await addContentEntry.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task FinalizeScanAsync(
        SqliteConnection connection,
        string rootPath,
        string scanPath,
        string scanId,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM FileSearch
            WHERE rowid IN (
                                SELECT Id FROM Files
                                WHERE RootPath = $root AND LastSeenScanId <> $scan
                                    AND (Path = $scope OR substr(Path, 1, length($prefix)) = $prefix)
                        );
                        DELETE FROM ContentSearch
                        WHERE rowid IN (
                                SELECT Id FROM Files
                                WHERE RootPath = $root AND LastSeenScanId <> $scan
                                    AND (Path = $scope OR substr(Path, 1, length($prefix)) = $prefix)
            );
                        DELETE FROM Files
                        WHERE RootPath = $root AND LastSeenScanId <> $scan
                            AND (Path = $scope OR substr(Path, 1, length($prefix)) = $prefix);
            """;
        command.Parameters.AddWithValue("$root", rootPath);
                command.Parameters.AddWithValue("$scope", scanPath);
                command.Parameters.AddWithValue("$prefix", GetPathPrefix(scanPath));
        command.Parameters.AddWithValue("$scan", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var rootCommand = connection.CreateCommand();
        rootCommand.Transaction = transaction;
        rootCommand.CommandText = """
            INSERT INTO IndexedRoots (RootPath, LastCompletedScanUtc, FileCount)
            VALUES ($root, $scanned, $count)
            ON CONFLICT(RootPath) DO UPDATE SET
                LastCompletedScanUtc = excluded.LastCompletedScanUtc,
                FileCount = excluded.FileCount;
            """;
        rootCommand.Parameters.AddWithValue("$root", rootPath);
        rootCommand.Parameters.AddWithValue("$scanned", DateTimeOffset.UtcNow.ToString("O"));
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM Files WHERE RootPath = $root;";
        countCommand.Parameters.AddWithValue("$root", rootPath);
        var fileCount = await countCommand.ExecuteScalarAsync(cancellationToken);
        rootCommand.Parameters.AddWithValue("$count", fileCount ?? 0L);

        await using var createRoots = connection.CreateCommand();
        createRoots.Transaction = transaction;
        createRoots.CommandText = """
            CREATE TABLE IF NOT EXISTS IndexedRoots (
                RootPath TEXT PRIMARY KEY COLLATE NOCASE,
                LastCompletedScanUtc TEXT NOT NULL,
                FileCount INTEGER NOT NULL
            );
            """;
        await createRoots.ExecuteNonQueryAsync(cancellationToken);
        await rootCommand.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    private static bool IsFilesystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private sealed record IndexedFile(
        string Path,
        string Name,
        string DirectoryPath,
        string Extension,
        long SizeBytes,
        long ModifiedUtcTicks,
        string Content);
}