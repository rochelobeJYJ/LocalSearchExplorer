using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using LocalSearch.App.Updates;
using LocalSearch.App.ViewModels;
using LocalSearch.Core.Content;
using LocalSearch.Core.Data;
using LocalSearch.Core.Defaults;
using LocalSearch.Core.Indexing;
using LocalSearch.Core.Search;
using LocalSearch.Core.Search.Query;

namespace LocalSearch.App;

public partial class MainWindow : Window
{
    private const int WmMouseHWheel = 0x020E;
    private const double HorizontalWheelStep = 72;

    private readonly SqliteIndexStore _store;
    private readonly IndexingService _indexingService;
    private readonly ContentIndexingService _contentIndexingService;
    private readonly SearchService _searchService;
    private readonly GitHubReleaseUpdateService _updateService;
    private readonly Dictionary<long, RootIndexStatusViewModel> _rootStatusesById = [];
    private LocationNodeViewModel? _selectedLocation;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        var databasePath = LocalSearchPaths.GetDefaultDatabasePath();
        _store = new SqliteIndexStore(databasePath);
        _indexingService = new IndexingService(_store, new FolderScanner());
        _contentIndexingService = new ContentIndexingService(_store, CompositeContentExtractor.CreateDefault());
        _searchService = new SearchService(_store);
        _updateService = new GitHubReleaseUpdateService(UpdateSettings.LoadDefault());

        LocationNodes = new ObservableCollection<LocationNodeViewModel>();
        RootStatuses = new ObservableCollection<RootIndexStatusViewModel>();
        Results = new ObservableCollection<SearchResultViewModel>();
        DataContext = this;
        DatabaseText.Text = databasePath;

        PreviewMouseWheel += MainWindow_PreviewMouseWheel;
        Loaded += async (_, _) => await InitializeAsync();
    }

    public ObservableCollection<LocationNodeViewModel> LocationNodes { get; }
    public ObservableCollection<RootIndexStatusViewModel> RootStatuses { get; }
    public ObservableCollection<SearchResultViewModel> Results { get; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private async Task InitializeAsync()
    {
        await RunGuardedAsync(async () =>
        {
            SetStatus("DB 초기화 중");
            await _store.InitializeAsync();

            var startupRoot = GetStartupRootFromArgs();
            if (!string.IsNullOrWhiteSpace(startupRoot) && Directory.Exists(startupRoot))
            {
                await IndexFolderCoreAsync(startupRoot);
                return;
            }

            await LoadLocationsAsync();
            ShowReadyToSearchState();
        });
    }

    private static string? GetStartupRootFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private async Task LoadLocationsAsync()
    {
        var previous = _selectedLocation;
        var statuses = await _store.GetRootIndexStatusesAsync();
        RootStatuses.Clear();
        _rootStatusesById.Clear();
        foreach (var status in statuses.Select(item => new RootIndexStatusViewModel(item)))
        {
            RootStatuses.Add(status);
            _rootStatusesById[status.Id] = status;
        }

        LocationNodes.Clear();
        var allNode = LocationNodeViewModel.AllLocations(RootStatuses.Count);
        LocationNodes.Add(allNode);
        foreach (var status in statuses)
        {
            LocationNodes.Add(LocationNodeViewModel.FromRoot(status));
        }

        _selectedLocation = ResolveSelectionAfterReload(previous, allNode);
        UpdateScopeTexts();
    }

    private LocationNodeViewModel ResolveSelectionAfterReload(LocationNodeViewModel? previous, LocationNodeViewModel allNode)
    {
        if (previous is null || previous.IsAllLocations || string.IsNullOrWhiteSpace(previous.Path))
        {
            return allNode;
        }

        return RootStatuses.Any(root => IsPathWithinRoot(previous.Path, root.Path))
            ? previous
            : allNode;
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "검색할 폴더 선택",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        await IndexFolderAsync(dialog.SelectedPath);
    }

    private async Task IndexFolderAsync(string path)
    {
        await RunGuardedAsync(() => IndexFolderCoreAsync(path));
    }

    private async Task IndexFolderCoreAsync(string path)
    {
        var progress = new Progress<IndexingProgress>(value =>
        {
            SetStatus($"메타데이터 인덱싱 중: {value.IndexedCount:N0}개 | 실패 {value.ErrorCount:N0}개 | {value.CurrentPath}");
        });

        var result = await _indexingService.AddOrRefreshRootAsync(path, progress);
        if (ContentSearchBox.IsChecked == true)
        {
            var contentProgress = new Progress<ContentIndexingProgress>(value =>
            {
                SetStatus($"내용 인덱싱 중: {value.CompletedCount:N0}/{value.TotalCount:N0} | 실패 {value.FailedCount:N0}개 | {value.CurrentPath}");
            });
            var content = await _contentIndexingService.IndexRootContentAsync(result.Root.Id, contentProgress);
            SetStatus($"내용 인덱싱 완료: {content.CompletedCount:N0}개 | 실패 {content.FailedCount:N0}개");
        }

        await LoadLocationsAsync();
        await SearchCoreAsync();
        SetStatus($"인덱싱 완료: {result.IndexedCount:N0}개 | 실패 {result.ErrorCount:N0}개");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var targetRoots = GetTargetRootsForCurrentSelection();
        if (targetRoots.Length == 0)
        {
            SetStatus("다시 스캔할 검색 위치가 없습니다.");
            return;
        }

        foreach (var root in targetRoots)
        {
            await IndexFolderAsync(root.Path);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesInteractiveAsync();
    }

    private async void IndexContentButton_Click(object sender, RoutedEventArgs e)
    {
        var targetRoots = GetTargetRootsForCurrentSelection();
        if (targetRoots.Length == 0)
        {
            SetStatus("내용 인덱싱할 검색 위치가 없습니다.");
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var completed = 0;
            var failed = 0;
            foreach (var root in targetRoots)
            {
                var contentProgress = new Progress<ContentIndexingProgress>(value =>
                {
                    SetStatus($"내용 인덱싱 중: {root.DisplayName} | {value.CompletedCount:N0}/{value.TotalCount:N0} | 실패 {value.FailedCount:N0}개");
                });
                var result = await _contentIndexingService.IndexRootContentAsync(root.Id, contentProgress);
                completed += result.CompletedCount;
                failed += result.FailedCount;
            }

            await LoadLocationsAsync();
            await SearchCoreAsync();
            SetStatus($"내용 인덱싱 완료: {completed:N0}개 | 실패 {failed:N0}개");
        });
    }

    private async void RemoveRootButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLocation is not { IsRoot: true, RootId: { } rootId } ||
            !_rootStatusesById.TryGetValue(rootId, out var selectedRoot))
        {
            System.Windows.MessageBox.Show(
                this,
                "루트 폴더를 선택한 상태에서만 검색 위치를 제거할 수 있습니다.",
                "검색 위치 제거",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"'{selectedRoot.Path}' 검색 위치를 인덱스에서 제거합니다. 원본 파일은 삭제하지 않습니다.",
            "검색 위치 제거",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _store.RemoveRootAsync(rootId);
            _selectedLocation = null;
            await LoadLocationsAsync();
            await SearchCoreAsync();
            SetStatus("선택한 검색 위치를 제거했습니다.");
        });
    }

    private async void DeleteContentIndexButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "문서 내용 텍스트 인덱스와 추출 오류 기록만 삭제합니다. 파일명/경로 인덱스와 원본 파일은 유지됩니다.",
            "내용 인덱스 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _store.DeleteContentIndexesAsync();
            await LoadLocationsAsync();
            await SearchCoreAsync();
            SetStatus("내용 인덱스를 삭제했습니다.");
        });
    }

    private async void DeleteAllIndexButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            "모든 검색 위치, 제외 규칙, 파일 메타데이터, 내용 인덱스를 삭제합니다. 원본 파일은 삭제하지 않습니다.",
            "모든 인덱스 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            await _store.DeleteAllIndexesAsync();
            _selectedLocation = null;
            await LoadLocationsAsync();
            await SearchCoreAsync();
            SetStatus("모든 인덱스를 삭제했습니다.");
        });
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        if (!_updateService.IsConfigured)
        {
            System.Windows.MessageBox.Show(
                this,
                "GitHub 저장소가 아직 설정되지 않았습니다. 배포 단계에서 version.json의 githubRepo 값을 '소유자/저장소' 형식으로 채우면 최신 릴리즈 확인이 활성화됩니다.",
                "업데이트 확인",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunGuardedAsync(async () =>
        {
            SetStatus("업데이트 확인 중");
            var result = await _updateService.CheckLatestReleaseAsync();
            if (result.IsError)
            {
                SetStatus("업데이트 확인 실패");
                System.Windows.MessageBox.Show(
                    this,
                    result.Message,
                    "업데이트 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (result is not { IsAvailable: true, Update: { } update })
            {
                SetStatus("업데이트: 최신 버전");
                System.Windows.MessageBox.Show(
                    this,
                    $"{result.Message}\n현재 버전: {result.CurrentVersion}",
                    "업데이트 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await PromptAndInstallUpdateAsync(update);
        });
    }

    private async Task PromptAndInstallUpdateAsync(UpdateInfo update)
    {
        var publishedAt = update.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "알 수 없음";
        var answer = System.Windows.MessageBox.Show(
            this,
            $"새 버전 {update.LatestVersion}을 찾았습니다.\n\n릴리즈: {update.ReleaseTag}\n게시: {publishedAt}\n설치 파일: {update.AssetName}\n\n지금 내려받아 설치를 시작할까요?",
            "업데이트 가능",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        var progress = new Progress<double>(value =>
        {
            SetStatus($"업데이트 설치 파일 다운로드 중: {value:P0}");
        });
        var installerPath = await _updateService.DownloadInstallerAsync(update, progress);

        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
        System.Windows.Application.Current.Shutdown();
    }

    private async void ScopeOption_Changed(object sender, RoutedEventArgs e)
    {
        if (ScopeBreadcrumbText is null)
        {
            return;
        }

        UpdateScopeTexts();
        if (IsLoaded && !_isBusy)
        {
            await RunSearchAsync();
        }
    }

    private async void LocationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not LocationNodeViewModel node || node.IsPlaceholder)
        {
            return;
        }

        _selectedLocation = node;
        UpdateScopeTexts();
        await RunSearchAsync();
    }

    private async void LocationTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: LocationNodeViewModel node } ||
            node.IsPlaceholder ||
            node.HasLoadedChildren ||
            node.RootId is not { } rootId ||
            string.IsNullOrWhiteSpace(node.Path))
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var children = await _store.GetChildFoldersAsync(rootId, node.Path);
            node.Children.Clear();
            foreach (var child in children)
            {
                node.Children.Add(LocationNodeViewModel.FromFolder(child));
            }

            node.HasLoadedChildren = true;
            SetStatus(children.Count == 0
                ? $"하위 폴더 없음 | 기준: {node.ScopeLabel}"
                : $"하위 폴더 {children.Count:N0}개 표시 | 기준: {node.ScopeLabel}");
        });
    }

    private async Task RunSearchAsync()
    {
        await RunGuardedAsync(SearchCoreAsync);
    }

    private async Task SearchCoreAsync()
    {
        var scopePath = GetCurrentScopePath();
        var includeSubfolders = IncludeSubfoldersBox.IsChecked == true;
        var options = new SearchOptions
        {
            IgnoreWhitespace = IgnoreWhitespaceBox.IsChecked == true,
            IncludeContent = ContentSearchBox.IsChecked == true,
            TypeFilter = GetTypeFilter(),
            ScopePath = scopePath,
            IncludeSubfolders = includeSubfolders,
            Limit = 1000
        };

        IReadOnlyList<SearchResult> results;
        var searchWatch = Stopwatch.StartNew();
        try
        {
            results = await _searchService.SearchAsync(SearchBox.Text, options);
        }
        catch (QueryParseException ex)
        {
            Results.Clear();
            ShowSearchHint($"{ex.Message} 예: 계약서 & !초안, ext:pdf, content:\"계약 금액\"");
            SetStatus($"검색식 오류 | 기준: {GetCurrentScopeLabel()}");
            return;
        }
        catch (ArgumentException ex) when (ex is RegexParseException || ex.Message.Contains("parsing", StringComparison.OrdinalIgnoreCase))
        {
            Results.Clear();
            ShowSearchHint($"정규식 검색식 오류: {ex.Message}");
            SetStatus($"검색식 오류 | 기준: {GetCurrentScopeLabel()}");
            return;
        }
        finally
        {
            searchWatch.Stop();
        }

        ResultsGrid.ItemsSource = null;
        Results.Clear();
        var sequenceNumber = 1;
        foreach (var result in results)
        {
            Results.Add(new SearchResultViewModel(result, sequenceNumber++));
        }
        ResultsGrid.ItemsSource = Results;

        var knownItemCount = GetKnownCurrentScopeItemCount();
        UpdateSearchHint(knownItemCount);
        ScopeStatusText.Text = knownItemCount.HasValue
            ? $"기준 항목 {knownItemCount.Value:N0}개 | {(includeSubfolders ? "하위 폴더 포함" : "현재 폴더만")} | 내용 검색 {(options.IncludeContent ? "사용" : "미사용")}"
            : $"기준: {GetCurrentScopeLabel()} | {(includeSubfolders ? "하위 폴더 포함" : "현재 폴더만")} | 내용 검색 {(options.IncludeContent ? "사용" : "미사용")}";
        SetStatus(knownItemCount.HasValue
            ? $"결과 {Results.Count:N0}개 | 기준 {knownItemCount.Value:N0}개 | 검색 {searchWatch.ElapsedMilliseconds:N0}ms"
            : $"결과 {Results.Count:N0}개 | 기준: {GetCurrentScopeLabel()} | 검색 {searchWatch.ElapsedMilliseconds:N0}ms");
    }

    private ItemTypeFilter GetTypeFilter()
    {
        return TypeFilterBox.SelectedIndex switch
        {
            1 => ItemTypeFilter.FileOnly,
            2 => ItemTypeFilter.FolderOnly,
            _ => ItemTypeFilter.All
        };
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedItem();
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedItem();
    }

    private void OpenParentMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedParent();
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedPaths(copyNameOnly: false);
    }

    private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedPaths(copyNameOnly: true);
    }

    private void RevealMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RevealSelectedInExplorer();
    }

    private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowSelectedProperties();
    }

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RenameSelectedAsync();
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedAsync();
    }

    private async void ExcludeFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ExcludeSelectedFolderAsync();
    }

    private async void RescanFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RescanSelectedRootAsync();
    }

    private void OpenTerminalMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenTerminalForSelected();
    }

    private void ContentSearchBox_Checked(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "내용 검색은 로컬 SQLite 내용 인덱스를 사용합니다. 먼저 왼쪽 인덱스 관리의 '내용 인덱싱'을 실행해야 문서 내부 텍스트가 검색됩니다. 데이터는 외부로 전송되지 않습니다.",
            "내용 검색 안내",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Enter && ResultsGrid.IsKeyboardFocusWithin)
        {
            ShowSelectedProperties();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && ResultsGrid.IsKeyboardFocusWithin)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenSelectedParent();
            }
            else
            {
                OpenSelectedItem();
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && ResultsGrid.IsKeyboardFocusWithin)
        {
            CopySelectedPaths(copyNameOnly: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            RefreshButton_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            await RunSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && ResultsGrid.IsKeyboardFocusWithin)
        {
            await RenameSelectedAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && ResultsGrid.IsKeyboardFocusWithin)
        {
            await DeleteSelectedAsync();
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            return;
        }

        if (ScrollHorizontallyUnderMouse(-e.Delta))
        {
            e.Handled = true;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel)
        {
            return IntPtr.Zero;
        }

        var delta = GetWheelDelta(wParam);
        if (ScrollHorizontallyUnderMouse(delta))
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static int GetWheelDelta(IntPtr wParam)
    {
        return unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
    }

    private bool ScrollHorizontallyUnderMouse(int delta)
    {
        var source = Mouse.DirectlyOver as DependencyObject;
        var viewer = FindVisualParent<ScrollViewer>(source) ?? FindVisualChild<ScrollViewer>(source);
        if (viewer is null && ResultsGrid.IsMouseOver)
        {
            viewer = FindVisualChild<ScrollViewer>(ResultsGrid);
        }
        else if (viewer is null && LocationTree.IsMouseOver)
        {
            viewer = FindVisualChild<ScrollViewer>(LocationTree);
        }

        if (viewer is null || viewer.ScrollableWidth <= 0)
        {
            return false;
        }

        var steps = delta / 120.0;
        if (Math.Abs(steps) < 0.01)
        {
            steps = Math.Sign(delta);
        }

        var nextOffset = Math.Clamp(
            viewer.HorizontalOffset + (steps * HorizontalWheelStep),
            0,
            viewer.ScrollableWidth);
        if (Math.Abs(nextOffset - viewer.HorizontalOffset) < 0.1)
        {
            return false;
        }

        viewer.ScrollToHorizontalOffset(nextOffset);
        return true;
    }

    private void ResultsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(Results);
        if (view is null || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
        {
            return;
        }

        e.Handled = true;
        var newDirection = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        foreach (var column in ResultsGrid.Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = newDirection;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(e.Column.SortMemberPath, newDirection));
    }

    private void OpenSelectedItem()
    {
        if (ResultsGrid.SelectedItem is SearchResultViewModel selected)
        {
            OpenPath(selected.FullPath);
        }
    }

    private void OpenSelectedParent()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        var parent = selected.IsDirectory
            ? Directory.GetParent(selected.FullPath)?.FullName
            : selected.ParentPath;

        if (!string.IsNullOrWhiteSpace(parent))
        {
            OpenPath(parent);
        }
    }

    private void RevealSelectedInExplorer()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        var arguments = selected.IsDirectory
            ? $"\"{selected.FullPath}\""
            : $"/select,\"{selected.FullPath}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = arguments,
            UseShellExecute = true
        });
    }

    private void ShowSelectedProperties()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = selected.FullPath,
            Verb = "properties",
            UseShellExecute = true
        });
    }

    private async Task RenameSelectedAsync()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        var newName = Microsoft.VisualBasic.Interaction.InputBox("새 이름을 입력하세요.", "이름 변경", selected.Name);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, selected.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            var target = Path.Combine(selected.ParentPath, newName);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException("같은 이름의 파일 또는 폴더가 이미 있습니다.");
            }

            if (selected.IsDirectory)
            {
                Directory.Move(selected.FullPath, target);
            }
            else
            {
                File.Move(selected.FullPath, target);
            }

            await RescanRootCoreAsync(selected.RootId);
        });
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = ResultsGrid.SelectedItems.OfType<SearchResultViewModel>().ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"선택한 항목 {selected.Length:N0}개를 휴지통으로 보냅니다.",
            "삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        await RunGuardedAsync(async () =>
        {
            foreach (var item in selected)
            {
                if (item.IsDirectory)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        item.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        item.FullPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }

            foreach (var rootId in selected.Select(item => item.RootId).Distinct())
            {
                await RescanRootCoreAsync(rootId);
            }
        });
    }

    private async Task ExcludeSelectedFolderAsync()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        var folder = selected.IsDirectory ? selected.FullPath : selected.ParentPath;
        await RunGuardedAsync(async () =>
        {
            await _store.AddExclusionAsync(selected.RootId, folder, "사용자 제외");
            await RescanRootCoreAsync(selected.RootId);
            SetStatus($"제외 폴더 추가: {folder}");
        });
    }

    private async Task RescanSelectedRootAsync()
    {
        if (ResultsGrid.SelectedItem is SearchResultViewModel selected)
        {
            await RunGuardedAsync(() => RescanRootCoreAsync(selected.RootId));
        }
    }

    private async Task RescanRootCoreAsync(long rootId)
    {
        if (_rootStatusesById.TryGetValue(rootId, out var root))
        {
            await IndexFolderCoreAsync(root.Path);
        }
    }

    private void OpenTerminalForSelected()
    {
        if (ResultsGrid.SelectedItem is not SearchResultViewModel selected)
        {
            return;
        }

        var folder = selected.IsDirectory ? selected.FullPath : selected.ParentPath;
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoExit -Command Set-Location -LiteralPath \"" + folder.Replace("\"", "`\"", StringComparison.Ordinal) + "\"",
            UseShellExecute = true
        });
    }

    private void CopySelectedPaths(bool copyNameOnly)
    {
        var selected = ResultsGrid.SelectedItems
            .OfType<SearchResultViewModel>()
            .Select(item => copyNameOnly ? item.Name : item.FullPath)
            .ToArray();

        if (selected.Length == 0)
        {
            return;
        }

        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, selected));
        SetStatus(copyNameOnly ? $"파일명 {selected.Length:N0}개 복사됨" : $"경로 {selected.Length:N0}개 복사됨");
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                System.Windows.MessageBox.Show(this, "경로를 찾을 수 없습니다.", "열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "열기 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private RootIndexStatusViewModel[] GetTargetRootsForCurrentSelection()
    {
        if (_selectedLocation?.RootId is long rootId &&
            _rootStatusesById.TryGetValue(rootId, out var selectedRoot))
        {
            return [selectedRoot];
        }

        return RootStatuses.ToArray();
    }

    private string? GetCurrentScopePath()
    {
        return _selectedLocation is { IsAllLocations: false } ? _selectedLocation.Path : null;
    }

    private string GetCurrentScopeLabel()
    {
        return _selectedLocation?.ScopeLabel ?? "모든 검색 위치";
    }

    private RootIndexStatusViewModel? GetSelectedRootStatus()
    {
        return _selectedLocation?.RootId is long rootId && _rootStatusesById.TryGetValue(rootId, out var status)
            ? status
            : null;
    }

    private int? GetKnownCurrentScopeItemCount()
    {
        if (_selectedLocation is null || _selectedLocation.IsAllLocations)
        {
            return RootStatuses.Sum(status => status.ItemCount);
        }

        if (_selectedLocation.IsRoot && _selectedLocation.RootId is long rootId && _rootStatusesById.TryGetValue(rootId, out var status))
        {
            return status.ItemCount;
        }

        return null;
    }

    private void ShowReadyToSearchState()
    {
        Results.Clear();
        ResultsGrid.ItemsSource = Results;

        var knownItemCount = GetKnownCurrentScopeItemCount();
        if (RootStatuses.Count == 0)
        {
            ShowSearchHint("왼쪽의 '폴더 추가'로 검색 위치를 등록하고 인덱싱하세요.");
            SetStatus("검색 위치 없음");
            return;
        }

        if (knownItemCount == 0)
        {
            ShowSearchHint("현재 검색 기준에 인덱싱된 항목이 없습니다. 검색 위치를 다시 스캔하거나 다른 위치를 선택하세요.");
            SetStatus($"준비됨 | 기준 항목 0개");
            return;
        }

        ShowSearchHint("검색어를 입력하고 Enter를 누르세요.");
        SetStatus(knownItemCount.HasValue
            ? $"준비됨 | 기준 {knownItemCount.Value:N0}개"
            : $"준비됨 | 기준: {GetCurrentScopeLabel()}");
    }

    private void UpdateScopeTexts()
    {
        var label = GetCurrentScopeLabel();
        var includeSubfolders = IncludeSubfoldersBox.IsChecked == true;
        ScopeBreadcrumbText.Text = _selectedLocation?.Breadcrumb ?? "검색 위치 > 모든 검색 위치";
        ScopeStatusText.Text = $"기준: {label} | {(includeSubfolders ? "하위 폴더 포함" : "현재 폴더만")}";
        StatusScopeText.Text = $"기준: {label}";
        SelectedLocationText.Text = $"현재 기준: {label}";

        var status = GetSelectedRootStatus();
        SelectedRootIndexStatusText.Text = status is null
            ? $"전체 루트 {RootStatuses.Count:N0}개 | 위치를 선택하면 해당 루트 상태를 봅니다."
            : $"{status.Summary}\n{status.ContentSummary}\n마지막 인덱싱: {status.LastIndexedDisplay}";
        RemoveRootButton.IsEnabled = !_isBusy && _selectedLocation?.IsRoot == true;
    }

    private void UpdateSearchHint(int? knownScopedItemCount)
    {
        if (RootStatuses.Count == 0)
        {
            ShowSearchHint("왼쪽의 '폴더 추가'로 검색 위치를 등록하고 인덱싱하세요.");
            return;
        }

        if (knownScopedItemCount == 0)
        {
            ShowSearchHint("현재 검색 기준에 인덱싱된 항목이 없습니다. 검색 위치를 다시 스캔하거나 다른 위치를 선택하세요.");
            return;
        }

        if (Results.Count == 0)
        {
            ShowSearchHint("현재 검색 기준에서 결과가 없습니다. 상위 폴더나 '모든 검색 위치'로 범위를 넓히거나, 문서 내부 검색은 내용 인덱싱 상태를 확인하세요.");
            return;
        }

        HideSearchHint();
    }

    private void ShowSearchHint(string message)
    {
        SearchHintText.Text = message;
        SearchHintText.Visibility = Visibility.Visible;
    }

    private void HideSearchHint()
    {
        SearchHintText.Text = string.Empty;
        SearchHintText.Visibility = Visibility.Collapsed;
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            ToggleControls(false);
            await action();
        }
        catch (Exception ex)
        {
            SetStatus("오류 발생");
            System.Windows.MessageBox.Show(this, ex.Message, "Local Search Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
            ToggleControls(true);
            UpdateScopeTexts();
        }
    }

    private void ToggleControls(bool enabled)
    {
        AddFolderButton.IsEnabled = enabled;
        DeleteAllIndexButton.IsEnabled = enabled;
        DeleteContentIndexButton.IsEnabled = enabled;
        IndexContentButton.IsEnabled = enabled && RootStatuses.Count > 0;
        LocationTree.IsEnabled = enabled;
        RemoveRootButton.IsEnabled = enabled && _selectedLocation?.IsRoot == true;
        SearchButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        UpdateButton.IsEnabled = enabled;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private static bool IsPathWithinRoot(string path, string rootPath)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(rootPath);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(EnsureTrailingSeparator(normalizedRoot), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : null;
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? source)
        where T : DependencyObject
    {
        if (source is null || source is not Visual and not Visual3D)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(source);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
