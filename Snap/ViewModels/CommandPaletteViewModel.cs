using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Snap.Helpers;
using Snap.Models;

namespace Snap.ViewModels;

public partial class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 50;
    private const int BatchSize = 10;
    private const int MaxRecursionDepth = 10;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private CommandPaletteItem? _selectedItem;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatusText = string.Empty;

    public ObservableCollection<CommandPaletteItem> Results { get; } = new();

    /// <summary>Current path of the active pane, used for file search.</summary>
    public string CurrentDirectory { get; set; } = @"C:\";

    /// <summary>Callback to navigate the active pane to a path.</summary>
    public Func<string, Task>? NavigateAction { get; set; }

    /// <summary>App commands offered in "/" mode (built by MainViewModel).</summary>
    public IReadOnlyList<PaletteCommand> Commands { get; set; } = [];

    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _debounceTimer;
    private string _pendingQuery = string.Empty;

    public CommandPaletteViewModel()
    {
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounceTimer.Tick += (s, e) =>
        {
            _debounceTimer.Stop();
            UpdateResultsAsync(_pendingQuery).SafeFireAndForget("CommandPalette.Search", "検索に失敗しました");
        };
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Close();
        }
        else
        {
            IsVisible = true;
            QueryText = string.Empty;
            Results.Clear();
        }
    }

    public void Close()
    {
        _searchCts?.Cancel();
        IsVisible = false;
        IsSearching = false;
        SearchStatusText = string.Empty;
        QueryText = string.Empty;
        Results.Clear();
    }

    partial void OnQueryTextChanged(string value)
    {
        _debounceTimer.Stop();

        if (string.IsNullOrWhiteSpace(value))
        {
            _searchCts?.Cancel();
            Results.Clear();
            SelectedItem = null;
            IsSearching = false;
            SearchStatusText = string.Empty;
            return;
        }

        var mode = ClassifyInput(value);
        if (mode == InputMode.Command || mode == InputMode.PathNavigate)
        {
            // Immediate for commands and path navigation
            UpdateResultsAsync(value).SafeFireAndForget("CommandPalette.Search", "検索に失敗しました");
        }
        else
        {
            // Debounce for file/content search
            _pendingQuery = value;
            _debounceTimer.Start();
        }
    }

    public void SelectNext()
    {
        if (Results.Count == 0) return;
        var idx = SelectedItem != null ? Results.IndexOf(SelectedItem) : -1;
        idx = (idx + 1) % Results.Count;
        SelectedItem = Results[idx];
    }

    public void SelectPrevious()
    {
        if (Results.Count == 0) return;
        var idx = SelectedItem != null ? Results.IndexOf(SelectedItem) : 0;
        idx = (idx - 1 + Results.Count) % Results.Count;
        SelectedItem = Results[idx];
    }

    public async Task ExecuteSelected()
    {
        var item = SelectedItem ?? (Results.Count > 0 ? Results[0] : null);
        if (item == null) return;

        Close();

        switch (item.Kind)
        {
            case CommandKind.NavigatePath:
                if (NavigateAction != null)
                    await NavigateAction(item.Data);
                break;
            case CommandKind.AppCommand:
                if (item.Command != null)
                    await item.Command.Execute();
                break;
            case CommandKind.FileItem:
                // For files, open with default app; for directories, navigate
                if (Directory.Exists(item.Data))
                {
                    if (NavigateAction != null)
                        await NavigateAction(item.Data);
                }
                else if (File.Exists(item.Data))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.Data,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { Snap.Services.Log.UserError("CommandPalette.Open", $"ファイルを開けません（{item.Data}）", ex); }
                }
                break;
            case CommandKind.GrepResult:
                if (File.Exists(item.Data))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = item.Data,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) { Snap.Services.Log.UserError("CommandPalette.Open", $"ファイルを開けません（{item.Data}）", ex); }
                }
                break;
        }
    }

    // ==================== Highlight helpers ====================

    private static List<HighlightSegment> BuildHighlightSegments(string text, string keyword)
    {
        var segments = new List<HighlightSegment>();
        if (string.IsNullOrEmpty(keyword) || string.IsNullOrEmpty(text))
        {
            segments.Add(new(text ?? "", false));
            return segments;
        }

        int pos = 0;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(keyword, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                segments.Add(new(text[pos..], false));
                break;
            }
            if (idx > pos)
                segments.Add(new(text[pos..idx], false));
            segments.Add(new(text[idx..(idx + keyword.Length)], true));
            pos = idx + keyword.Length;
        }
        return segments;
    }

    // ==================== Input classification ====================

    private enum InputMode
    {
        Command,
        ContentSearch,
        PathNavigate,
        ExtensionFilter,
        FileNameSearch,
    }

    private static InputMode ClassifyInput(string query)
    {
        if (query.StartsWith("/"))
            return InputMode.Command;
        if (query.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            return InputMode.ContentSearch;
        if (IsPathLike(query))
            return InputMode.PathNavigate;
        if (query.Contains("*.") || query.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            return InputMode.ExtensionFilter;
        return InputMode.FileNameSearch;
    }

    private static bool IsPathLike(string query)
    {
        return query.StartsWith(@"\\") ||
               query.StartsWith("~/") ||
               query.StartsWith("~\\") ||
               query == "~" ||
               (query.Length >= 2 && char.IsLetter(query[0]) && query[1] == ':');
    }

    /// <summary>
    /// Parses "*.cs keyword" or "ext:cs keyword" into (keyword, extensions[]).
    /// </summary>
    private static (string keyword, string[] extensions) ParseSearchQuery(string query)
    {
        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var extensions = new List<string>();
        var keywords = new List<string>();

        foreach (var part in parts)
        {
            if (part.StartsWith("*."))
                extensions.Add(part[1..]); // ".cs"
            else if (part.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                extensions.Add("." + part[4..]); // ".cs"
            else
                keywords.Add(part);
        }

        return (string.Join(" ", keywords), extensions.ToArray());
    }

    // ==================== Main update ====================

    private async Task UpdateResultsAsync(string query)
    {
        var oldCts = _searchCts;
        oldCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        oldCts?.Dispose();
        var token = _searchCts.Token;

        Results.Clear();
        SelectedItem = null;
        IsSearching = false;
        SearchStatusText = string.Empty;

        if (string.IsNullOrWhiteSpace(query))
            return;

        var mode = ClassifyInput(query);

        try
        {
            switch (mode)
            {
                case InputMode.Command:
                    AddCommandResults(query[1..].Trim().ToLowerInvariant());
                    break;
                case InputMode.ContentSearch:
                    await SearchFileContentsAsync(query["content:".Length..].Trim(), token);
                    break;
                case InputMode.PathNavigate:
                    AddPathResults(query);
                    break;
                case InputMode.ExtensionFilter:
                case InputMode.FileNameSearch:
                    await SearchRecursiveAsync(query, mode, token);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                Snap.Services.Log.Warn("CommandPalette.Search", query, ex);
                SearchStatusText = $"検索エラー: {ex.Message}";
            }
        }

        // A newer query replaced this one: its state (IsSearching / Results) belongs to it.
        if (token.IsCancellationRequested) return;

        IsSearching = false;
        if (Results.Count > 0 && SelectedItem == null)
            SelectedItem = Results[0];
    }

    // ==================== Command mode ====================

    private void AddCommandResults(string filter)
    {
        foreach (var command in Commands)
        {
            if (string.IsNullOrEmpty(filter) || command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Results.Add(new CommandPaletteItem
                {
                    DisplayText = command.Label,
                    Icon = command.Icon,
                    Kind = CommandKind.AppCommand,
                    Data = command.Name,
                    Command = command,
                    DisplaySegments = new() { new(command.Label, false) },
                });
            }
            if (Results.Count >= 20) break;
        }
    }

    // ==================== Path navigation ====================

    private void AddPathResults(string query)
    {
        var expandedPath = query;

        if (expandedPath == "~" || expandedPath.StartsWith("~/") || expandedPath.StartsWith("~\\"))
        {
            expandedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expandedPath.Length > 1 ? expandedPath[2..] : "");
        }

        try
        {
            if (Directory.Exists(expandedPath))
            {
                var openLabel = $"Open {expandedPath}";
                Results.Add(new CommandPaletteItem
                {
                    DisplayText = openLabel,
                    Icon = "\uE8B7",
                    Kind = CommandKind.NavigatePath,
                    Data = expandedPath,
                    DisplaySegments = new() { new(openLabel, false) },
                });

                foreach (var dir in Directory.EnumerateDirectories(expandedPath).Take(15))
                {
                    var dirName = Path.GetFileName(dir);
                    Results.Add(new CommandPaletteItem
                    {
                        DisplayText = dirName,
                        SecondaryText = dir,
                        Icon = "\uE8B7",
                        Kind = CommandKind.NavigatePath,
                        Data = dir,
                        DisplaySegments = new() { new(dirName, false) },
                        SecondarySegments = new() { new(dir, false) },
                    });
                }
            }
            else
            {
                var parent = Path.GetDirectoryName(expandedPath);
                var partial = Path.GetFileName(expandedPath);

                if (parent != null && Directory.Exists(parent) && !string.IsNullOrEmpty(partial))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(parent)
                        .Where(e => Path.GetFileName(e)
                            .Contains(partial, StringComparison.OrdinalIgnoreCase))
                        .Take(20))
                    {
                        var isDir = Directory.Exists(entry);
                        var entryName = Path.GetFileName(entry);
                        Results.Add(new CommandPaletteItem
                        {
                            DisplayText = entryName,
                            SecondaryText = entry,
                            Icon = isDir ? "\uE8B7" : "\uE8A5",
                            Kind = isDir ? CommandKind.NavigatePath : CommandKind.FileItem,
                            Data = entry,
                            DisplaySegments = BuildHighlightSegments(entryName, partial),
                            SecondarySegments = new() { new(entry, false) },
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Typing a path hits unreadable / half-typed locations routinely: trace only.
            Snap.Services.Log.Warn("CommandPalette.PathResults", query, ex);
        }
    }

    // ==================== Recursive file name search ====================

    private async Task SearchRecursiveAsync(string query, InputMode mode, CancellationToken token)
    {
        if (!Directory.Exists(CurrentDirectory)) return;

        var (keyword, extensions) = mode == InputMode.ExtensionFilter
            ? ParseSearchQuery(query)
            : (query, Array.Empty<string>());

        IsSearching = true;
        SearchStatusText = "検索中...";

        var batch = new List<CommandPaletteItem>();
        var resultCount = 0;
        var baseDir = CurrentDirectory;

        await Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaxRecursionDepth,
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(baseDir, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (resultCount >= MaxResults) break;

                var name = Path.GetFileName(entry);

                // Extension filter
                if (extensions.Length > 0)
                {
                    var ext = Path.GetExtension(entry);
                    if (!extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                        continue;
                }

                // Keyword match
                if (!string.IsNullOrEmpty(keyword) &&
                    !name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isDir = Directory.Exists(entry);
                var relativePath = Path.GetRelativePath(baseDir, entry);

                batch.Add(new CommandPaletteItem
                {
                    DisplayText = name,
                    SecondaryText = relativePath,
                    Icon = isDir ? "\uE8B7" : "\uE8A5",
                    Kind = isDir ? CommandKind.NavigatePath : CommandKind.FileItem,
                    Data = entry,
                    DisplaySegments = BuildHighlightSegments(name, keyword),
                    SecondarySegments = BuildHighlightSegments(relativePath, keyword),
                });
                resultCount++;

                if (batch.Count >= BatchSize)
                {
                    var toAdd = batch.ToList();
                    batch.Clear();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // A newer search may have cancelled this one and cleared Results
                        // between the Count check and the Add; bail before touching Results.
                        if (token.IsCancellationRequested) return;
                        foreach (var item in toAdd)
                            Results.Add(item);
                        if (SelectedItem == null && Results.Count > 0)
                            SelectedItem = Results[0];
                        SearchStatusText = $"検索中... {Results.Count}件";
                    });
                }
            }
        }, token);

        // Superseded while finishing: don't append stale results to the newer search.
        token.ThrowIfCancellationRequested();

        // Flush remaining batch
        if (batch.Count > 0)
        {
            foreach (var item in batch)
                Results.Add(item);
        }

        SearchStatusText = Results.Count >= MaxResults
            ? $"{Results.Count}件（上限に達しました）"
            : Results.Count > 0 ? $"{Results.Count}件" : "該当なし";
    }

    // ==================== File content search (grep) ====================

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".js", ".ts", ".json", ".xml", ".html",
        ".css", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".log",
        ".py", ".java", ".cpp", ".c", ".h", ".sh", ".bat", ".ps1",
        ".csv", ".sql", ".xaml", ".csproj", ".sln", ".gitignore",
        ".jsx", ".tsx", ".vue", ".rb", ".go", ".rs", ".swift",
    };

    private const long MaxFileSizeBytes = 1_000_000; // 1MB

    private async Task SearchFileContentsAsync(string query, CancellationToken token)
    {
        if (!Directory.Exists(CurrentDirectory)) return;

        var (keyword, extensions) = ParseSearchQuery(query);
        if (string.IsNullOrEmpty(keyword)) return;

        var allowedExtensions = extensions.Length > 0
            ? new HashSet<string>(extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase)
            : TextExtensions;

        IsSearching = true;
        SearchStatusText = "ファイル内検索中...";

        var resultCount = 0;
        var skipped = 0;
        Exception? firstSkip = null;
        var baseDir = CurrentDirectory;

        await Task.Run(() =>
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaxRecursionDepth,
            };

            foreach (var filePath in Directory.EnumerateFiles(baseDir, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (resultCount >= MaxResults) break;

                var ext = Path.GetExtension(filePath);
                if (!allowedExtensions.Contains(ext)) continue;

                try
                {
                    var info = new FileInfo(filePath);
                    if (info.Length > MaxFileSizeBytes) continue;

                    using var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true);
                    int lineNumber = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        lineNumber++;
                        if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            var relativePath = Path.GetRelativePath(baseDir, filePath);
                            var trimmedLine = line.Trim();
                            if (trimmedLine.Length > 80)
                                trimmedLine = trimmedLine[..80] + "...";

                            var displayText = $"{relativePath}:{lineNumber}";
                            var item = new CommandPaletteItem
                            {
                                DisplayText = displayText,
                                SecondaryText = trimmedLine,
                                Icon = "\uE8A5",
                                Kind = CommandKind.GrepResult,
                                Data = filePath,
                                DisplaySegments = BuildHighlightSegments(displayText, ""),
                                SecondarySegments = BuildHighlightSegments(trimmedLine, keyword),
                            };

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // A newer search may have cancelled this one and cleared
                                // Results; bail before touching the (possibly empty) list.
                                if (token.IsCancellationRequested) return;
                                Results.Add(item);
                                if (SelectedItem == null && Results.Count > 0)
                                    SelectedItem = Results[0];
                                SearchStatusText = $"ファイル内検索中... {Results.Count}件";
                            });

                            resultCount++;
                            break; // 1ファイル1マッチ
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Locked / unreadable file: skip it, but count it (one log line per search).
                    skipped++;
                    firstSkip ??= ex;
                }
            }
        }, token);

        token.ThrowIfCancellationRequested();

        if (skipped > 0)
            Snap.Services.Log.Warn("CommandPalette.ContentSearch",
                $"{skipped} file(s) unreadable under {baseDir}; first error shown", firstSkip);

        SearchStatusText = (Results.Count >= MaxResults
            ? $"{Results.Count}件（上限に達しました）"
            : Results.Count > 0 ? $"{Results.Count}件" : "該当なし")
            + (skipped > 0 ? $"・読めないファイル {skipped} 件" : "");
    }
}

public enum CommandKind
{
    NavigatePath,
    AppCommand,
    FileItem,
    GrepResult,
}

public class CommandPaletteItem
{
    public string DisplayText { get; set; } = string.Empty;
    public string SecondaryText { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public CommandKind Kind { get; set; }
    public string Data { get; set; } = string.Empty;
    /// <summary>The app command to run (Kind == AppCommand).</summary>
    public PaletteCommand? Command { get; set; }
    public List<HighlightSegment> DisplaySegments { get; set; } = new();
    public List<HighlightSegment> SecondarySegments { get; set; } = new();
}
