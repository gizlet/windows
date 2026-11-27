using Gizlet.Core;
using Gizlet.Core.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gizlet.Launcher.Pages;

/// <summary>
/// The Gizlet Store, showing only apps with a package this PC runs: featured shelves, search,
/// and a page per app with its screenshots, controls and an Install, Update or Open button.
/// </summary>
sealed class StorePage : LauncherPage
{
    readonly AutoSuggestBox search = new() { QueryIcon = new SymbolIcon(Symbol.Find), MinWidth = 320 };
    readonly Grid content = new();
    readonly InfoBar status = new() { IsClosable = true };
    CancellationTokenSource? loading;
    string query = "";
    string? cursor;
    StackPanel? results;
    StoreGizlet? detail;
    readonly Dictionary<string, double> installing = new(StringComparer.Ordinal);
    readonly List<(StoreGizlet Gizlet, ContentControl Holder)> holders = [];

    public StorePage(Services.Launcher launcher) : base(launcher)
    {
        search.PlaceholderText = S.SearchStore;
        Ui.AutomationName(search, S.SearchStore);
        search.QuerySubmitted += (_, e) => Search(e.QueryText);
        search.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape });
        search.KeyboardAccelerators[0].Invoked += (_, e) =>
        {
            search.Text = "";
            Search("");
            e.Handled = true;
        };
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Title(S.Store));
        Grid.SetColumn(search, 1);
        header.Children.Add(search);
        Content = Ui.Page(Ui.Stack(16, header, status, content));
        launcher.AppsChanged += OnAppsChanged;
        _ = LoadFeaturedAsync();
    }

    void OnAppsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (detail is not null) ShowDetail(detail);
        else Redraw();
    });

    public override void Hidden() => Launcher.AppsChanged -= OnAppsChanged;

    public override void Shown()
    {
        Launcher.AppsChanged -= OnAppsChanged;
        Launcher.AppsChanged += OnAppsChanged;
    }

    public override bool FocusSearch() => search.Focus(FocusState.Keyboard);

    public override void Refresh()
    {
        if (detail is not null) _ = OpenAsync(detail.Repo);
        else if (query.Length > 0) Search(query);
        else _ = LoadFeaturedAsync();
    }

    CancellationToken Begin()
    {
        loading?.Cancel();
        loading = new CancellationTokenSource();
        status.IsOpen = false;
        return loading.Token;
    }

    /// <summary>Shows new content. Builders call <see cref="Fresh"/> first, so the buttons they register stay live.</summary>
    void Set(UIElement element)
    {
        content.Children.Clear();
        content.Children.Add(element);
    }

    void Fresh() => holders.Clear();

    static ProgressRing Spinner() => new() { IsActive = true, Width = 32, Height = 32, Margin = new Thickness(0, 48, 0, 48), HorizontalAlignment = HorizontalAlignment.Center };

    async Task LoadFeaturedAsync()
    {
        detail = null;
        query = "";
        var token = Begin();
        Set(Spinner());
        try
        {
            var featured = await Launcher.Store.FeaturedAsync(token);
            Fresh();
            var panel = new StackPanel { Spacing = 24 };
            if (featured.Hero.Count > 0) panel.Children.Add(Hero(featured.Hero[0]));
            if (featured.Hero.Count > 1) panel.Children.Add(Shelf(featured.Hero.Skip(1)));
            foreach (var section in featured.Sections) panel.Children.Add(Ui.Stack(8, Ui.Subtitle(section.TitleIn(S.Language)), Shelf(section.Gizlets)));
            if (panel.Children.Count == 0)
            {
                // Nothing hand-picked for Windows yet: list what there is.
                var all = await Launcher.Store.SearchAsync("", null, 48, token);
                panel.Children.Add(all.Gizlets.Count == 0 ? Ui.Card(Ui.Text(S.NothingForWindows), 24) : Ui.Stack(8, Ui.Subtitle(S.AllApps), List(all.Gizlets)));
            }
            Set(panel);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Offline(e, () => _ = LoadFeaturedAsync());
        }
    }

    void Search(string text)
    {
        query = text.Trim();
        if (query.Length == 0)
        {
            _ = LoadFeaturedAsync();
            return;
        }
        detail = null;
        cursor = null;
        Fresh();
        results = new StackPanel { Spacing = 4 };
        Set(Ui.Stack(8, Ui.Subtitle(S.Results(query)), results));
        _ = MoreAsync();
    }

    async Task MoreAsync()
    {
        if (results is null) return;
        var token = Begin();
        var list = results;
        if (list.Children.LastOrDefault() is Button) list.Children.RemoveAt(list.Children.Count - 1);
        var spinner = Spinner();
        list.Children.Add(spinner);
        try
        {
            var page = await Launcher.Store.SearchAsync(query, cursor, 24, token);
            list.Children.Remove(spinner);
            foreach (var gizlet in page.Gizlets) list.Children.Add(Row(gizlet));
            cursor = page.NextCursor;
            if (list.Children.Count == 0) list.Children.Add(Ui.Card(Ui.Text(S.NothingFound), 24));
            if (cursor is not null) list.Children.Add(Ui.Button(S.LoadMore, () => _ = MoreAsync()));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            list.Children.Remove(spinner);
            Offline(e, () => Search(query));
        }
    }

    void Offline(Exception e, Action retry)
    {
        status.Severity = InfoBarSeverity.Error;
        status.Title = S.StoreOffline;
        status.Message = e is GizletException g ? S.Error(g) : "";
        status.ActionButton = Ui.Button(S.Retry, retry);
        status.IsOpen = true;
        if (content.Children.FirstOrDefault() is ProgressRing) content.Children.Clear();
    }

    Border Hero(StoreGizlet gizlet)
    {
        var name = gizlet.NameIn(S.Language);
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (gizlet.CoverUrl is { } cover)
        {
            grid.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                Child = new Image { Source = new BitmapImage(cover) { DecodePixelWidth = 960 }, Stretch = Stretch.UniformToFill, MaxHeight = 280 },
            });
        }
        var text = Ui.Stack(8, Ui.Title(name), Ui.Text(gizlet.DescriptionIn(S.Language) ?? ""), Ui.Row(8, StateButton(gizlet)));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return Clickable(Ui.Card(grid, 16), gizlet);
    }

    ScrollViewer Shelf(IEnumerable<StoreGizlet> gizlets)
    {
        var row = Ui.Row(12);
        foreach (var gizlet in gizlets)
        {
            var name = gizlet.NameIn(S.Language);
            var card = Ui.Card(Ui.Stack(8,
                Ui.AppIcon(gizlet.IconUrl, gizlet.Color, name, 96),
                Ui.Strong(name, wrap: false),
                Ui.Secondary(Category(gizlet), wrap: false)), 12);
            card.Width = 132;
            row.Children.Add(Clickable(card, gizlet));
        }
        return new ScrollViewer
        {
            Content = row,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 0, 12),
        };
    }

    StackPanel List(IEnumerable<StoreGizlet> gizlets)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var gizlet in gizlets) list.Children.Add(Row(gizlet));
        return list;
    }

    Border Row(StoreGizlet gizlet)
    {
        var name = gizlet.NameIn(S.Language);
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Ui.AppIcon(gizlet.IconUrl, gizlet.Color, name, 48));
        var badges = Ui.Row(6);
        if (InstalledVersion(gizlet) is { } have)
        {
            badges.Children.Add(Ui.Badge(Services.AppUpdates.IsNewer(gizlet, have) ? S.UpdateAvailable : S.InstalledBadge, accent: Services.AppUpdates.IsNewer(gizlet, have)));
        }
        var text = Ui.Stack(2, Ui.Row(8, Ui.Strong(name, wrap: false), badges), Ui.Secondary($"{Category(gizlet)} · {gizlet.DescriptionIn(S.Language)}", wrap: false));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = StateButton(gizlet);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return Clickable(Ui.Card(grid, 12), gizlet);
    }

    Border Clickable(Border card, StoreGizlet gizlet)
    {
        card.IsTabStop = true;
        card.UseSystemFocusVisuals = true;
        Ui.AutomationName(card, gizlet.NameIn(S.Language));
        card.Tapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInButton(source, card)) return;
            _ = OpenAsync(gizlet.Repo);
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key is global::Windows.System.VirtualKey.Enter or global::Windows.System.VirtualKey.Space)
            {
                _ = OpenAsync(gizlet.Repo);
                e.Handled = true;
            }
        };
        return card;
    }

    static bool IsInButton(DependencyObject element, DependencyObject stop)
    {
        for (var at = element; at is not null && at != stop; at = VisualTreeHelper.GetParent(at))
        {
            if (at is ButtonBase) return true;
        }
        return false;
    }

    static string Category(StoreGizlet gizlet) => gizlet.Category == "game" ? S.Game : S.App;

    /// <summary>The installed version, when this gizlet's repository installed it.</summary>
    string? InstalledVersion(StoreGizlet gizlet) =>
        Launcher.Library.Find(gizlet.Id) is { } app && (app.Origin is null || string.Equals(app.Origin.Repo, gizlet.Repo, StringComparison.OrdinalIgnoreCase))
            ? app.Manifest.Version
            : null;

    /// <summary>Install, Update, Open, or the install's progress, kept up to date in place.</summary>
    ContentControl StateButton(StoreGizlet gizlet)
    {
        var holder = new ContentControl { Content = State(gizlet), IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
        holders.Add((gizlet, holder));
        return holder;
    }

    FrameworkElement State(StoreGizlet gizlet)
    {
        if (installing.TryGetValue(gizlet.Repo, out var progress))
        {
            return new ProgressBar { Value = progress * 100, Width = 120, IsIndeterminate = progress <= 0 };
        }
        if (InstalledVersion(gizlet) is { } have)
        {
            if (Services.AppUpdates.IsNewer(gizlet, have)) return Ui.Button(S.Update, () => _ = InstallAsync(gizlet), "\uE896", accent: true);
            return Ui.Button(S.Open, () =>
            {
                if (Launcher.Library.Find(gizlet.Id) is { } app)
                {
                    try
                    {
                        Launcher.Runner.Start(app);
                    }
                    catch (Exception e)
                    {
                        Error(e);
                    }
                }
            }, "\uE768");
        }
        return Ui.Button(S.Get, () => _ = InstallAsync(gizlet), "\uE896", accent: true);
    }

    async Task InstallAsync(StoreGizlet gizlet)
    {
        if (installing.ContainsKey(gizlet.Repo)) return;
        installing[gizlet.Repo] = 0;
        Redraw();
        try
        {
            var install = await Launcher.Store.InstallAsync(gizlet);
            var progress = new Progress<double>(p =>
            {
                installing[gizlet.Repo] = Math.Max(0.01, p);
                Redraw();
            });
            var app = await Launcher.InstallFromStoreAsync(install.Package!, install.Repo, install.RepositoryId, install.Tag, progress);
            status.Severity = InfoBarSeverity.Success;
            status.Title = S.Installed(app.Manifest.NameIn(S.Language));
            status.Message = "";
            status.ActionButton = Ui.Button(S.Open, () => Launcher.Runner.Start(app));
            status.IsOpen = true;
        }
        catch (Exception e)
        {
            Error(e);
        }
        finally
        {
            installing.Remove(gizlet.Repo);
            Redraw();
        }
    }

    void Redraw()
    {
        foreach (var (gizlet, holder) in holders) holder.Content = State(gizlet);
    }

    void Error(Exception e)
    {
        status.Severity = InfoBarSeverity.Error;
        status.Title = S.Error(e);
        status.Message = "";
        status.ActionButton = null;
        status.IsOpen = true;
    }

    async Task OpenAsync(string repo)
    {
        var token = Begin();
        Set(Spinner());
        try
        {
            var gizlet = await Launcher.Store.GizletAsync(repo, token);
            detail = gizlet;
            ShowDetail(gizlet);
            if (gizlet.AboutUrls.Count > 0) _ = LoadAboutAsync(gizlet, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Offline(e, () => _ = OpenAsync(repo));
        }
    }

    TextBlock? about;
    string? aboutText;

    void ShowDetail(StoreGizlet gizlet)
    {
        Fresh();
        var name = gizlet.NameIn(S.Language);
        var back = Ui.Button(S.Back, () =>
        {
            detail = null;
            if (query.Length > 0) Search(query);
            else _ = LoadFeaturedAsync();
        }, "\uE72B");
        back.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Left, Modifiers = global::Windows.System.VirtualKeyModifiers.Menu });
        back.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Back, Modifiers = global::Windows.System.VirtualKeyModifiers.None });

        var head = new Grid { ColumnSpacing = 20 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(Ui.AppIcon(gizlet.IconUrl, gizlet.Color, name, 112));
        var installed = InstalledVersion(gizlet);
        var facts = $"{Category(gizlet)} · {S.Version(gizlet.Version ?? "?")}" + (installed is not null ? $" · {S.InstalledBadge} {installed}" : "");
        var title = Ui.Stack(6, Ui.Title(name), Ui.Secondary(facts, "BodyTextBlockStyle"), Ui.Text(gizlet.DescriptionIn(S.Language) ?? ""), Ui.Row(8, StateButton(gizlet)));
        Grid.SetColumn(title, 1);
        head.Children.Add(title);

        var panel = Ui.Stack(24, back, head);
        if (gizlet.Screenshots.Count > 0)
        {
            var shots = Ui.Row(12);
            foreach (var shot in gizlet.Screenshots)
            {
                shots.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Child = new Image { Source = new BitmapImage(shot) { DecodePixelWidth = 640 }, Height = 240, Stretch = Stretch.Uniform },
                });
            }
            panel.Children.Add(Ui.Stack(8, Ui.Subtitle(S.Screenshots), new ScrollViewer
            {
                Content = shots,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 0, 12),
            }));
        }
        about = Ui.Text(aboutText ?? "");
        if (gizlet.AboutUrls.Count > 0) panel.Children.Add(Ui.Card(about, 20));
        if (gizlet.Controls.Count > 0)
        {
            var controls = new StackPanel { Spacing = 6 };
            foreach (var control in gizlet.Controls)
            {
                var action = control.Actions.TryGetValue(S.Language, out var local) ? local : control.Action;
                controls.Children.Add(Ui.Row(12, Ui.Badge(control.Button.ToUpperInvariant()), Ui.Text(action)));
            }
            panel.Children.Add(Ui.Stack(8, Ui.Subtitle(S.Controls), Ui.Card(controls)));
        }
        var details = new StackPanel { Spacing = 6 };
        if (gizlet.Owner is { } owner) details.Children.Add(Ui.Row(8, Ui.Strong(S.Developer), Ui.Text(owner)));
        if (gizlet.License is { } license) details.Children.Add(Ui.Row(8, Ui.Strong(S.License), Ui.Text(license)));
        if (gizlet.ShareUrl is { } share) details.Children.Add(new HyperlinkButton { Content = S.ViewOnWeb, NavigateUri = share, Padding = new Thickness(0) });
        panel.Children.Add(Ui.Stack(8, Ui.Subtitle(S.Details), Ui.Card(details)));
        Set(panel);
    }

    async Task LoadAboutAsync(StoreGizlet gizlet, CancellationToken token)
    {
        var url = gizlet.AboutUrls.TryGetValue(S.Language, out var local) ? local : gizlet.AboutUrls.GetValueOrDefault("en") ?? gizlet.AboutUrls.Values.First();
        try
        {
            var markdown = await Launcher.Http.GetStringAsync(url, token);
            aboutText = Plain(markdown);
            if (about is not null && detail?.Repo == gizlet.Repo) about.Text = aboutText;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
    }

    /// <summary>A Markdown about page as plain paragraphs: headings, emphasis and links kept as text.</summary>
    static string Plain(string markdown)
    {
        var lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(line =>
        {
            var text = line.TrimStart('#', ' ', '>');
            text = System.Text.RegularExpressions.Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
            text = text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            return System.Text.RegularExpressions.Regex.Replace(text, @"^\s*[-*+]\s+", "• ");
        });
        return string.Join('\n', lines).Trim();
    }
}
