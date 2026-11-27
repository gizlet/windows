using Gizlet.Core;
using Gizlet.Core.Profiles;
using Gizlet.Launcher.Services;
using Gizlet.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gizlet.Launcher.Pages;

/// <summary>
/// Profiles, the current profile's language and appearance, Launcher updates and starting with
/// Windows. Language and appearance belong to the profile and apply at once.
/// </summary>
sealed class SettingsPage : LauncherPage
{
    readonly StackPanel profiles = new() { Spacing = 4 };
    readonly InfoBar profileStatus = new() { IsClosable = true };
    readonly TextBlock updateText = Ui.Text("");
    readonly TextBlock updateDetail = Ui.Secondary("");
    readonly Button checkButton;
    readonly Button installButton;

    public SettingsPage(Services.Launcher launcher) : base(launcher)
    {
        checkButton = Ui.Button(S.CheckNow, () => _ = launcher.Updates.CheckAsync(), "\uE895");
        installButton = Ui.Button(S.InstallUpdate, () => _ = launcher.Updates.InstallAsync(), accent: true);

        var page = Ui.Stack(28,
            Ui.Title(S.Settings),
            ProfilesSection(),
            LanguageSection(),
            AppearanceSection(),
            UpdatesSection(),
            SystemSection());
        Content = Ui.Page(page);
        launcher.Profiles.Changed += OnProfilesChanged;
        BuildProfiles();
        ShowUpdate();
    }

    void OnProfilesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(BuildProfiles);

    public override void Hidden() => Launcher.Profiles.Changed -= OnProfilesChanged;

    public override void Shown()
    {
        Launcher.Profiles.Changed -= OnProfilesChanged;
        Launcher.Profiles.Changed += OnProfilesChanged;
        BuildProfiles();
        ShowUpdate();
    }

    public override void Refresh() => _ = Launcher.Updates.CheckAsync();

    StackPanel ProfilesSection()
    {
        var create = Ui.Button(S.NewProfile, () => _ = CreateProfileAsync(), "\uE8FA");
        return Ui.Stack(8, Ui.Subtitle(S.Profiles), Ui.Secondary(S.ProfilesHelp, "BodyTextBlockStyle"), profileStatus, profiles, create);
    }

    void BuildProfiles()
    {
        profiles.Children.Clear();
        var current = Launcher.Profile.Id;
        foreach (var profile in Launcher.Profiles.List())
        {
            var isCurrent = profile.Id == current;
            var name = Ui.Row(8, Ui.Glyph("\uE77B"), Ui.Strong(profile.Name, wrap: false));
            if (isCurrent) name.Children.Add(Ui.Badge(S.CurrentProfile, accent: true));
            var actions = Ui.Row(8);
            if (!isCurrent) actions.Children.Add(Ui.Button(S.SwitchTo, () => Switch(profile)));
            var rename = Ui.Button(S.Rename, () => _ = RenameAsync(profile), "\uE8AC");
            actions.Children.Add(rename);
            if (!isCurrent) actions.Children.Add(Ui.Button(S.Delete, () => _ = DeleteAsync(profile), "\uE74D"));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            name.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(name);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            profiles.Children.Add(Ui.Card(grid, 12));
        }
    }

    void Switch(Profile profile)
    {
        try
        {
            Launcher.SwitchProfile(profile.Id);
        }
        catch (Exception e) when (e is InvalidOperationException or GizletException)
        {
            ShowProfileError(e is GizletException g ? S.Error(g) : e.Message);
        }
    }

    async Task CreateProfileAsync()
    {
        var name = await Ui.AskAsync(XamlRoot, S.NewProfile, S.ProfileName, "", S.Save);
        if (name is null) return;
        try
        {
            Launcher.Profiles.Create(name);
        }
        catch (GizletException e)
        {
            ShowProfileError(S.Error(e));
        }
    }

    async Task RenameAsync(Profile profile)
    {
        var name = await Ui.AskAsync(XamlRoot, S.Rename, S.ProfileName, profile.Name, S.Save);
        if (name is null || name == profile.Name) return;
        try
        {
            Launcher.Profiles.Rename(profile.Id, name);
            if (profile.Id == Launcher.Profile.Id) Launcher.ReloadProfile();
        }
        catch (GizletException e)
        {
            ShowProfileError(S.Error(e));
        }
    }

    async Task DeleteAsync(Profile profile)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, S.DeleteProfileTitle(profile.Name), S.DeleteProfileBody, S.Delete, destructive: true)) return;
        try
        {
            await Task.Run(() => Launcher.Profiles.Delete(profile.Id));
        }
        catch (Exception e) when (e is GizletException or IOException or UnauthorizedAccessException)
        {
            ShowProfileError(S.Error(e));
        }
    }

    void ShowProfileError(string message)
    {
        profileStatus.Severity = InfoBarSeverity.Error;
        profileStatus.Message = message;
        profileStatus.IsOpen = true;
    }

    StackPanel LanguageSection()
    {
        var choices = new RadioButtons();
        Ui.AutomationName(choices, S.Language_);
        string[] values = ["system", "en", "zh-CN"];
        choices.Items.Add(S.FollowWindows);
        choices.Items.Add("English");
        choices.Items.Add("简体中文");
        choices.SelectedIndex = Math.Max(0, Array.IndexOf(values, Launcher.Settings.Language));
        choices.SelectionChanged += (_, _) =>
        {
            var value = values[Math.Max(0, choices.SelectedIndex)];
            if (value != Launcher.Settings.Language) DispatcherQueue.TryEnqueue(() => Launcher.ChangeSettings(s => s with { Language = value }, rebuild: true));
        };
        return Ui.Stack(8, Ui.Subtitle(S.Language_), Ui.Card(choices));
    }

    StackPanel AppearanceSection()
    {
        var choices = new RadioButtons();
        Ui.AutomationName(choices, S.Appearance);
        ThemeChoice[] values = [ThemeChoice.System, ThemeChoice.Light, ThemeChoice.Dark];
        choices.Items.Add(S.ThemeSystem);
        choices.Items.Add(S.ThemeLight);
        choices.Items.Add(S.ThemeDark);
        choices.SelectedIndex = Array.IndexOf(values, Launcher.Settings.Theme);
        choices.SelectionChanged += (_, _) =>
        {
            var value = values[Math.Max(0, choices.SelectedIndex)];
            if (value != Launcher.Settings.Theme) DispatcherQueue.TryEnqueue(() => Launcher.ChangeSettings(s => s with { Theme = value }, rebuild: true));
        };
        return Ui.Stack(8, Ui.Subtitle(S.Appearance), Ui.Card(choices));
    }

    StackPanel UpdatesSection()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Ui.Stack(4, Ui.Strong(S.CurrentVersion(Services.Launcher.Version.ToString())), updateText, updateDetail);
        grid.Children.Add(text);
        var buttons = Ui.Row(8, installButton, checkButton);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return Ui.Stack(8, Ui.Subtitle(S.LauncherUpdates), Ui.Card(grid));
    }

    /// <summary>Shows what the Launcher's update check last found.</summary>
    public void ShowUpdate()
    {
        var updates = Launcher.Updates;
        checkButton.IsEnabled = updates.State is UpdateState.Idle or UpdateState.Available or UpdateState.Failed;
        installButton.Visibility = updates.State is UpdateState.Available or UpdateState.Failed ? Visibility.Visible : Visibility.Collapsed;
        updateText.Text = updates.State switch
        {
            UpdateState.Checking => S.Checking,
            UpdateState.Available or UpdateState.Failed when updates.Available is { } update => S.NewLauncher(update.Version.ToString()),
            UpdateState.Downloading => $"{S.UpdateDownloading} {updates.Progress:P0}",
            UpdateState.Verifying or UpdateState.Installing => S.UpdateVerifying,
            UpdateState.WaitingForFlash => S.UpdateWaitsForFlash,
            _ when updates.LastCheckFailed => S.UpdateCheckFailed,
            _ when updates.CheckedAt is not null => S.UpToDate,
            _ => "",
        };
        var notes = updates.State is UpdateState.Available && updates.Available is { } available ? available.Manifest.NotesIn(S.Language) : null;
        var failure = updates.State == UpdateState.Failed ? S.UpdateFailed(updates.Failure ?? "") : null;
        var checkedAt = updates.CheckedAt is { } when ? S.LastChecked(when) : null;
        updateDetail.Text = string.Join("\n", new[] { failure, notes, checkedAt }.Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    StackPanel SystemSection()
    {
        var autostart = new ToggleSwitch { Header = S.StartWithWindows, IsOn = Autostart.Enabled };
        autostart.Toggled += (_, _) =>
        {
            try
            {
                Autostart.Set(autostart.IsOn);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                autostart.IsOn = Autostart.Enabled;
            }
        };
        var folder = new Grid { ColumnSpacing = 16 };
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Ui.Stack(2, Ui.Strong(S.DataFolder), Ui.Secondary(Launcher.Layout.Root, wrap: false));
        folder.Children.Add(text);
        var open = Ui.Button(S.OpenFolder, () => Services.Launcher.OpenInExplorer(Launcher.Layout.Root), "\uE838");
        open.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(open, 1);
        folder.Children.Add(open);
        return Ui.Stack(8, Ui.Card(autostart), Ui.Card(folder));
    }
}
