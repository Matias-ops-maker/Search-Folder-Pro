using System.Security;
using Microsoft.Data.Sqlite;
using SearchFolderPro.Core.Models;

namespace SearchFolderPro.Core.Services;

public sealed class FileIndexer
{
    private const int BatchSize = 256;
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
            var databaseDirectory = Path.GetDirectoryName(_databasePath)
                ?? throw new InvalidOperationException("La ruta del índice debe incluir una carpeta.");
            Directory.CreateDirectory(databaseDirectory);

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaAsync(connection, cancellationToken);

            var scanId = Guid.NewGuid().ToString("N");
            var pendingFiles = new List<IndexedFile>(BatchSize);
            var directories = new Queue<string>();
            directories.Enqueue(normalizedRoot);
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
                                Path.GetFileName(entryPath),
                                Path.GetDirectoryName(entryPath) ?? currentDirectory,
                                Path.GetExtension(entryPath),
                                fileInfo.Length,
                                fileInfo.LastWriteTimeUtc.Ticks));

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
                await FinalizeScanAsync(connection, normalizedRoot, scanId, filesIndexed, cancellationToken);
            }

            progress?.Report(new IndexingProgress(normalizedRoot, normalizedRoot, filesIndexed, failures));
            return new IndexingResult(normalizedRoot, filesIndexed, failures, isComplete);
        }
        finally
        {
            _indexGate.Release();
        }
    }

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
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearch USING fts5(
                Name,
                DirectoryPath,
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
        }

        transaction.Commit();
    }

    private static async Task FinalizeScanAsync(
        SqliteConnection connection,
        string rootPath,
        string scanId,
        long filesIndexed,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM FileSearch
            WHERE rowid IN (
                SELECT Id FROM Files WHERE RootPath = $root AND LastSeenScanId <> $scan
            );
            DELETE FROM Files WHERE RootPath = $root AND LastSeenScanId <> $scan;
            """;
        command.Parameters.AddWithValue("$root", rootPath);
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
        rootCommand.Parameters.AddWithValue("$count", filesIndexed);

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
        long ModifiedUtcTicks);
}