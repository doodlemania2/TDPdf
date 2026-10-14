using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// The themed building blocks the Electronic Signature windows share: the custom-chrome shell
    /// (the recipe of <c>TdpDialog.CreateShell</c> / <c>MainWindow.OpenSignatureCreator</c>),
    /// brushes with a system-colour fallback, and the few controls those windows are made of.
    /// </summary>
    /// <remarks>
    /// Styles such as <c>DarkButton</c> and <c>DarkComboBox</c> live in MainWindow.xaml's own
    /// resources, not the application's, so a window built here cannot find them through its own
    /// tree or through <c>Application.Current</c>; they are looked up through the owner. Brushes are
    /// in the theme dictionaries at application level, which is why <see cref="Brush"/> can use
    /// <c>Application.Current</c> — and falls back to a system colour rather than throwing the way
    /// <c>FindResource</c> would if a theme is ever missing a key.
    /// </remarks>
    internal static class DocumensoUi
    {
        internal static SolidColorBrush Brush(string key)
            => Application.Current?.TryFindResource(key) as SolidColorBrush ?? SystemBrush(key);

        private static SolidColorBrush SystemBrush(string key) => key switch
        {
            "AccentGreen" or "AccentGreenDim" or "DangerRed" or "WarningOrange" => SystemColors.HighlightBrush,
            "BgDark" or "BgPanel" => SystemColors.WindowBrush,
            "BgHover" => SystemColors.ControlBrush,
            "TextSecondary" or "TextMuted" => SystemColors.GrayTextBrush,
            _ => SystemColors.WindowTextBrush,
        };

        /// <summary>A style from the owner's resources (where MainWindow keeps them), else the app's.</summary>
        internal static Style? StyleFrom(Window? owner, string key)
            => owner?.TryFindResource(key) as Style ?? Application.Current?.TryFindResource(key) as Style;

        /// <summary>
        /// Theme brushes that tell recipients apart, on the dialog and on the canvas alike: the
        /// shortcut-overlay category colours, which every theme defines (High Contrast maps them all
        /// to the system hot-track colour, where the label text then does the telling apart).
        /// Red is left out on purpose — on the canvas it means a pending redaction.
        /// </summary>
        private static readonly string[] RecipientKeys =
            { "KsCatFile", "KsCatTools", "KsCatEdit", "KsCatNav", "KsCatView", "KsCatSearch", "KsCatHelp" };

        internal static SolidColorBrush RecipientBrush(int index)
            => Brush(RecipientKeys[((index % RecipientKeys.Length) + RecipientKeys.Length) % RecipientKeys.Length]);

        /// <summary>
        /// A borderless themed window: rounded accent border, draggable Consolas title bar, MDL2
        /// close glyph. Returns the window and the stack its content goes in (below the title bar).
        /// </summary>
        internal static (Window Window, StackPanel Root, Button Close) CreateShell(Window? owner, string title, double width)
        {
            var win = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                Owner = owner,
                WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };

            var outer = new Border
            {
                Background = Brush("BgDark"),
                BorderBrush = Brush("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
            };
            var root = new StackPanel();
            outer.Child = root;
            win.Content = outer;

            var titleBar = new Border
            {
                Background = Brush("BgPanel"),
                Padding = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var close = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                Foreground = Brush("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close",
            };
            System.Windows.Automation.AutomationProperties.SetName(close, "Close");
            close.MouseEnter += (_, _) => close.Foreground = Brush("DangerRed");
            close.MouseLeave += (_, _) => close.Foreground = Brush("TextSecondary");
            Grid.SetColumn(close, 1);
            grid.Children.Add(close);
            titleBar.Child = grid;
            root.Children.Add(titleBar);
            return (win, root, close);
        }

        internal static TextBlock Para(string text, string brushKey, double size, Thickness margin, bool semiBold = false)
            => new()
            {
                Text = text,
                Foreground = Brush(brushKey),
                FontSize = size,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin,
                FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            };

        internal static Button MakeButton(Window? owner, string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
            };
            if (StyleFrom(owner, "DarkButton") is { } style) b.Style = style;
            if (primary)
            {
                b.Background = Brush("AccentGreenDim");
                b.Foreground = Brush("AccentGreen");
                b.BorderBrush = Brush("AccentGreen");
                b.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                b.Background = Brush("BgHover");
                b.Foreground = Brush("TextPrimary");
                b.BorderBrush = Brush("BorderDim");
            }
            return b;
        }

        /// <summary>A square MDL2 glyph button (up, down, remove) with an accessible name.</summary>
        internal static Button GlyphButton(Window? owner, string glyph, string name)
        {
            var b = MakeButton(owner, glyph, primary: false);
            b.FontFamily = new FontFamily("Segoe MDL2 Assets");
            b.FontSize = 10;
            b.Padding = new Thickness(0);
            b.Width = 26;
            b.Height = 26;
            b.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            return b;
        }

        internal static TextBox MakeTextBox(string text, string accessibleName, int maxLength)
        {
            var t = new TextBox
            {
                Text = text,
                MaxLength = maxLength,
                Background = Brush("BgPanel"),
                Foreground = Brush("TextPrimary"),
                BorderBrush = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                CaretBrush = Brush("TextPrimary"),
                SelectionBrush = Brush("AccentGreen"),
                Padding = new Thickness(5, 3, 5, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(t, accessibleName);
            return TdpDialog.UseThemedTextMenu(t);
        }

        internal static ComboBox MakeCombo(Window? owner, string accessibleName)
        {
            var c = new ComboBox();
            if (StyleFrom(owner, "DarkComboBox") is { } style) c.Style = style;
            else
            {
                c.Foreground = Brush("TextPrimary");
                c.Background = Brush("BgPanel");
                c.BorderBrush = Brush("BorderDim");
            }
            System.Windows.Automation.AutomationProperties.SetName(c, accessibleName);
            return c;
        }

        internal static CheckBox MakeCheckBox(string text, bool isChecked) => new()
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = Brush("TextPrimary"),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        /// <summary>A compact secondary button for per-row actions in a list.</summary>
        internal static Button SmallButton(Window? owner, string text, string? accessibleName = null)
        {
            var b = MakeButton(owner, text, primary: false);
            b.Padding = new Thickness(8, 2, 8, 2);
            b.FontSize = 11;
            b.Margin = new Thickness(0, 0, 6, 4);
            if (accessibleName is not null) System.Windows.Automation.AutomationProperties.SetName(b, accessibleName);
            return b;
        }

        /// <summary>
        /// Opens an http(s) address in the default browser. Anything else — file:, a UNC path, a
        /// script scheme — is refused rather than handed to the shell. False when nothing opened.
        /// </summary>
        internal static bool OpenInBrowser(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
                || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A section heading inside a dialog body.</summary>
        internal static TextBlock Heading(string text, Thickness margin)
            => Para(text, "TextPrimary", 12, margin, semiBold: true);
    }
}
