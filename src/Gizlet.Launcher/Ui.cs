using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Gizlet.Launcher;

/// <summary>Small builders for a code-only WinUI interface, using the Fluent theme resources.</summary>
static class Ui
{
    /// <summary>A style or other app-wide resource. Theme brushes go through <see cref="Themed{T}"/>.</summary>
    public static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    const string Presentation = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    /// <summary>
    /// An element whose brushes are {ThemeResource} references, so they follow the light or dark
    /// theme of the window it is shown in and change with it. A theme brush cannot be read from the
    /// application's resources in code.
    /// </summary>
    static T Themed<T>(string element, string brushes) where T : DependencyObject =>
        (T)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<{element} {Presentation} {brushes} />");

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", bool wrap = true) => new()
    {
        Text = text,
        Style = Resource<Style>(style),
        TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Secondary(string text, string style = "CaptionTextBlockStyle", bool wrap = true)
    {
        var block = Themed<TextBlock>("TextBlock", "Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"");
        block.Text = text;
        block.Style = Resource<Style>(style);
        block.TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
        block.TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        return block;
    }

    public static TextBlock Title(string text) => Text(text, "TitleTextBlockStyle");

    public static TextBlock Subtitle(string text) => Text(text, "SubtitleTextBlockStyle");

    public static TextBlock Strong(string text, bool wrap = true) => Text(text, "BodyStrongTextBlockStyle", wrap);

    public static FontIcon Glyph(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    public static StackPanel Stack(double spacing, params UIElement[] children) => Stack(Orientation.Vertical, spacing, children);

    public static StackPanel Row(double spacing, params UIElement[] children) => Stack(Orientation.Horizontal, spacing, children);

    static StackPanel Stack(Orientation orientation, double spacing, UIElement[] children)
    {
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        if (orientation == Orientation.Horizontal)
        {
            foreach (var child in children)
            {
                if (child is FrameworkElement element && element.VerticalAlignment == VerticalAlignment.Stretch) element.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        return panel;
    }

    /// <summary>A Fluent settings card: a rounded, raised surface.</summary>
    public static Border Card(UIElement child, double padding = 16)
    {
        var card = Themed<Border>("Border", "Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\"");
        card.Child = child;
        card.Padding = new Thickness(padding);
        card.CornerRadius = new CornerRadius(8);
        card.BorderThickness = new Thickness(1);
        return card;
    }

    public static Button Button(string text, Action click, string? glyph = null, bool accent = false)
    {
        var button = new Button
        {
            Content = glyph is null ? text : Row(8, Glyph(glyph, 14), new TextBlock { Text = text }),
        };
        if (accent) button.Style = Resource<Style>("AccentButtonStyle");
        AutomationName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    public static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    public static global::Windows.UI.Color? Color(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        return ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>An app's icon on its own colour, or its initial when it has none.</summary>
    public static Border AppIcon(Uri? icon, string? color, string name, double size)
    {
        var background = Color(color) ?? ColorHelper.FromArgb(255, 0x37, 0x49, 0x78);
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 5),
            Background = new SolidColorBrush(background),
        };
        if (icon is not null)
        {
            border.Child = new Image
            {
                Source = new BitmapImage(icon) { DecodePixelWidth = (int)(size * 2), DecodePixelType = DecodePixelType.Logical },
                Stretch = Stretch.UniformToFill,
            };
        }
        else
        {
            border.Child = new TextBlock
            {
                Text = name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "?",
                FontSize = size / 2,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return border;
    }

    public static Border Badge(string text, bool accent = false)
    {
        var label = Themed<TextBlock>("TextBlock", $"Foreground=\"{{ThemeResource {(accent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush")}}}\"");
        label.Text = text;
        label.FontSize = 12;
        var badge = Themed<Border>("Border", $"Background=\"{{ThemeResource {(accent ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush")}}}\"");
        badge.Child = label;
        badge.Padding = new Thickness(8, 2, 8, 2);
        badge.CornerRadius = new CornerRadius(10);
        badge.VerticalAlignment = VerticalAlignment.Center;
        return badge;
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    /// <summary>A page's padding and maximum width, as Windows' own settings pages use.</summary>
    public static FrameworkElement Page(UIElement content)
    {
        var holder = new Grid { Padding = new Thickness(32, 16, 32, 32), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        holder.Children.Add(content);
        return Scroll(holder);
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string body, string primary, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = Text(body),
            PrimaryButtonText = primary,
            CloseButtonText = S.Cancel,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task AlertAsync(XamlRoot root, string title, string body)
    {
        var dialog = new ContentDialog { XamlRoot = root, Title = title, Content = Text(body), CloseButtonText = S.Close, DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    public static async Task<string?> AskAsync(XamlRoot root, string title, string label, string initial, string primary)
    {
        var box = new TextBox { Header = label, Text = initial, MaxLength = 40 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = S.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}

/// <summary>A page the main window shows; built again when the profile, language or theme changes.</summary>
abstract class LauncherPage : UserControl
{
    protected LauncherPage(Services.Launcher launcher) => Launcher = launcher;

    protected Services.Launcher Launcher { get; }

    protected static MainWindow Window => App.Current!.Window!;

    /// <summary>F5.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Ctrl+F.</summary>
    public virtual bool FocusSearch() => false;

    /// <summary>The page is shown again after another one.</summary>
    public virtual void Shown()
    {
    }

    public virtual void Hidden()
    {
    }
}
