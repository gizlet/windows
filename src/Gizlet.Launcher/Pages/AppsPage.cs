using Gizlet.Core;
using Gizlet.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;

namespace Gizlet.Launcher.Pages;

/// <summary>
/// The installed apps, favourites first. Each opens with its current profile's saves; updates
/// come from the repository it was installed from; uninstalling keeps every profile's saves.
/// </summary>
sealed class AppsPage : LauncherPage
{
    readonly StackPanel body = new() { Spacing = 24 };
    readonly InfoBar status = new() { IsClosable = true };
    readonly HashSet<string> busy = new(StringComparer.Ordinal);
    bool checking;

    public AppsPage(Services.Launcher launcher) : base(launcher)
    {
        var import = Ui.Button(S.ImportPackage, () => _ = ImportAsync(), "\uE8E5");
        ToolTipService.SetToolTip(import, "Ctrl+O");
        var check = Ui.Button(S.CheckAppUpdates, () => _ = CheckAsync(), "\uE895");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Title(S.Apps));
        var actions = Ui.Row(8, check, import);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        Content = Ui.Page(Ui.Stack(16, header, status, body));
        launcher.AppsChanged += OnAppsChanged;
        Build();
    }

    void OnAppsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Build);

    public override void Hidden() => Launcher.AppsChanged -= OnAppsChanged;

    public override void Shown()
    {
        Launcher.AppsChanged -= OnAppsChanged;
        Launcher.AppsChanged += OnAppsChanged;
        Build();
    }

    public override void Refresh() => _ = CheckAsync();

    void Build()
    {
        body.Children.Clear();
        var apps = Launcher.Library.List();
        if (apps.Count == 0)
        {
            body.Children.Add(Ui.Card(Ui.Stack(12,
                Ui.Text(S.NoApps),
                Ui.Row(8, Ui.Button(S.GoToStore, () => Window.Navigate("store"), "\uE719", accent: true),
                    Ui.Button(S.ImportPackage, () => _ = ImportAsync(), "\uE8E5"))), 24));
            return;
        }
        var favorites = apps.Where(a => Launcher.IsFavorite(a.Id)).ToList();
        if (favorites.Count > 0) body.Children.Add(Section(S.Favorites, favorites));
        body.Children.Add(Section(S.AllApps, apps));
    }

    StackPanel Section(string title, IEnumerable<InstalledApp> apps)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var app in apps) list.Children.Add(Tile(app));
        return Ui.Stack(8, Ui.Subtitle(title), list);
    }

    Border Tile(InstalledApp app)
    {
        var manifest = app.Manifest;
        var name = manifest.NameIn(S.Language);
        var running = Launcher.Runner.IsRunning(app.Id);
        var update = Launcher.AppUpdates.For(app.Id);
        var working = busy.Contains(app.Id);

        var icon = Ui.AppIcon(app.IconPath is { } path && File.Exists(path) ? new Uri(path) : null, manifest.Color, name, 48);
        var badges = Ui.Row(6);
        if (running) badges.Children.Add(Ui.Badge(S.Running, accent: true));
        if (update is not null) badges.Children.Add(Ui.Badge(S.UpdateAvailable, accent: true));
        var details = S.Version(manifest.Version) + (app.Origin is { } origin ? $" · {origin.Repo}" : $" · {S.LocalPackage}");
        var text = Ui.Stack(2, Ui.Row(8, Ui.Strong(name, wrap: false), badges), Ui.Secondary(details, wrap: false));
        text.VerticalAlignment = VerticalAlignment.Center;

        var open = Ui.Button(running ? S.Running : S.Open, () => Start(app), "\uE768", accent: true);
        open.IsEnabled = !working;
        var favorite = new ToggleButton
        {
            IsChecked = Launcher.IsFavorite(app.Id),
            Content = Ui.Glyph(Launcher.IsFavorite(app.Id) ? "\uE735" : "\uE734", 14),
        };
        var favoriteName = Launcher.IsFavorite(app.Id) ? S.RemoveFavorite : S.AddFavorite;
        Ui.AutomationName(favorite, favoriteName);
        ToolTipService.SetToolTip(favorite, favoriteName);
        favorite.Click += (_, _) => Launcher.ToggleFavorite(app.Id);

        var menu = new MenuFlyout();
        if (update is not null)
        {
            var updateItem = new MenuFlyoutItem { Text = $"{S.Update} ({update.Version})", Icon = new FontIcon { Glyph = "\uE896" }, IsEnabled = !running && !working };
            updateItem.Click += (_, _) => _ = UpdateAsync(app);
            menu.Items.Add(updateItem);
        }
        var folder = new MenuFlyoutItem { Text = S.OpenFolder, Icon = new FontIcon { Glyph = "\uE838" } };
        folder.Click += (_, _) => Services.Launcher.OpenInExplorer(Launcher.Profiles.SaveDirectory(Launcher.Profile.Id, app.Id));
        menu.Items.Add(folder);
        menu.Items.Add(new MenuFlyoutSeparator());
        var uninstall = new MenuFlyoutItem { Text = S.Uninstall, Icon = new FontIcon { Glyph = "\uE74D" }, IsEnabled = !running && !working };
        uninstall.Click += (_, _) => _ = UninstallAsync(app, name);
        menu.Items.Add(uninstall);
        var more = new Button { Content = Ui.Glyph("\uE712", 14), Flyout = menu };
        Ui.AutomationName(more, S.Details);

        var actions = Ui.Row(8);
        if (update is not null)
        {
            var updateButton = Ui.Button(S.Update, () => _ = UpdateAsync(app), "\uE896");
            updateButton.IsEnabled = !running && !working;
            actions.Children.Add(updateButton);
        }
        actions.Children.Add(open);
        actions.Children.Add(favorite);
        actions.Children.Add(more);
        if (working) actions.Children.Insert(0, new ProgressRing { IsActive = true, Width = 20, Height = 20 });

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(actions, 2);
        actions.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(actions);
        grid.DoubleTapped += (_, _) => Start(app);
        return Ui.Card(grid, 12);
    }

    void Start(InstalledApp app)
    {
        try
        {
            Launcher.Runner.Start(app);
            Launcher.RaiseAppsChanged();
        }
        catch (Exception e)
        {
            Show(InfoBarSeverity.Error, S.Error(e));
        }
    }

    /// <summary>Installs a .gzl from disk, chosen with Windows' file picker.</summary>
    public async Task ImportAsync()
    {
        var picker = new FileOpenPicker(Window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".gzl");
        var file = await picker.PickSingleFileAsync();
        if (file?.Path is not { Length: > 0 } path) return;
        Show(InfoBarSeverity.Informational, S.Installing);
        try
        {
            var app = await Launcher.InstallAsync(path, null);
            Show(InfoBarSeverity.Success, S.Installed(app.Manifest.NameIn(S.Language)));
        }
        catch (Exception e)
        {
            Show(InfoBarSeverity.Error, S.Error(e));
        }
    }

    async Task CheckAsync()
    {
        if (checking) return;
        checking = true;
        Show(InfoBarSeverity.Informational, S.Checking);
        try
        {
            await Launcher.AppUpdates.CheckAsync();
            var count = Launcher.AppUpdates.Count;
            Show(InfoBarSeverity.Success, count == 0 ? S.AppsUpToDate : S.UpdatesFound(count));
        }
        catch (Exception e)
        {
            Show(InfoBarSeverity.Error, S.Error(e));
        }
        finally
        {
            checking = false;
        }
    }

    async Task UpdateAsync(InstalledApp app)
    {
        if (Launcher.AppUpdates.For(app.Id) is not { Package: { } package } update || app.Origin is not { } origin) return;
        busy.Add(app.Id);
        Build();
        try
        {
            Show(InfoBarSeverity.Informational, S.Downloading);
            var installed = await Launcher.InstallFromStoreAsync(package, origin.Repo, update.RepositoryId ?? origin.RepositoryId, update.Tag ?? origin.Tag, null);
            Show(InfoBarSeverity.Success, S.Installed($"{installed.Manifest.NameIn(S.Language)} {installed.Manifest.Version}"));
        }
        catch (Exception e)
        {
            Show(InfoBarSeverity.Error, S.Error(e));
        }
        finally
        {
            busy.Remove(app.Id);
            Build();
        }
    }

    async Task UninstallAsync(InstalledApp app, string name)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, S.UninstallTitle(name), S.UninstallBody, S.Uninstall, destructive: true)) return;
        try
        {
            await Launcher.UninstallAsync(app.Id);
        }
        catch (GizletException e)
        {
            Show(InfoBarSeverity.Error, S.Error(e));
        }
    }

    void Show(InfoBarSeverity severity, string message)
    {
        status.Severity = severity;
        status.Message = message;
        status.IsOpen = true;
    }
}
