using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QueryMaster;
using ServerBrowser.Core;

namespace ServerBrowser.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, (DataGridColumn Column, ServerColumn Default)> builtinColumns = [];
    private DataGridCollectionView? serverView;
    private ObservableCollection<ServerEntry> displayedServers = [];
    private bool changingColumns;
    private bool changingServerSelection;
    private string? sortingColumnId;
    private readonly DispatcherTimer columnSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private void InitializeServerColumns()
    {
        string[] ids = ["Favorite", "BuddyCount", "Country", "Address", "Dedicated", "Private", "Join",
            "Name", "Description", "Tags", "Players", "Humans", "Bots", "Total", "Max", "Map", "Ping", "Status"];
        for (int i = 0; i < ServersGrid.Columns.Count; i++)
        {
            var column = ServersGrid.Columns[i];
            var path = column.SortMemberPath;
            if (string.IsNullOrEmpty(path) && column is DataGridTextColumn { Binding: Binding binding }) path = binding.Path;
            path ??= ids[i] == "Favorite" ? "Favorite" : "Country";
            column.SortMemberPath = path;
            var definition = new ServerColumn
            {
                Id = ids[i], Field = path, Title = column.Header?.ToString() ?? ids[i], Visible = column.IsVisible,
                Width = column.Width.IsAbsolute ? column.Width.Value : 300, Order = i
            };
            builtinColumns[definition.Id] = (column, definition);
        }
        ServersGrid.Sorting += (_, e) => sortingColumnId = (e.Column.Tag as ServerColumn)?.Id;
        ServersGrid.ColumnReordered += (_, _) => QueueColumnSave();
        columnSaveTimer.Tick += (_, _) => { columnSaveTimer.Stop(); SaveSettings(); };
        Closed += (_, _) => columnSaveTimer.Stop();
    }

    private static ServerColumn CopyColumn(ServerColumn source) => new()
    {
        Id = source.Id, Title = source.Title, Field = source.Field, Custom = source.Custom, Numeric = source.Numeric,
        Visible = source.Visible, Width = source.Width, Order = source.Order
    };

    private void RestoreServerColumns()
    {
        if (active is null) return;
        changingColumns = true;
        try
        {
            serverView = null;
            ServersGrid.ItemsSource = null;
            ServersGrid.Columns.Clear();
            foreach (var (_, template) in builtinColumns.Values)
                if (!active.Columns.Any(c => c.Id == template.Id)) active.Columns.Add(CopyColumn(template));
            foreach (var definition in active.Columns.OrderBy(c => c.Order).ToList())
            {
                if (!definition.Custom && !builtinColumns.ContainsKey(definition.Id)) continue;
                var column = definition.Custom
                    ? new DataGridTextColumn { Binding = new Binding($"ColumnValues[{definition.Id}]") { Mode = BindingMode.OneWay }, CanUserSort = true, CustomSortComparer = new ColumnValueComparer(definition) }
                    : builtinColumns[definition.Id].Column;
                column.Tag = definition;
                column.Header = definition.Title;
                column.Width = new DataGridLength(definition.Width, DataGridLengthUnitType.Pixel, definition.Width, definition.Width);
                column.IsVisible = definition.Visible && (definition.Id != "Address" || settings.AddressMode != 0);
                column.HeaderTemplate = new FuncDataTemplate<object>((_, _) => CreateColumnHeader(definition));
                // Reused built-in columns must have only one notification subscription.
                column.PropertyChanged -= ColumnPropertyChanged;
                column.PropertyChanged += ColumnPropertyChanged;
                ServersGrid.Columns.Add(column);
            }
            // Reused built-in columns retain their former DisplayIndex even after Clear().
            // Apply the saved order only after every column has been attached.
            for (int i = 0; i < ServersGrid.Columns.Count; i++) ServersGrid.Columns[i].DisplayIndex = i;
            sortingColumnId = active.SortColumn;
            AutoFitMenuItem.IsChecked = active.AutoFitColumns;
        }
        finally { changingColumns = false; }
    }

    private Control CreateColumnHeader(ServerColumn definition)
    {
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "Hide column" };
        hide.Click += (_, _) => { definition.Visible = false; ApplyColumnVisibility(); SaveSettings(); };
        var chooser = new MenuItem { Header = "Choose Columns..." }; chooser.Click += Columns_Click;
        var add = new MenuItem { Header = "Add Custom Column..." }; add.Click += AddColumn_Click;
        var fit = new MenuItem { Header = "Best Fit" }; fit.Click += (_, _) => BestFitColumns(definition.Id);
        var fitAll = new MenuItem { Header = "Best Fit All Columns" }; fitAll.Click += BestFitAll_Click;
        menu.Items.Add(fit); menu.Items.Add(fitAll); menu.Items.Add(new Separator());
        menu.Items.Add(hide); menu.Items.Add(chooser); menu.Items.Add(add);
        if (definition.Custom)
        {
            var remove = new MenuItem { Header = "Delete custom column" };
            remove.Click += (_, _) =>
            {
                if (active is null) return;
                active.Columns.Remove(definition);
                if (active.SortColumn == definition.Id) active.SortColumn = null;
                RestoreServerColumns(); ApplyFilter(); SaveSettings();
            };
            menu.Items.Add(remove);
        }
        var header = new Border { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch, ClipToBounds = true,
            Child = new TextBlock { Text = definition.Title, TextTrimming = TextTrimming.CharacterEllipsis }, ContextMenu = menu };
        header.DoubleTapped += (_, e) => { BestFitColumns(definition.Id); e.Handled = true; };
        return header;
    }

    private void ColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(DataGridColumn.Width)) QueueColumnSave();
    }
    private void QueueColumnSave()
    {
        if (changingColumns || loading || active is null) return;
        columnSaveTimer.Stop(); columnSaveTimer.Start();
    }
    private void CaptureColumnLayout()
    {
        if (changingColumns || active is null) return;
        foreach (var column in ServersGrid.Columns)
        {
            if (column.Tag is not ServerColumn definition || !active.Columns.Contains(definition)) continue;
            definition.Order = column.DisplayIndex;
            if (column.Width.IsAbsolute) definition.Width = column.Width.Value;
            else if (column.ActualWidth > 0) definition.Width = column.ActualWidth;
        }
    }
    private void ApplyColumnVisibility()
    {
        changingColumns = true;
        try
        {
            foreach (var column in ServersGrid.Columns)
                if (column.Tag is ServerColumn definition)
                    column.IsVisible = definition.Visible && (definition.Id != "Address" || settings.AddressMode != 0);
        }
        finally { changingColumns = false; }
    }

    private void BindServerRows(List<ServerEntry> rows, ServerEntry? selected)
    {
        if (active is null) return;
        var tab = active;
        foreach (var row in rows) row.UpdateColumnValues(tab.Columns);
        bool fresh = serverView is null;
        if (fresh)
        {
            displayedServers = new();
            var view = new DataGridCollectionView(displayedServers);
            var sortColumn = ServersGrid.Columns.FirstOrDefault(c => (c.Tag as ServerColumn)?.Id == tab.SortColumn);
            if (sortColumn is not null)
            {
                var direction = tab.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                view.SortDescriptions.Add(sortColumn.CustomSortComparer is { } comparer
                    ? DataGridSortDescription.FromComparer(comparer, direction)
                    : DataGridSortDescription.FromPath(sortColumn.SortMemberPath, direction, CultureInfo.InvariantCulture));
            }
            view.SortDescriptions.CollectionChanged += (_, _) =>
            {
                if (!ReferenceEquals(view, serverView) || !ReferenceEquals(active, tab) || changingColumns) return;
                if (view.SortDescriptions.Count == 0) tab.SortColumn = null;
                else
                {
                    var description = view.SortDescriptions.Last();
                    var column = ServersGrid.Columns.FirstOrDefault(c => (c.Tag as ServerColumn)?.Id == sortingColumnId);
                    if (description.HasPropertyPath && column?.SortMemberPath != description.PropertyPath)
                        column = ServersGrid.Columns.FirstOrDefault(c => c.SortMemberPath == description.PropertyPath);
                    if (column?.Tag is ServerColumn definition)
                    {
                        tab.SortColumn = definition.Id;
                        tab.SortDescending = description.Direction == ListSortDirection.Descending;
                    }
                }
                SaveSettings();
            };
            serverView = view;
        }
        // Keep the same view and row containers while results stream in. Replacing
        // ItemsSource on every batch cancels selection and mouse interactions.
        changingServerSelection = true;
        try
        {
            if (fresh) ServersGrid.ItemsSource = serverView;
            if (tab.SortColumn is null)
            {
                var defaults = settings.FavoritesOnTop ? new[] { "Favorite", "HumanPlayers", "Bots" } : new[] { "HumanPlayers", "Bots" };
                if (!serverView!.SortDescriptions.Select(s => s.PropertyPath).SequenceEqual(defaults))
                {
                    changingColumns = true;
                    try
                    {
                        serverView.SortDescriptions.Clear();
                        foreach (var path in defaults) serverView.SortDescriptions.Add(DataGridSortDescription.FromPath(path, ListSortDirection.Descending, CultureInfo.InvariantCulture));
                    }
                    finally { changingColumns = false; }
                }
            }
            var wanted = rows.Select(r => r.Address).ToHashSet();
            for (int i = displayedServers.Count - 1; i >= 0; i--)
                if (!wanted.Contains(displayedServers[i].Address)) displayedServers.RemoveAt(i);
            var indices = displayedServers.Select((row, index) => (row.Address, index)).ToDictionary(p => p.Address, p => p.index);
            foreach (var row in rows)
            {
                if (!indices.TryGetValue(row.Address, out int index)) displayedServers.Add(row);
                else if (!ReferenceEquals(displayedServers[index], row)) displayedServers[index] = row;
            }
            if (!busy && !fresh) serverView!.Refresh();
            ServersGrid.SelectedItem = rows.FirstOrDefault(r => r.Address == selected?.Address) ?? serverView!.Cast<ServerEntry>().FirstOrDefault();
        }
        finally { changingServerSelection = false; }
        SynchronizeDetailsSelection();
        UpdateActionStates();
        if (tab.AutoFitColumns && !busy) BestFitColumns(save: false);
    }

    private void BestFitAll_Click(object? sender, RoutedEventArgs e) => BestFitColumns();
    private void AutoFit_Click(object? sender, RoutedEventArgs e)
    {
        if (active is null) return;
        active.AutoFitColumns = AutoFitMenuItem.IsChecked;
        if (active.AutoFitColumns) BestFitColumns(); else SaveSettings();
    }
    private void BestFitColumns(string? id = null, bool save = true)
    {
        if (active is null) return;
        changingColumns = true;
        try
        {
            GridBestFit.Fit(ServersGrid, ServersGrid.Columns.Where(c => id is null || (c.Tag as ServerColumn)?.Id == id).ToList());
        }
        finally { changingColumns = false; }
        if (save) SaveSettings();
    }

    private async void Columns_Click(object? sender, RoutedEventArgs e)
    {
        if (active is null) return;
        var tab = active;
        CaptureColumnLayout();
        var choices = new StackPanel { Spacing = 4 };
        var checks = new List<(ServerColumn Column, CheckBox Check)>();
        foreach (var column in tab.Columns.OrderBy(c => c.Order))
        {
            var check = new CheckBox { Content = column.Title + (column.Custom ? $"  ({column.Field})" : ""),
                IsChecked = column.Visible, Tag = column.Id };
            checks.Add((column, check)); choices.Children.Add(check);
        }
        var scroll = new ScrollViewer { Content = choices, MaxHeight = 460 };
        if (!await Dialogs.Form(this, "Choose Columns", ("Show columns in this tab (unchecked columns can be restored here)", scroll))) return;
        foreach (var (column, check) in checks) column.Visible = check.IsChecked == true;
        if (ReferenceEquals(active, tab)) ApplyColumnVisibility();
        SaveSettings();
    }

    private async void AddColumn_Click(object? sender, RoutedEventArgs e) => await AddCustomColumnDialog();
    private async Task AddCustomColumnDialog(string field = "Info.GameVersion", string title = "", bool numeric = false)
    {
        if (active is null) return;
        var tab = active;
        var available = typeof(ServerInfo).GetProperties().Where(p => p.Name is not ("Extra" or "EndPoint")).Select(p => "Info." + p.Name)
            .Concat(typeof(ExtraInfo).GetProperties().Where(p => p.Name != "SpecInfo").Select(p => "Info.Extra." + p.Name))
            .Concat(Rows(tab).SelectMany(r => r.Rules).Select(r => "Rule." + r.Name)).Distinct().Order().ToList();
        var fieldBox = new AutoCompleteBox { Name = "ColumnFieldInput", ItemsSource = available, Text = field, MinimumPrefixLength = 0 };
        var titleBox = Dialogs.Text(title.Length > 0 ? title : field.Split('.').Last()); titleBox.Name = "ColumnTitleInput";
        var number = new CheckBox { Name = "ColumnNumericInput", Content = "Sort values as numbers", IsChecked = numeric };
        if (!await Dialogs.Form(this, "Add Custom Column", ("Field (Info.GameVersion, Info.Extra.SteamID, or Rule.sv_tags)", fieldBox),
            ("Column title", titleBox), ("Sorting", number))) return;
        string key = fieldBox.Text?.Trim() ?? "";
        if ((!available.Contains(key) && !(key.StartsWith("Rule.") && key.Length > 5)) || key.Contains('\0'))
        { StatusText.Text = "Unknown field. Choose a server detail, or enter Rule.<rule name>."; return; }
        var definition = new ServerColumn { Id = "Custom" + Guid.NewGuid().ToString("N"), Custom = true,
            Field = key, Title = titleBox.Text?.Trim() is { Length: > 0 } value ? value : key,
            Numeric = number.IsChecked == true, Width = 140, Order = tab.Columns.Count };
        CaptureColumnLayout();
        tab.Columns.Add(definition);
        if (ReferenceEquals(active, tab)) { RestoreServerColumns(); ApplyFilter(); }
        SaveSettings();
        StatusText.Text = key.StartsWith("Rule.") ? "Column added. Use Update Status to populate rule values." : "Column added.";
    }
    private async void AddRuleTextColumn_Click(object? sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is Rule rule) await AddCustomColumnDialog("Rule." + rule.Name, rule.Name);
    }
    private async void AddRuleNumberColumn_Click(object? sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is Rule rule) await AddCustomColumnDialog("Rule." + rule.Name, rule.Name, true);
    }
    private async void AddDetailColumn_Click(object? sender, RoutedEventArgs e)
    {
        if (DetailItemsGrid.SelectedItem is not KeyValuePair<string, string> detail) return;
        string field = detail.Key is "SteamID" or "GameID" or "Keywords" ? "Info.Extra." + (detail.Key == "GameID" ? "GameId" : detail.Key) : "Info." + detail.Key;
        await AddCustomColumnDialog(field, detail.Key);
    }
    private sealed class ColumnValueComparer(ServerColumn definition) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            object? a = x is ServerEntry left ? definition.GetValue(left) : null;
            object? b = y is ServerEntry right ? definition.GetValue(right) : null;
            if (a is null) return b is null ? 0 : 1;
            if (b is null) return -1;
            if (definition.Numeric) return ((decimal)a).CompareTo((decimal)b);
            return StringComparer.OrdinalIgnoreCase.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture));
        }
    }
}
