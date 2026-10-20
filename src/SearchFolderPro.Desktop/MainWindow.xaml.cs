using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using SearchFolderPro.Core.Models;
using SearchFolderPro.Core.Services;

namespace SearchFolderPro.Desktop;

public partial class MainWindow : Window
{
    private readonly string _databasePath;
    private readonly string _rootsFilePath;
    private readonly FileIndexer _indexer;
    private readonly FileSearchService _searchService;
    private readonly ObservableCollection<string> _roots = [];
    private readonly Dictionary<string, IndexWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _searchTimer;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _indexCancellation;

    public MainWindow()
    {
        InitializeComponent();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SearchFolderPro");
        _databasePath = Path.Combine(dataDirectory, "index.db");
        _rootsFilePath = Path.Combine(dataDirectory, "carpetas.json");
        _indexer = new FileIndexer(_databasePath);
        _searchService = new FileSearchService(_databasePath);
        FoldersList.ItemsSource = _roots;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RunSearchAsync();
        };

        LoadRoots();
    }

    private async void ShowSearch_Click(object sender, RoutedEventArgs eventArgs)
    {
        SearchView.Visibility = Visibility.Visible;
        FoldersView.Visibility = Visibility.Collapsed;
        PageTitle.Text = "Buscar archivos";
        PageSubtitle.Text = "Busca por nombre, tipo, tamaño, fecha o texto.";
        SearchNavButton.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(49, 83, 63));
        FoldersNavButton.Background = System.Windows.Media.Brushes.Transparent;
        await RunSearchAsync();
    }

    private void ShowFolders_Click(object sender, RoutedEventArgs eventArgs)
    {
        SearchView.Visibility = Visibility.Collapsed;
        FoldersView.Visibility = Visibility.Visible;
        PageTitle.Text = "Carpetas";
        PageSubtitle.Text = "Elige qué carpetas incluir en tus búsquedas.";
        SearchNavButton.Background = System.Windows.Media.Brushes.Transparent;
        FoldersNavButton.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(49, 83, 63));
    }

    private async void Search_Click(object sender, RoutedEventArgs eventArgs) => await RunSearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            _searchTimer.Stop();
            await RunSearchAsync();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elige una carpeta para incluir en el índice",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        if (_roots.Contains(rootPath, StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Esa carpeta ya está en la lista.";
            return;
        }

        _roots.Add(rootPath);
        StartWatcher(rootPath);
        SaveRoots();
        await IndexRootAsync(rootPath);
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (FoldersList.SelectedItem is not string rootPath)
        {
            StatusText.Text = "Selecciona una carpeta para quitarla.";
            return;
        }

        if (_watchers.Remove(rootPath, out var watcher))
        {
            watcher.Dispose();
        }

        await _indexer.RemoveRootAsync(rootPath);
        _roots.Remove(rootPath);
        SaveRoots();
        StatusText.Text = "Carpeta quitada del índice.";
        await RunSearchAsync();
    }

    private async void IndexFolders_Click(object sender, RoutedEventArgs eventArgs)
    {
        foreach (var rootPath in _roots.ToArray())
        {
            if (Directory.Exists(rootPath))
            {
                await IndexRootAsync(rootPath);
            }
        }

        if (_roots.Count == 0)
        {
            StatusText.Text = "Agrega una carpeta para empezar.";
        }
    }

    private void CancelIndex_Click(object sender, RoutedEventArgs eventArgs) => _indexCancellation?.Cancel();

    private async Task IndexRootAsync(string rootPath)
    {
        if (_indexCancellation is not null)
        {
            StatusText.Text = "Ya hay una actualización en curso.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _indexCancellation = cancellation;
        CancelIndexButton.IsEnabled = true;
        IndexProgress.IsIndeterminate = true;
        IndexProgress.Visibility = Visibility.Visible;

        var progress = new Progress<IndexingProgress>(value =>
        {
            StatusText.Text = $"Revisando {value.CurrentDirectory} · {value.FilesIndexed:N0} archivos";
        });

        try
        {
            var result = await _indexer.IndexRootAsync(rootPath, progress, cancellation.Token);
            StatusText.Text = result.IsComplete
                ? $"Índice actualizado · {result.FilesIndexed:N0} archivos"
                : $"Actualización parcial · {result.FilesIndexed:N0} archivos · {result.Failures:N0} errores";
            await RunSearchAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Actualización cancelada.";
        }
        catch (IOException)
        {
            StatusText.Text = "No se pudo leer la carpeta seleccionada.";
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "No hay permiso para leer esa carpeta.";
        }
        catch (SqliteException)
        {
            StatusText.Text = "No se pudo guardar el índice.";
        }
        finally
        {
            _indexCancellation = null;
            CancelIndexButton.IsEnabled = false;
            IndexProgress.IsIndeterminate = false;
            IndexProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunSearchAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;

        try
        {
            var options = new FileSearchOptions(
                SearchBox.Text,
                ExtensionBox.Text,
                ParseMegabytes(MinimumSizeBox.Text),
                ParseMegabytes(MaximumSizeBox.Text),
                ModifiedAfterPicker.SelectedDate,
                SearchContentsCheckBox.IsChecked == true);

            var results = await _searchService.SearchAsync(options, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            ResultsGrid.ItemsSource = results.Select(result => new SearchResultRow(
                result.Name,
                result.Path,
                result.DirectoryPath,
                string.IsNullOrEmpty(result.Extension) ? "Archivo" : result.Extension,
                FormatSize(result.SizeBytes),
                result.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))).ToArray();
            StatusText.Text = results.Count == 0
                ? "No hay coincidencias. Comprueba que las carpetas estén indexadas."
                : $"{results.Count:N0} resultados";
        }
        catch (SqliteException)
        {
            StatusText.Text = "No se pudo leer el índice. Prueba actualizar las carpetas.";
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void StartWatcher(string rootPath)
    {
        if (_watchers.ContainsKey(rootPath))
        {
            return;
        }

        var status = new Progress<string>(message => StatusText.Text = message);
        _watchers[rootPath] = new IndexWatcher(rootPath, _indexer, status);
    }

    private void LoadRoots()
    {
        if (!File.Exists(_rootsFilePath))
        {
            return;
        }

        try
        {
            var savedRoots = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_rootsFilePath)) ?? [];
            foreach (var rootPath in savedRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _roots.Add(rootPath);
                StartWatcher(rootPath);
            }
        }
        catch (JsonException)
        {
            StatusText.Text = "No se pudo leer la lista de carpetas guardadas.";
        }
        catch (IOException)
        {
            StatusText.Text = "No se pudo leer la configuración local.";
        }
    }

    private void SaveRoots()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_rootsFilePath)!);
        File.WriteAllText(_rootsFilePath, JsonSerializer.Serialize(_roots));
    }

    private static long? ParseMegabytes(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var megabytes)
        && megabytes >= 0
            ? (long)(megabytes * 1024 * 1024)
            : null;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:N1} {units[unit]}";
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (ResultsGrid.SelectedItem is SearchResultRow result && File.Exists(result.Path))
        {
            Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true });
        }
    }

    private void Window_Closed(object? sender, EventArgs eventArgs)
    {
        _searchTimer.Stop();
        _searchCancellation?.Cancel();
        _indexCancellation?.Cancel();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
    }

    private sealed record SearchResultRow(
        string Name,
        string Path,
        string DirectoryPath,
        string Extension,
        string SizeLabel,
        string ModifiedLabel);
}