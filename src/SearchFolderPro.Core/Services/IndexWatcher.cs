using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SearchFolderPro.Core.Services;

public sealed class IndexWatcher : IDisposable
{
    private readonly string _rootPath;
    private readonly FileIndexer _indexer;
    private readonly IProgress<string>? _status;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _fullRescanRequested;

    public IndexWatcher(string rootPath, FileIndexer indexer, IProgress<string>? status = null)
    {
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _indexer = indexer;
        _status = status;

        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 32 * 1024,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _worker = ProcessChangesAsync(_stop.Token);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _stop.Cancel();
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stop.Dispose();
            _signal.Dispose();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => QueuePath(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        QueuePath(eventArgs.OldFullPath);
        QueuePath(eventArgs.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs eventArgs)
    {
        Interlocked.Exchange(ref _fullRescanRequested, 1);
        SignalWorker();
    }

    private void QueuePath(string path)
    {
        _pendingPaths.TryAdd(path, 0);
        SignalWorker();
    }

    private void SignalWorker()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitForSignalAsync(cancellationToken))
            {
                await Task.Delay(350, cancellationToken);
                while (_signal.Wait(0))
                {
                }

                if (Interlocked.Exchange(ref _fullRescanRequested, 0) == 1)
                {
                    _pendingPaths.Clear();
                    _status?.Report("Actualizando el índice completo...");
                    await _indexer.IndexRootAsync(_rootPath, cancellationToken: cancellationToken);
                    continue;
                }

                var paths = _pendingPaths.Keys.ToArray();
                foreach (var path in paths)
                {
                    _pendingPaths.TryRemove(path, out _);
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (Directory.Exists(path))
                        {
                            await _indexer.IndexDirectoryAsync(_rootPath, path, cancellationToken: cancellationToken);
                        }
                        else if (File.Exists(path))
                        {
                            await _indexer.IndexFileAsync(_rootPath, path, cancellationToken);
                        }
                        else
                        {
                            await _indexer.RemovePathAsync(_rootPath, path, cancellationToken);
                        }

                        _status?.Report("Índice actualizado.");
                    }
                    catch (IOException)
                    {
                        _status?.Report("No se pudo actualizar un archivo en uso.");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _status?.Report("No se pudo acceder a un archivo.");
                    }
                    catch (SqliteException)
                    {
                        _status?.Report("No se pudo guardar el cambio en el índice.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SqliteException)
        {
            _status?.Report("No se pudo actualizar el índice.");
        }
        catch (IOException)
        {
            _status?.Report("No se pudo leer una carpeta.");
        }
        catch (UnauthorizedAccessException)
        {
            _status?.Report("No hay permiso para leer una carpeta.");
        }
    }

    private async Task<bool> WaitForSignalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}