using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// Displays the third-party licence notices embedded in the executable.
    /// </summary>
    /// <remarks>
    /// This is a compliance requirement, not a courtesy. TDPdf ships as a single self-contained
    /// exe, so every dependency — PDFium and its eleven bundled libraries, Leptonica, Tesseract,
    /// PdfPig, PdfSharpCore, ImageSharp, OpenTelemetry and the .NET runtime packages — is
    /// physically inside the binary the user receives. MIT requires its notice be "included in
    /// all copies", BSD-3 requires reproduction "in the documentation or other materials provided
    /// with the distribution", and Apache-2.0 section 4(a) requires a copy of the licence itself.
    /// A binary-only download has no accompanying documentation, so the app itself is where those
    /// notices have to live.
    ///
    /// It is a separate window rather than an addition to About because About is a
    /// <c>TdpDialog</c>: 380px wide, SizeToContent, NoResize, no controls. The notices run to
    /// roughly 1,900 lines.
    /// </remarks>
    internal sealed class ThirdPartyLicensesWindow : Window
    {
        private const string ResourceName = "TDPdf.THIRD-PARTY-NOTICES.md";
        private readonly string _notices;

        internal ThirdPartyLicensesWindow(Window? owner)
        {
            _notices = LoadNotices();

            Owner = owner;
            Title = "Third-Party Licenses";
            Width = 860;
            Height = 660;
            MinWidth = 520;
            MinHeight = 360;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner;
            Background = Brush("BgDark", Colors.Black);
            Foreground = Brush("TextPrimary", Colors.White);
            Content = BuildContent();
        }

        /// <summary>
        /// Theme brush by key, falling back to a literal so the window still renders if a theme
        /// dictionary is missing — the same defensive lookup TdpDialog uses.
        /// </summary>
        private static SolidColorBrush Brush(string key, Color fallback)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b;
            }
            catch { /* resource lookup before the dictionaries load */ }
            var brush = new SolidColorBrush(fallback);
            brush.Freeze();
            return brush;
        }

        private static string LoadNotices()
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream);
                    string text = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch { /* fall through to the pointer below */ }

            // Never leave the user with an empty box: the obligation is to make the notices
            // reachable, so if the embedded copy is unreadable, say where to get it.
            return "The embedded third-party notices could not be read from this build.\r\n\r\n" +
                   "They are published with the source for every release:\r\n" +
                   "https://github.com/doodlemania2/TDPdf/blob/main/THIRD-PARTY-NOTICES.md";
        }

        private UIElement BuildContent()
        {
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new TextBlock
            {
                Text = "TDPdf is released under the GNU General Public License v3.0 and includes the "
                     + "third-party software listed below. Each component is reproduced with its "
                     + "copyright notice and licence text.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextSecondary", Colors.Gainsboro),
                Margin = new Thickness(0, 0, 0, 12),
            };
            Grid.SetRow(heading, 0);
            grid.Children.Add(heading);

            // Read-only TextBox rather than a TextBlock: it gives selection, keyboard scrolling and
            // Ctrl+F-free manual copying of any single licence without a dependency on a Markdown
            // renderer. IsReadOnly (not IsEnabled=false) keeps it selectable and correctly themed.
            var body = new TextBox
            {
                Text = _notices,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 12,
                Background = Brush("BgPanel", Color.FromRgb(0x24, 0x24, 0x24)),
                Foreground = Brush("TextPrimary", Colors.White),
                BorderBrush = Brush("BorderDim", Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
            };
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };

            var copy = MakeButton("Copy all");
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(_notices); }
                catch { /* clipboard momentarily locked by another app */ }
            };
            buttons.Children.Add(copy);

            var close = MakeButton("Close");
            close.IsDefault = true;
            close.IsCancel = true;
            close.Margin = new Thickness(8, 0, 0, 0);
            close.Click += (_, _) => Close();
            buttons.Children.Add(close);

            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            return grid;
        }

        private Button MakeButton(string text) => new()
        {
            Content = text,
            MinWidth = 92,
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Background = Brush("BgHover", Color.FromRgb(0x2e, 0x2e, 0x2e)),
            Foreground = Brush("TextPrimary", Colors.White),
            BorderBrush = Brush("BorderDim", Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
        };

        /// <summary>Opens the licences window modally over <paramref name="owner"/>.</summary>
        internal static void Show(Window? owner)
        {
            var win = new ThirdPartyLicensesWindow(owner);
            if (owner is null) win.ShowDialog();
            else win.ShowDialog();
        }
    }
}
