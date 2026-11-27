using Gizlet.Core;
using Gizlet.Core.Flash;
using Gizlet.Launcher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Gizlet.Launcher.Pages;

/// <summary>
/// Writes Gizlet OS to an R36S card. The card list follows readers and cards coming and going;
/// the confirmation names the card that will be erased, and a card taken out, put back or swapped
/// since then needs confirming again. Writing happens in the elevated helper, which checks again.
/// </summary>
sealed class FlashPage : LauncherPage
{
    readonly StackPanel cards = new() { Spacing = 4 };
    readonly RadioButtons imageChoice = new();
    readonly TextBlock localName = Ui.Secondary(S.NoFileChosen);
    readonly RadioButtons panelChoice = new();
    readonly TextBlock panelNote = Ui.Secondary("", "BodyTextBlockStyle");
    readonly StackPanel panelSection;
    readonly Button writeButton;
    readonly Button cancelButton;
    readonly ProgressBar progress = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock progressText = Ui.Text("");
    readonly InfoBar status = new() { IsClosable = true };
    readonly DispatcherQueueTimer timer;

    IReadOnlyList<DiskInfo> disks = [];
    DiskInfo? selected;
    PanelGuess? guess;
    string? localImage;
    OsRelease? release;
    CancellationTokenSource? running;
    bool listing;

    public FlashPage(Services.Launcher launcher) : base(launcher)
    {
        imageChoice.Items.Add(S.OfficialImage);
        imageChoice.Items.Add(S.LocalImage);
        imageChoice.SelectedIndex = 0;
        Ui.AutomationName(imageChoice, S.ChooseImage);
        imageChoice.SelectionChanged += (_, _) => Update();
        var browse = Ui.Button(S.Browse, () => _ = BrowseAsync(), "\uE8E5");

        panelChoice.Items.Add(S.Panel4);
        panelChoice.Items.Add(S.Panel0);
        panelChoice.SelectedIndex = -1;
        Ui.AutomationName(panelChoice, S.ChoosePanel);
        panelChoice.SelectionChanged += (_, _) => Update();
        panelSection = Ui.Stack(8, Ui.Subtitle(S.ChoosePanel), Ui.Card(Ui.Stack(8, panelNote, panelChoice)));

        writeButton = Ui.Button(S.WriteCard, () => _ = WriteAsync(), "\uE88E", accent: true);
        cancelButton = Ui.Button(S.Cancel, () => running?.Cancel());
        cancelButton.Visibility = Visibility.Collapsed;

        var refresh = Ui.Button(S.Refresh, () => _ = ListAsync(), "\uE72C");
        var cardHeader = new Grid();
        cardHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardHeader.Children.Add(Ui.Subtitle(S.ChooseCard));
        Grid.SetColumn(refresh, 1);
        cardHeader.Children.Add(refresh);

        Content = Ui.Page(Ui.Stack(24,
            Ui.Stack(4, Ui.Title(S.Flash), Ui.Secondary(S.FlashIntro, "BodyTextBlockStyle")),
            Ui.Stack(8, cardHeader, cards),
            Ui.Stack(8, Ui.Subtitle(S.ChooseImage), Ui.Card(Ui.Stack(8, imageChoice, Ui.Row(12, browse, localName)))),
            panelSection,
            status,
            Ui.Stack(8, progressText, progress),
            Ui.Row(8, writeButton, cancelButton)));

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _ = ListAsync();
        _ = LoadReleaseAsync();
        Update();
    }

    public override void Shown()
    {
        timer.Start();
        _ = ListAsync();
    }

    public override void Hidden() => timer.Stop();

    public override void Refresh() => _ = ListAsync();

    bool Official => imageChoice.SelectedIndex == 0;

    R36sPanel? Panel => panelChoice.SelectedIndex switch
    {
        0 => R36sPanel.V30Panel4,
        1 => R36sPanel.Original,
        _ => null,
    };

    async Task LoadReleaseAsync()
    {
        try
        {
            release = await Launcher.Flash.Images.LatestAsync();
            imageChoice.Items[0] = S.OfficialImageVersion(release.Tag);
        }
        catch (Exception e) when (e is HttpRequestException or GizletException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
        }
    }

    /// <summary>Lists the card readers again, keeping the choice only while it is the same card.</summary>
    async Task ListAsync()
    {
        if (listing || Launcher.Flash.Busy) return;
        listing = true;
        try
        {
            var found = await FlashSession.CardsAsync();
            var same = found.Count == disks.Count && found.Zip(disks).All(p => DiskIdentity.Of(p.First).SameAs(DiskIdentity.Of(p.Second))
                && p.First.VolumeNames == p.Second.VolumeNames);
            if (same) return;
            disks = found;
            var keep = selected is null ? null : disks.FirstOrDefault(d => DiskIdentity.Of(d).SameAs(DiskIdentity.Of(selected)));
            if (keep is null && selected is not null)
            {
                selected = null;
                guess = null;
                panelChoice.SelectedIndex = -1;
            }
            if (keep is null && disks.Count > 0 && selected is null) await SelectAsync(disks[0]);
            else selected = keep;
            BuildCards();
        }
        catch (Exception e)
        {
            App.Log(e);
        }
        finally
        {
            listing = false;
            Update();
        }
    }

    async Task SelectAsync(DiskInfo disk)
    {
        selected = disk;
        guess = await FlashSession.PanelAsync(disk);
        panelChoice.SelectedIndex = guess?.Panel switch
        {
            R36sPanel.V30Panel4 => 0,
            R36sPanel.Original => 1,
            _ => -1,
        };
        BuildCards();
        Update();
    }

    void BuildCards()
    {
        cards.Children.Clear();
        if (disks.Count == 0)
        {
            cards.Children.Add(Ui.Card(Ui.Row(12, Ui.Glyph("\uE88E", 20), Ui.Text(S.NoCards)), 20));
            return;
        }
        foreach (var disk in disks)
        {
            var radio = new RadioButton
            {
                GroupName = "card",
                IsChecked = selected is not null && DiskIdentity.Of(disk).SameAs(DiskIdentity.Of(selected)),
                Content = Describe(disk),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Ui.AutomationName(radio, $"{S.Disk(disk.Number)} {disk.Reader} {S.Size(disk.Size)}");
            radio.Checked += (_, _) =>
            {
                if (selected is null || !DiskIdentity.Of(disk).SameAs(DiskIdentity.Of(selected))) _ = SelectAsync(disk);
            };
            cards.Children.Add(Ui.Card(radio, 12));
        }
    }

    static StackPanel Describe(DiskInfo disk)
    {
        var title = Ui.Row(8, Ui.Strong($"{S.Disk(disk.Number)} · {S.Size(disk.Size)}", wrap: false), Ui.Secondary(disk.Reader, "BodyTextBlockStyle", wrap: false));
        var names = disk.VolumeNames is { Length: > 0 } v ? v : S.NoVolumeName;
        var panel = Ui.Stack(4, title, Ui.Secondary(names, wrap: false));
        var badges = Ui.Row(6);
        if (disk.HasGizletOs) badges.Children.Add(Ui.Badge(S.HasGizlet));
        if (disk.LooksLikeStockCard) badges.Children.Add(Ui.Badge(S.StockCard));
        if (disk.Size > DiskRules.LargeCard) badges.Children.Add(Ui.Badge(S.LargeDisk, accent: true));
        if (badges.Children.Count > 0) panel.Children.Add(badges);
        return panel;
    }

    void Update()
    {
        var busy = running is not null;
        panelSection.Visibility = Official ? Visibility.Visible : Visibility.Collapsed;
        localName.Text = localImage is null ? (Official ? "" : S.NoFileChosen) : Path.GetFileName(localImage);
        panelNote.Text = guess?.Source switch
        {
            PanelSource.Stock => S.PanelFromStock,
            PanelSource.Gizlet => S.PanelFromGizlet,
            _ => S.PanelUnknown,
        };
        if (!Official) panelNote.Text = S.LocalImagePanel;
        var ready = selected is not null && (Official ? Panel is not null : localImage is not null);
        writeButton.IsEnabled = ready && !busy;
        imageChoice.IsEnabled = panelChoice.IsEnabled = cards.IsHitTestVisible = !busy;
        cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task BrowseAsync()
    {
        var picker = new FileOpenPicker(Window.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".img");
        picker.FileTypeFilter.Add(".gz");
        var file = await picker.PickSingleFileAsync();
        if (file?.Path is not { Length: > 0 } path) return;
        localImage = path;
        imageChoice.SelectedIndex = 1;
        Update();
    }

    void Report(string text, double? fraction)
    {
        progressText.Text = text;
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = fraction is null;
        if (fraction is { } f) progress.Value = f * 100;
    }

    void Show(InfoBarSeverity severity, string message)
    {
        status.Severity = severity;
        status.Message = message;
        status.IsOpen = true;
    }

    async Task WriteAsync()
    {
        if (selected is null || running is not null) return;
        running = new CancellationTokenSource();
        var token = running.Token;
        status.IsOpen = false;
        timer.Stop();
        Update();
        try
        {
            // 1. The image, before anything is asked, so the confirmation is the last step.
            Report(S.PreparingImage, null);
            var stage = new Progress<(string Stage, double Fraction)>(p => Report(S.PreparingImage, p.Fraction));
            CardImage image;
            if (Official)
            {
                release ??= await Launcher.Flash.Images.LatestAsync(token);
                image = await Launcher.Flash.Images.DownloadAsync(release, stage, token);
            }
            else
            {
                image = await Launcher.Flash.Images.LocalAsync(localImage!, stage, token);
            }

            // 2. The card as it is now, confirmed by typing its name.
            var now = (await FlashSession.CardsAsync()).FirstOrDefault(d => DiskIdentity.Of(d).SameAs(DiskIdentity.Of(selected)));
            if (now is null) throw new GizletException(GizletError.DiskChanged, "before confirmation");
            if (DiskRules.Refusal(now, image.Size) is not null) throw new GizletException(GizletError.ImageTooLarge, $"{image.Size} bytes");
            progress.Visibility = Visibility.Collapsed;
            progressText.Text = "";
            if (!await ConfirmAsync(now)) throw new OperationCanceledException(S.NothingWritten);

            // 3. Taken out, put back or swapped during the dialog: confirm again.
            var confirmed = DiskIdentity.Of(now);
            var after = (await FlashSession.CardsAsync()).FirstOrDefault(d => d.Number == now.Number);
            if (after is null || !DiskIdentity.Of(after).SameAs(confirmed))
            {
                Show(InfoBarSeverity.Warning, S.CardChanged);
                return;
            }

            // 4. The elevated helper checks it all again and writes.
            await Launcher.Flash.WriteAsync(confirmed, image, image.Official && Panel == R36sPanel.V30Panel4, flashEvent => DispatcherQueue.TryEnqueue(() =>
            {
                double? fraction = flashEvent.Total > 0 ? (double)flashEvent.Done / flashEvent.Total : null;
                switch (flashEvent.Stage)
                {
                    case FlashStage.Checking: Report(S.StageChecking, fraction); break;
                    case FlashStage.Writing: Report($"{S.StageWriting} {S.Size(flashEvent.Done)} / {S.Size(flashEvent.Total)}", fraction); cancelButton.IsEnabled = false; break;
                    case FlashStage.Verifying: Report($"{S.StageVerifying} {S.Size(flashEvent.Done)} / {S.Size(flashEvent.Total)}", fraction); break;
                    case FlashStage.Configuring: Report(S.StageConfiguring, null); break;
                }
            }), () => DispatcherQueue.TryEnqueue(() => Report(S.AskingPermission, null)), token);
            progress.Visibility = Visibility.Collapsed;
            progressText.Text = "";
            Show(InfoBarSeverity.Success, S.FlashDone);
        }
        catch (Panel4Exception)
        {
            progress.Visibility = Visibility.Collapsed;
            progressText.Text = "";
            Show(InfoBarSeverity.Warning, S.Panel4Manual);
        }
        catch (OperationCanceledException e)
        {
            progress.Visibility = Visibility.Collapsed;
            progressText.Text = "";
            Show(InfoBarSeverity.Warning, string.IsNullOrEmpty(e.Message) || e.Message == new OperationCanceledException().Message ? S.NothingWritten : e.Message);
        }
        catch (Exception e)
        {
            progress.Visibility = Visibility.Collapsed;
            progressText.Text = "";
            Show(InfoBarSeverity.Error, S.FlashFailed(S.Error(e)));
        }
        finally
        {
            running.Dispose();
            running = null;
            cancelButton.IsEnabled = true;
            disks = [];
            timer.Start();
            _ = ListAsync();
            Update();
        }
    }

    /// <summary>Names the card, warns about large and stock cards, and asks for its name typed out.</summary>
    async Task<bool> ConfirmAsync(DiskInfo disk)
    {
        var name = S.Disk(disk.Number);
        var body = Ui.Stack(12, Ui.Text(S.EraseBody), Ui.Card(Describe(disk), 12));
        if (disk.Size > DiskRules.LargeCard) body.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, Message = S.LargeDisk, IsOpen = true, IsClosable = false });
        if (disk.LooksLikeStockCard) body.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, Message = S.StockCard, IsOpen = true, IsClosable = false });
        var typed = new TextBox { Header = S.TypeToConfirm(name), PlaceholderText = name };
        body.Children.Add(typed);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = S.EraseTitle,
            Content = body,
            PrimaryButtonText = S.Erase,
            CloseButtonText = S.Cancel,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        typed.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(typed.Text.Trim(), name, StringComparison.OrdinalIgnoreCase);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            && string.Equals(typed.Text.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }
}
