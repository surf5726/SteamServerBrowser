using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using QueryMaster;

namespace ServerBrowser.Desktop;

public partial class MainWindow
{
    private void InitializePlayerGrid()
    {
        foreach (var column in PlayersGrid.Columns.OfType<DataGridTextColumn>())
        {
            column.SortMemberPath = (column.Binding as Avalonia.Data.Binding)?.Path;
            string field = column.SortMemberPath ?? "";
            if (settings.PlayerColumnWidths.TryGetValue(field, out var width))
                column.Width = new DataGridLength(width, DataGridLengthUnitType.Pixel, width, width);
            column.HeaderTemplate = new FuncDataTemplate<object>((_, _) =>
            {
                var menu = new ContextMenu();
                var fit = new MenuItem { Header = "Best Fit" };
                fit.Click += (_, _) => FitPlayerColumns(column);
                var fitAll = new MenuItem { Header = "Best Fit All Columns" };
                fitAll.Click += PlayerBestFitAll_Click;
                menu.Items.Add(fit); menu.Items.Add(fitAll);
                var label = new TextBlock { Text = column.Header?.ToString(), TextTrimming = TextTrimming.CharacterEllipsis };
                var header = new Border { Background = Brushes.Transparent, ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch, Child = label, ContextMenu = menu };
                header.DoubleTapped += (_, e) => { FitPlayerColumns(column); e.Handled = true; };
                return header;
            });
        }
    }

    private void SetPlayerRows(IEnumerable<Player> players)
    {
        var view = new DataGridCollectionView(players.ToList());
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(settings.PlayerSortColumn,
            settings.PlayerSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        view.SortDescriptions.CollectionChanged += (_, _) =>
        {
            if (!ReferenceEquals(PlayersGrid.ItemsSource, view) || view.SortDescriptions.Count == 0) return;
            var sort = view.SortDescriptions.Last();
            if (sort.PropertyPath is not ("Name" or "Score" or "Time")) return;
            settings.PlayerSortColumn = sort.PropertyPath;
            settings.PlayerSortDescending = sort.Direction == ListSortDirection.Descending;
            SaveSettings();
        };
        PlayersGrid.ItemsSource = view;
    }

    private void PlayerBestFitAll_Click(object? sender, RoutedEventArgs e) => FitPlayerColumns();
    private void FitPlayerColumns(DataGridColumn? column = null)
    {
        GridBestFit.Fit(PlayersGrid, column is null ? PlayersGrid.Columns : [column]);
        CapturePlayerWidths();
        SaveSettings();
    }
    private void CapturePlayerWidths()
    {
        foreach (var column in PlayersGrid.Columns)
            if (!string.IsNullOrEmpty(column.SortMemberPath) && column.Width.IsAbsolute)
                settings.PlayerColumnWidths[column.SortMemberPath] = column.Width.Value;
    }
}

