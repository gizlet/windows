using System.Runtime.InteropServices;
using Gizlet.Core.Profiles;
using Gizlet.Launcher.Pages;
using Gizlet.Launcher.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace Gizlet.Launcher;

/// <summary>
/// The window: Mica behind a title bar of its own, a Fluent side bar (Apps, Store, Flash a card,
/// the current profile and Settings) and the Launcher update banner.
/// </summary>
sealed partial class MainWindow : Window
{
    readonly Services.Launcher launcher;
    readonly Grid root = new();
    readonly NavigationView navigation = new();
    readonly InfoBar updateBar = new() { IsClosable = true, Margin = new Thickness(16, 0, 16, 8) };
    readonly Dictionary<string, LauncherPage> pages = [];
    readonly TextBlock titleText = new();
    string current = "apps";
    Core.SemVersion? dismissedUpdate;

    public MainWindow(Services.Launcher launcher)
    {
        this.launcher = launcher;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "gizlet.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 520;
        }

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = TitleBar();
        root.Children.Add(titleBar);
        Grid.SetRow(updateBar, 1);
        root.Children.Add(updateBar);
        Grid.SetRow(navigation, 2);
        root.Children.Add(navigation);
        Content = root;
        SetTitleBar(titleBar);

        navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        navigation.IsSettingsVisible = true;
        navigation.OpenPaneLength = 260;
        navigation.SelectionChanged += (_, e) =>
        {
            var tag = e.IsSettingsSelected ? "settings" : (e.SelectedItem as NavigationViewItem)?.Tag as string;
            if (tag is not null && tag != current) Navigate(tag);
        };
        navigation.ItemInvoked += (_, e) =>
        {
            if (e.InvokedItemContainer?.Tag is "profile") Navigate("settings");
        };
        AddAccelerators();

        updateBar.Closed += (_, _) =>
        {
            dismissedUpdate = launcher.Updates.Available?.Version;
            launcher.Updates.Dismiss();
        };
        launcher.Updates.Changed += (_, _) => DispatcherQueue.TryEnqueue(ShowUpdate);
        launcher.ProfileChanged += (_, _) => Rebuild();
        Closed += (_, e) =>
        {
            // A card write must finish; the window only hides until then.
            if (launcher.Flash.Busy)
            {
                e.Handled = true;
                AppWindow.Hide();
            }
        };
        Rebuild();
    }

    public nint Handle => Win32Interop.GetWindowFromWindowId(AppWindow.Id);

    public void Show(bool minimized)
    {
        if (!AppWindow.IsVisible)
        {
            var scale = GetDpiForWindow(Handle) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(780 * scale)));
        }
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (minimized) presenter.Minimize();
            else if (presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        }
        if (!minimized) Services.Native.Focus(Handle);
    }

    Grid TitleBar()
    {
        var bar = new Grid { Padding = new Thickness(16, 0, 140, 0) };
        var icon = new Image { Width = 16, Height = 16, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "gizlet-32.png"))) };
        titleText.Style = Ui.Resource<Style>("CaptionTextBlockStyle");
        titleText.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(Ui.Row(12, icon, titleText));
        return bar;
    }

    /// <summary>Builds every page again, in the profile's language and theme.</summary>
    void Rebuild()
    {
        foreach (var page in pages.Values) page.Hidden();
        pages.Clear();
        Title = S.AppName;
        titleText.Text = S.AppName;
        root.RequestedTheme = launcher.Settings.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = launcher.Settings.Theme switch
        {
            ThemeChoice.Light => TitleBarTheme.Light,
            ThemeChoice.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        navigation.MenuItems.Clear();
        navigation.MenuItems.Add(Item("apps", S.Apps, "\uE71D"));
        navigation.MenuItems.Add(Item("store", S.Store, "\uE719"));
        navigation.MenuItems.Add(Item("flash", S.Flash, "\uE88E"));
        navigation.FooterMenuItems.Clear();
        var profile = Item("profile", launcher.Profile.Name, "\uE77B");
        profile.SelectsOnInvoked = false;
        ToolTipService.SetToolTip(profile, S.Profiles);
        navigation.FooterMenuItems.Add(profile);
        if (navigation.SettingsItem is NavigationViewItem settings) settings.Content = S.Settings;
        Navigate(current);
        ShowUpdate();
    }

    static NavigationViewItem Item(string tag, string text, string glyph)
    {
        var item = new NavigationViewItem { Content = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
        Ui.AutomationName(item, text);
        return item;
    }

    public void Navigate(string tag)
    {
        if (pages.TryGetValue(current, out var old) && current != tag) old.Hidden();
        current = tag;
        if (!pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "store" => new StorePage(launcher),
                "flash" => new FlashPage(launcher),
                "settings" => new SettingsPage(launcher),
                _ => new AppsPage(launcher),
            };
            pages[tag] = page;
        }
        navigation.Content = page;
        page.Shown();
        var selected = tag == "settings"
            ? navigation.SettingsItem
            : navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (!ReferenceEquals(navigation.SelectedItem, selected)) navigation.SelectedItem = selected;
    }

    public T Page<T>(string tag) where T : LauncherPage
    {
        Navigate(tag);
        return (T)pages[tag];
    }

    void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                action();
                e.Handled = true;
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control, () => Navigate("apps"));
        Add(VirtualKey.Number2, VirtualKeyModifiers.Control, () => Navigate("store"));
        Add(VirtualKey.Number3, VirtualKeyModifiers.Control, () => Navigate("flash"));
        Add((VirtualKey)188, VirtualKeyModifiers.Control, () => Navigate("settings"));
        Add(VirtualKey.F, VirtualKeyModifiers.Control, () =>
        {
            if (!pages.TryGetValue(current, out var page) || !page.FocusSearch()) Page<StorePage>("store").FocusSearch();
        });
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = Page<AppsPage>("apps").ImportAsync());
        Add(VirtualKey.F5, VirtualKeyModifiers.None, () =>
        {
            if (pages.TryGetValue(current, out var page)) page.Refresh();
        });
    }

    /// <summary>The banner for a new Launcher: its version and notes, and what the update is doing.</summary>
    void ShowUpdate()
    {
        var updates = launcher.Updates;
        var update = updates.Available;
        updateBar.ActionButton = null;
        switch (updates.State)
        {
            case UpdateState.Available when update is not null && update.Version != dismissedUpdate:
                updateBar.Severity = InfoBarSeverity.Informational;
                updateBar.Title = S.NewLauncher(update.Version.ToString());
                updateBar.Message = update.Manifest.NotesIn(S.Language);
                updateBar.ActionButton = Ui.Button(S.InstallUpdate, () => _ = updates.InstallAsync(), accent: true);
                updateBar.IsOpen = true;
                break;
            case UpdateState.Downloading:
                updateBar.Severity = InfoBarSeverity.Informational;
                updateBar.Title = S.UpdateDownloading;
                updateBar.Message = $"{updates.Progress:P0}";
                updateBar.IsOpen = true;
                break;
            case UpdateState.Verifying or UpdateState.Installing:
                updateBar.Severity = InfoBarSeverity.Informational;
                updateBar.Title = S.UpdateVerifying;
                updateBar.Message = "";
                updateBar.IsOpen = true;
                break;
            case UpdateState.WaitingForFlash:
                updateBar.Severity = InfoBarSeverity.Warning;
                updateBar.Title = S.UpdateWaitsForFlash;
                updateBar.Message = "";
                updateBar.IsOpen = true;
                break;
            case UpdateState.Failed:
                updateBar.Severity = InfoBarSeverity.Error;
                updateBar.Title = S.UpdateFailed(updates.Failure ?? "");
                updateBar.Message = "";
                updateBar.ActionButton = Ui.Button(S.Retry, () => _ = updates.InstallAsync());
                updateBar.IsOpen = true;
                break;
            default:
                updateBar.IsOpen = false;
                break;
        }
        if (pages.TryGetValue("settings", out var settings)) ((SettingsPage)settings).ShowUpdate();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
