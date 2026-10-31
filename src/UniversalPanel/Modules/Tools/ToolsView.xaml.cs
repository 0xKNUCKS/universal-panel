using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using UniversalPanel.Diagnostics;
using UniversalPanel.Modules.Processes;
using UniversalPanel.Native;
using UniversalPanel.Shell;
using Wpf.Ui.Controls;

namespace UniversalPanel.Modules.Tools;

// Launcher: tiles sorted by name, optionally in collapsible sections by category (a tool can be in
// several). Search + category chips filter; Ctrl/Shift select several for open / remove / sections.
// Double-click opens with the user's normal rights (even when the panel is elevated); "Run as
// administrator" is per click and confirms first when no UAC prompt would appear.
internal sealed partial class ToolsView : UserControl
{
    private const string AllChip = "All";

    private readonly IModuleHost _host;
    private readonly List<ToolRow> _rows = [];
    private bool _inspected;
    private string? _category;
    private bool _fillingChips;

    public ToolsView(IModuleHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var entry in Settings.Tools)
        {
            _rows.Add(new ToolRow(entry.Clone()));
        }

        UpdateSectionsToggle();
        Rebuild();
    }

    private static PanelSettings Settings => PanelSettings.Current;

    /// <summary>Re-reads identities and icons (tools get updated or moved while we're hidden).</summary>
    public void Refresh() => _ = Guard.RunAsync("Tools.Inspect", () => InspectAsync(_rows.ToList()));

    private async Task InspectAsync(List<ToolRow> rows)
    {
        if (!_inspected)
        {
            await LoadingRing.ShowAsync(Spinner); // sections come from identities: don't show a wrong layout first
        }

        try
        {
            var results = await Task.Run(() => rows.Select(r => r.Inspect()).ToList());
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Apply(results[i]);
            }
        }
        finally
        {
            _inspected = true;
            LoadingRing.Set(Spinner, false);
        }

        Rebuild();
    }

    // ---- tiles, sections, search, chips ------------------------------------------------------

    /// <summary>Rebuilds tiles from the rows: filtered, sorted by section then name, grouped if on.</summary>
    private void Rebuild()
    {
        var selected = SelectedTools().ToHashSet();
        string query = SearchBox.Text.Trim();
        bool sections = Settings.ToolSections;

        var tiles = new List<TileItem>();
        foreach (var row in _rows.Where(r => r.Matches(query)))
        {
            var categories = row.Categories;
            if (_category is not null && !categories.Contains(_category))
            {
                continue;
            }

            if (!sections)
            {
                tiles.Add(new TileItem(row, null));
                continue;
            }

            foreach (var category in categories.Where(c => _category is null || c == _category))
            {
                tiles.Add(new TileItem(row, category));
            }
        }

        tiles.Sort((a, b) =>
        {
            int bySection = sections ? ToolCategories.Order(a.Section!).CompareTo(ToolCategories.Order(b.Section!)) : 0;
            if (bySection == 0 && sections)
            {
                bySection = string.Compare(a.Section, b.Section, StringComparison.CurrentCultureIgnoreCase);
            }

            return bySection != 0 ? bySection
                : string.Compare(a.Tool.Name, b.Tool.Name, StringComparison.CurrentCultureIgnoreCase) is var byName and not 0 ? byName
                : string.Compare(a.Tool.Path, b.Tool.Path, StringComparison.OrdinalIgnoreCase);
        });

        var view = new ListCollectionView(tiles);
        if (sections)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TileItem.Section)));
        }

        ToolList.ItemsSource = view;
        foreach (var tile in tiles.Where(t => selected.Contains(t.Tool)))
        {
            ToolList.SelectedItems.Add(tile);
        }

        FillChips();
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"All" plus the categories that have tools, in section order.</summary>
    private void FillChips()
    {
        var present = _rows.SelectMany(r => r.Categories).Distinct()
            .OrderBy(ToolCategories.Order).ThenBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (_category is not null && !present.Contains(_category))
        {
            _category = null;
        }

        _fillingChips = true;
        try
        {
            CategoryChips.ItemsSource = new[] { AllChip }.Concat(present).ToList();
            CategoryChips.SelectedItem = _category ?? AllChip;
            CategoryChips.Visibility = present.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _fillingChips = false;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingChips && CategoryChips.SelectedItem is string chip)
        {
            _category = chip == AllChip ? null : chip;
            Rebuild();
        }
    }

    private void OnSectionsToggle(object sender, RoutedEventArgs e)
    {
        Settings.ToolSections = !Settings.ToolSections;
        Settings.SaveSoon();
        UpdateSectionsToggle();
        Rebuild();
    }

    private void UpdateSectionsToggle()
    {
        SectionsToggle.Appearance = Settings.ToolSections ? ControlAppearance.Primary : ControlAppearance.Secondary;
        SectionsToggle.ToolTip = Settings.ToolSections ? "Showing sections by category (click for one grid)" : "Showing one grid (click for sections by category)";
    }

    private void OnSectionExpanded(object sender, RoutedEventArgs e) => SetSectionCollapsed(sender, false);

    private void OnSectionCollapsed(object sender, RoutedEventArgs e) => SetSectionCollapsed(sender, true);

    private static void SetSectionCollapsed(object sender, bool collapsed)
    {
        if (sender is FrameworkElement { DataContext: CollectionViewGroup { Name: string name } }
            && Settings.ToolSectionsCollapsed.Contains(name) != collapsed)
        {
            if (collapsed)
            {
                Settings.ToolSectionsCollapsed.Add(name);
            }
            else
            {
                Settings.ToolSectionsCollapsed.Remove(name);
            }

            Settings.SaveSoon();
        }
    }

    // ---- selection and launching -------------------------------------------------------------

    /// <summary>Selected tools, once each (a tool shown in two sections can be selected twice).</summary>
    private List<ToolRow> SelectedTools() =>
        ToolList.SelectedItems.OfType<TileItem>().Select(t => t.Tool).Distinct().ToList();

    // One click selects (Ctrl / Shift add to the selection), two open.
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { DataContext: TileItem tile }
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = Guard.RunAsync("Tools.Open", () => OpenAsync(tile.Tool));
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = Guard.RunAsync("Tools.Open", OpenSelectedAsync);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _ = Guard.RunAsync("Tools.Remove", RemoveSelectedAsync);
            e.Handled = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tools = SelectedTools();
        if (tools.Count == 0)
        {
            e.Handled = true; // nothing under the menu
            return;
        }

        bool one = tools.Count == 1;
        OpenItem.Header = one ? "Open" : $"Open {tools.Count}";
        AdminItem.IsEnabled = one;
        EditItem.IsEnabled = one;
        LocationItem.IsEnabled = one;
        CopyItem.Header = one ? "Copy path" : $"Copy {tools.Count} paths";
        RemoveItem.Header = one ? "Remove" : $"Remove {tools.Count}";

        // Checked = every selected tool is in that section.
        SectionsMenu.Items.Clear();
        foreach (var category in ToolCategories.All)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = category,
                IsCheckable = true,
                IsChecked = tools.All(t => t.Categories.Contains(category)),
            };
            item.Click += (_, _) => SetCategory(tools, category, item.IsChecked);
            SectionsMenu.Items.Add(item);
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => _ = Guard.RunAsync("Tools.Open", OpenSelectedAsync);

    private async Task OpenSelectedAsync()
    {
        foreach (var row in SelectedTools())
        {
            await OpenAsync(row);
        }
    }

    private void OnRunAsAdmin(object sender, RoutedEventArgs e)
    {
        if (SelectedTools() is [var row])
        {
            _ = Guard.RunAsync("Tools.RunAsAdmin", () => RunAsAdminAsync(row));
        }
    }

    private async Task OpenAsync(ToolRow row)
    {
        if (!await CanStartAsync(row))
        {
            return;
        }

        try
        {
            ToolLauncher.Open(row.Path);
        }
        catch (ProcessActionException ex)
        {
            _host.ShowNotice($"Couldn't open {row.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task RunAsAdminAsync(ToolRow row)
    {
        if (!await CanStartAsync(row))
        {
            return;
        }

        // An elevated panel gets no UAC prompt, so this dialog is the only check left.
        if (Elevation.IsElevated && !await _host.ConfirmAsync(
                $"Run {row.Name} as administrator?",
                $"{row.Path}\n\n{(string.IsNullOrEmpty(row.Identity.Detail) ? "Not a recognized tool." : row.Identity.Detail)}\n\nThe panel is elevated, so Windows won't ask again.",
                "Run as administrator", destructive: false))
        {
            return;
        }

        try
        {
            using (_host.SuppressAutoHide())
            {
                ToolLauncher.RunAsAdministrator(row.Path);
            }
        }
        catch (ProcessActionException ex)
        {
            _host.ShowNotice($"Couldn't start {row.Name}", ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>The file must still be there; a broken or foreign signature needs a yes first.</summary>
    private async Task<bool> CanStartAsync(ToolRow row)
    {
        row.Apply(await Task.Run(row.Inspect)); // the file may have changed since it was listed
        if (!row.Exists)
        {
            _host.ShowNotice($"{row.Name} isn't there anymore", row.Path, InfoBarSeverity.Warning);
            return false;
        }

        return row.Identity.Trust != ToolTrust.Warning || await _host.ConfirmAsync(
            $"{row.Name} doesn't look official",
            $"{row.Identity.Detail}\n\n{row.Path}",
            "Open anyway", destructive: true);
    }

    // ---- managing the list -------------------------------------------------------------------

    // Add opens a small menu: pick a program yourself, or let the panel find installed ones.
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (AddButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = AddButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>A click on empty space (not a tile, scrollbar or section header) clears the selection.</summary>
    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (sender is ListBox list
            && !IsInside<ListBoxItem>(source)
            && !IsInside<System.Windows.Controls.Primitives.ScrollBar>(source)
            && !IsInside<System.Windows.Controls.Primitives.ButtonBase>(source))
        {
            list.UnselectAll();
        }
    }

    private void OnAddManually(object sender, RoutedEventArgs e)
    {
        if (PickFile("Add a tool", "Programs and scripts (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) is { } path)
        {
            Add([path]);
        }
    }

    private string? PickFile(string title, string filter, string? folder)
    {
        using (_host.SuppressAutoHide())
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, InitialDirectory = folder ?? string.Empty };
            return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
        }
    }

    /// <returns>How many were new.</returns>
    private int Add(IEnumerable<string> paths)
    {
        var added = new List<ToolRow>();
        foreach (var path in paths)
        {
            if (ToolEntry.IsSafePath(path) && !_rows.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new ToolRow(new ToolEntry { Path = path });
                _rows.Add(row);
                added.Add(row);
            }
        }

        if (added.Count > 0)
        {
            Save();
            _ = Guard.RunAsync("Tools.Inspect", () => InspectAsync(added));
        }

        return added.Count;
    }

    private void OnRemove(object sender, RoutedEventArgs e) => _ = Guard.RunAsync("Tools.Remove", RemoveSelectedAsync);

    private async Task RemoveSelectedAsync()
    {
        var tools = SelectedTools();
        if (tools.Count == 0
            || (tools.Count > 1 && !await _host.ConfirmAsync(
                $"Remove {tools.Count} tools?",
                string.Join("\n", tools.Select(t => t.Name)) + "\n\nOnly the shortcuts are removed; the programs stay where they are.",
                "Remove", destructive: true)))
        {
            return;
        }

        _rows.RemoveAll(tools.Contains);
        Save();
        Rebuild();
    }

    private void SetCategory(List<ToolRow> tools, string category, bool include)
    {
        foreach (var tool in tools)
        {
            var categories = tool.Categories.ToList();
            if (include && !categories.Contains(category))
            {
                categories.Add(category);
            }
            else if (!include)
            {
                categories.Remove(category);
            }

            var entry = tool.Entry.Clone();
            entry.Categories = categories.Count > 0 ? categories : [ToolCategories.Other];
            tool.Update(entry);
        }

        Save();
        Rebuild();
    }

    private void OnOpenLocation(object sender, RoutedEventArgs e)
    {
        if (SelectedTools() is [var row])
        {
            ClipboardText.Reveal(_host, row.Path);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var tools = SelectedTools();
        if (tools.Count > 0)
        {
            ClipboardText.Copy(_host, string.Join(Environment.NewLine, tools.Select(t => t.Path)));
        }
    }

    private void Save()
    {
        Settings.Tools = _rows.Select(r => r.Entry.Clone()).ToList();
        Settings.SaveSoon();
    }

    private void ShowPage(Grid page)
    {
        MainPage.Visibility = page == MainPage ? Visibility.Visible : Visibility.Collapsed;
        FindPage.Visibility = page == FindPage ? Visibility.Visible : Visibility.Collapsed;
        EditPage.Visibility = page == EditPage ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- edit --------------------------------------------------------------------------------

    private ToolRow? _editing;
    private string? _editIcon;

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (SelectedTools() is [var row])
        {
            OpenEdit(row);
        }
    }

    private void OpenEdit(ToolRow row)
    {
        _editing = row;
        _editIcon = row.Entry.Icon;
        EditIcon.Source = row.Icon;
        EditIdentity.Text = row.Identity.Version is { } v ? $"{row.Identity.Name} {v}" : row.Identity.Name;
        EditDetail.Text = row.Identity.Detail;
        EditName.Text = row.Name;
        EditName.PlaceholderText = row.Identity.Name;
        EditPath.Text = row.Path;

        EditSections.Children.Clear();
        foreach (var category in ToolCategories.All.Concat(row.Categories).Distinct())
        {
            EditSections.Children.Add(new CheckBox
            {
                Content = category,
                IsChecked = row.Categories.Contains(category),
                Margin = new Thickness(0, 0, 12, 4),
                MinWidth = 0,
            });
        }

        ShowPage(EditPage);
    }

    private void OnEditPickIcon(object sender, RoutedEventArgs e)
    {
        if (PickFile("Choose an icon", "Icons and images (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll", Path.GetDirectoryName(EditPath.Text)) is { } path)
        {
            if (ToolRow.LoadIcon(path) is { } icon)
            {
                _editIcon = path;
                EditIcon.Source = icon;
            }
            else
            {
                _host.ShowNotice("That file has no usable icon", path, InfoBarSeverity.Warning);
            }
        }
    }

    private void OnEditResetIcon(object sender, RoutedEventArgs e)
    {
        _editIcon = null;
        EditIcon.Source = ProcessIcons.Get(EditPath.Text, ToolRow.IconSize);
    }

    private void OnEditBrowsePath(object sender, RoutedEventArgs e)
    {
        if (PickFile("Choose the program", "Programs and scripts (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd", Path.GetDirectoryName(EditPath.Text)) is { } path)
        {
            EditPath.Text = path;
        }
    }

    private void OnEditCancel(object sender, RoutedEventArgs e)
    {
        _editing = null;
        ShowPage(MainPage);
    }

    private void OnEditSave(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } row)
        {
            return;
        }

        string path = EditPath.Text.Trim().Trim('"');
        if (!ToolEntry.IsSafePath(path))
        {
            _host.ShowNotice("That isn't a full path", "Use Browse… or paste a path like C:\\Tools\\tool.exe.", InfoBarSeverity.Warning);
            return;
        }

        if (_rows.Any(r => r != row && r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            _host.ShowNotice("That program is already in the list", path, InfoBarSeverity.Warning);
            return;
        }

        var checkedSections = EditSections.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        string name = EditName.Text.Trim();
        var entry = new ToolEntry
        {
            Path = path,
            Name = name.Length == 0 || name == row.Identity.Name ? null : name,
            Icon = _editIcon,
            // Same as the defaults (or none): keep following the catalog.
            Categories = checkedSections.Count == 0 || checkedSections.SequenceEqual(ToolCatalog.DefaultCategories(row.Identity.Known)) ? null : checkedSections,
        };
        entry.Sanitize();

        bool reinspect = !path.Equals(row.Path, StringComparison.OrdinalIgnoreCase) || entry.Icon != row.Entry.Icon;
        row.Update(entry);
        Save();
        _editing = null;
        ShowPage(MainPage);
        if (!File.Exists(path))
        {
            _host.ShowNotice("Saved, but that file doesn't exist", path, InfoBarSeverity.Warning);
        }

        if (reinspect)
        {
            _ = Guard.RunAsync("Tools.Inspect", () => InspectAsync([row]));
        }
        else
        {
            Rebuild();
        }
    }

    // ---- find installed: scan, then pick what to add ------------------------------------------

    private readonly List<FoundTool> _found = [];
    private readonly ObservableCollection<FoundTool> _foundShown = [];
    private string? _findCategory;

    // The finder opens first, empty, with its own spinner; results fill in when the scan is done.
    private async void OnFindClick(object sender, RoutedEventArgs e)
    {
        _found.Clear();
        _foundShown.Clear();
        _findCategory = null;
        FindSearchBox.Text = string.Empty;
        FindChips.ItemsSource = null;
        FindTitle.Text = "Looking for installed tools…";
        FoundList.ItemsSource = _foundShown;
        UpdateAddFound();
        ShowPage(FindPage);
        await LoadingRing.ShowAsync(FindSpinner);
        try
        {
            await Guard.RunAsync("Tools.Find", async () =>
            {
                var paths = await Task.Run(() => ToolFinder.Find(CancellationToken.None));
                if (FindPage.Visibility != Visibility.Visible)
                {
                    return; // closed while scanning
                }

                if (paths.Count == 0)
                {
                    FindTitle.Text = "No known tools found. Use Add a program… for anything else.";
                    return;
                }

                var rows = paths.Select(p => new ToolRow(new ToolEntry { Path = p })).ToList();
                var results = await Task.Run(() => rows.Select(r => r.Inspect()).ToList());
                _found.Clear();
                for (int i = 0; i < rows.Count; i++)
                {
                    rows[i].Apply(results[i]);
                    bool listed = _rows.Any(r => r.Path.Equals(rows[i].Path, StringComparison.OrdinalIgnoreCase));
                    var found = new FoundTool(rows[i], listed);
                    found.PropertyChanged += (_, _) => UpdateAddFound();
                    _found.Add(found);
                }

                int fresh = _found.Count(f => f.CanAdd);
                FindTitle.Text = $"Found {paths.Count} tool{(paths.Count == 1 ? string.Empty : "s")}, {fresh} not in your list";
                FindChips.ItemsSource = new[] { AllChip }.Concat(_found.SelectMany(f => f.Tool.Categories).Distinct().OrderBy(ToolCategories.Order)).ToList();
                FindChips.SelectedItem = AllChip;
                ShowFound();
                UpdateAddFound();
            });
        }
        finally
        {
            LoadingRing.Set(FindSpinner, false);
        }
    }

    private void ShowFound()
    {
        string query = FindSearchBox.Text.Trim();
        _foundShown.Clear();
        foreach (var found in _found
            .Where(f => f.Tool.Matches(query) && (_findCategory is null || f.Tool.Categories.Contains(_findCategory)))
            .OrderBy(f => f.Tool.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _foundShown.Add(found);
        }
    }

    private void OnFindSearchChanged(object sender, TextChangedEventArgs e) => ShowFound();

    private void OnFindCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindChips.SelectedItem is string chip)
        {
            _findCategory = chip == AllChip ? null : chip;
            ShowFound();
        }
    }

    private void OnFoundClick(object sender, MouseButtonEventArgs e)
    {
        // A click anywhere on the row toggles it; the checkbox handles its own clicks.
        if (sender is ListBoxItem { DataContext: FoundTool found } && !IsInside<CheckBox>(e.OriginalSource as DependencyObject))
        {
            found.IsChecked = !found.IsChecked;
        }
    }

    private static bool IsInside<T>(DependencyObject? element)
        where T : DependencyObject
    {
        for (; element is not null; element = System.Windows.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is T)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateAddFound()
    {
        int count = _found.Count(f => f.IsChecked);
        AddFoundButton.Content = count > 0 ? $"Add {count}" : "Add";
        AddFoundButton.IsEnabled = count > 0;
    }

    private void OnAddFound(object sender, RoutedEventArgs e)
    {
        int added = Add(_found.Where(f => f.IsChecked).Select(f => f.Tool.Path));
        CloseFind();
        _host.ShowNotice($"Added {added} tool{(added == 1 ? string.Empty : "s")}", string.Empty, InfoBarSeverity.Success);
    }

    private void OnFindBack(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        ShowPage(MainPage);
        _found.Clear();
        _foundShown.Clear();
    }
}
