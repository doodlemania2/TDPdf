// TdpDialog — the dark-themed replacement for MessageBox used throughout TDPdf.
// Extracted verbatim from MainWindow.xaml.cs; it is a separate top-level type that
// merely shared that file, not part of MainWindow.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using TDPdf.Diagnostics;
using TDPdf.Services;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class TdpDialog
    {
        private static SolidColorBrush Brush(string key)
        {
            return Application.Current?.TryFindResource(key) as SolidColorBrush
                ?? SystemBrush(key);
        }

        private static SolidColorBrush SystemBrush(string key)
        {
            return key switch
            {
                "AccentGreen" => SystemColors.HighlightBrush,
                "AccentGreenDim" => SystemColors.HighlightBrush,
                "DangerRed" => SystemColors.HighlightBrush,
                "BgDark" => SystemColors.WindowBrush,
                "BgPanel" => SystemColors.WindowBrush,
                "BgHover" => SystemColors.ControlBrush,
                "BgPressed" => SystemColors.ControlDarkBrush,
                "BorderDim" => SystemColors.WindowTextBrush,
                "TextSecondary" => SystemColors.WindowTextBrush,
                _ => SystemColors.WindowTextBrush
            };
        }

        private static SolidColorBrush FrozenSolidColorBrush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The shared TDPdf dialog shell: a borderless, transparent-background window (no OS title
        /// bar) holding a rounded panel with an accent border and a draggable Consolas wordmark
        /// title bar. Returns the window plus the vertical stack each dialog fills with its body.
        /// </summary>
        private static (Window Window, StackPanel Body) CreateShell(Window? owner, string title)
        {
            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };

            var outerBorder = new Border
            {
                Background      = Brush("BgDark"),
                BorderBrush     = Brush("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var root = new StackPanel();

            var titleBar = new Border
            {
                Background   = Brush("BgPanel"),
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock
            {
                Text       = title,
                Foreground = Brush("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new System.Windows.Media.FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            outerBorder.Child = root;
            win.Content = outerBorder;
            return (win, root);
        }

        // Flat, themed button chrome. Replaces the stock WPF template so no default blue Aero
        // hover/focus chrome bleeds through onto a dark dialog.
        private static ControlTemplate MakeBtnTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            bf.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }

        // Themed PasswordBox chrome: our panel fill and dim border instead of the OS white box
        // with its blue focus ring. PART_ContentHost is the contract name WPF looks for.
        private static ControlTemplate MakePasswordFieldTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            bf.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty,
                new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty,
                new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(Control.PaddingProperty, new Thickness(0));
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            bf.AppendChild(host);
            return new ControlTemplate(typeof(PasswordBox)) { VisualTree = bf };
        }

        /// <summary>
        /// Themed "Password Required" prompt: the family dialog chrome around a themed PasswordBox.
        /// Returns the entered password, or <c>null</c> if the user cancelled or closed the dialog.
        /// </summary>
        /// <summary>
        /// Asks for a block of free text — one item per line. Null means cancelled.
        /// </summary>
        /// <remarks>
        /// A plain multi-line box rather than an add/remove list editor, deliberately. The thing
        /// being collected is a short list of choices, and typing three lines is faster than three
        /// rounds of "click +, type, click +". Enter inserts a newline, so the accept button is not
        /// IsDefault here — Ctrl+Enter accepts instead, and Esc still cancels.
        /// </remarks>
        public static string? PromptMultiline(Window? owner, string title, string prompt, string initial = "")
            => PromptCore(owner, title, prompt, initial, multiline: true);

        /// <summary>Asks for a single line of text. Null means cancelled.</summary>
        public static string? PromptText(Window? owner, string title, string prompt, string initial = "")
            => PromptCore(owner, title, prompt, initial, multiline: false);

        private static string? PromptCore(Window? owner, string title, string prompt, string initial, bool multiline)
        {
            string? result = null;
            var text  = Brush("TextPrimary");
            var green = Brush("AccentGreen");

            var (win, root) = CreateShell(owner, title);

            root.Children.Add(new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock
                {
                    Text = prompt,
                    Foreground = text,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

            var box = new TextBox
            {
                Text            = initial,
                AcceptsReturn   = multiline,
                MinLines        = multiline ? 4 : 1,
                MaxLines        = multiline ? 12 : 1,
                FontSize        = 12,
                Background      = Brush("BgPanel"),
                Foreground      = text,
                BorderBrush     = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
                CaretBrush      = text,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AutomationProperties.SetName(box, prompt);
            root.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = box });

            if (multiline)
                root.Children.Add(new Border
                {
                    Padding = new Thickness(20, 0, 20, 0),
                    Child = new TextBlock
                    {
                        // Enter has to insert a newline in a multi-line box, so it cannot also be
                        // the accept key — say which one is.
                        Text = "Ctrl+Enter to accept",
                        Foreground = Brush("TextSecondary"),
                        FontSize = 11,
                    },
                });

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            Button MakeBtn(string label, bool accent)
            {
                var bgNorm = accent ? Brush("AccentGreenDim") : Brush("BgPanel");
                var bgHov  = accent ? Brush("BgPressed") : Brush("BgHover");
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? green : text,
                    BorderBrush     = accent ? green : Brush("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate(),
                };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            var okBtn = MakeBtn("OK", accent: true);
            okBtn.IsDefault = !multiline;
            okBtn.Click += (_, _2) => { result = box.Text; win.Close(); };
            var cancelBtn = MakeBtn("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => { result = null; win.Close(); };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });

            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (multiline && Keyboard.Modifiers != ModifierKeys.Control) return;
                result = box.Text;
                win.Close();
            };

            win.Loaded += (_, _2) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();
            return result;
        }

        public static string? PromptPassword(Window? owner, string filename)
        {
            string? result = null;
            var text  = Brush("TextPrimary");
            var green = Brush("AccentGreen");

            var (win, root) = CreateShell(owner, "TDPdf");

            var message = new TextBlock
            {
                Foreground   = text,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap
            };
            message.Inlines.Add(new System.Windows.Documents.Run(
                $"“{System.IO.Path.GetFileName(filename)}” ") { FontWeight = FontWeights.SemiBold });
            message.Inlines.Add(new System.Windows.Documents.Run("is password protected."));
            root.Children.Add(new Border { Padding = new Thickness(20, 16, 20, 10), Child = message });

            var pwBox = new PasswordBox
            {
                FontSize        = 12,
                Background      = Brush("PanelBackground"),
                Foreground      = text,
                BorderBrush     = Brush("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
                CaretBrush      = text,
                Template        = MakePasswordFieldTemplate()
            };
            AutomationProperties.SetName(pwBox, "Password");
            AutomationProperties.SetHelpText(pwBox, "Password for the protected PDF");
            root.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = pwBox });

            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button MakeBtn(string label, bool accent)
            {
                var bgNorm = accent ? Brush("AccentGreenDim") : Brush("BgPanel");
                var bgHov  = accent ? Brush("BgPressed") : Brush("BgHover");
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? green : text,
                    BorderBrush     = accent ? green : Brush("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate()
                };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            var openBtn = MakeBtn("Open", accent: true);
            openBtn.IsDefault = true;
            openBtn.Click += (_, _2) => { result = pwBox.Password; win.Close(); };
            var cancelBtn = MakeBtn("Cancel", accent: false);
            cancelBtn.IsCancel = true;   // Esc closes the prompt, leaving result null
            cancelBtn.Click += (_, _2) => { result = null; win.Close(); };
            btnPanel.Children.Add(openBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });

            // Enter submits from inside the field as well (IsDefault covers the rest of the dialog).
            pwBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = pwBox.Password; win.Close(); } };

            win.Loaded += (_, _2) => pwBox.Focus();
            win.ShowDialog();
            return result;
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "TDPdf",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(owner, message, title, buttons, image, null, null, null).result;

        public static MessageBoxResult ShowYesNo(
            Window? owner,
            string message,
            string yesLabel,
            string noLabel,
            string title = "TDPdf",
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(
                owner,
                message,
                title,
                MessageBoxButton.YesNo,
                image,
                null,
                yesLabel,
                noLabel).result;

        // Same themed dialog as Show, plus a single opt-out checkbox below the message (e.g. "Don't ask
        // again"). Returns the button result together with whether the checkbox was ticked.
        public static (MessageBoxResult result, bool ticked) ShowWithCheckbox(
            Window? owner,
            string message,
            string checkboxLabel,
            string title = "TDPdf",
            MessageBoxButton buttons = MessageBoxButton.OKCancel,
            MessageBoxImage image = MessageBoxImage.None)
            => ShowCore(owner, message, title, buttons, image, checkboxLabel, null, null);

        private static (MessageBoxResult result, bool ticked) ShowCore(
            Window? owner,
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage image,
            string? checkboxLabel,
            string? yesLabel,
            string? noLabel)
        {
            var result = MessageBoxResult.OK;
            bool ticked = false;
            var green = Brush("AccentGreen");
            var panel = Brush("BgPanel");
            var text = Brush("TextPrimary");
            var border = Brush("BorderDim");
            var greenDim = Brush("AccentGreenDim");
            var greenHov = Brush("BgPressed");
            var hover = Brush("BgHover");
            var danger = Brush("DangerRed");
            var warning = Brush("WarningOrange");

            var (win, root) = CreateShell(owner, title);

            // Message body: icon column + wrapped message text.
            var msgGrid = new Grid { Margin = new Thickness(20, 16, 20, 8) };
            msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            msgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (TryGetMessageBoxGlyph(image, green, warning, danger, out var glyphChar, out var glyphBrush))
            {
                var glyph = new TextBlock
                {
                    Text       = glyphChar,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize   = 28,
                    Foreground = glyphBrush,
                    VerticalAlignment   = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin              = new Thickness(0, 0, 14, 0)
                };
                Grid.SetColumn(glyph, 0);
                msgGrid.Children.Add(glyph);
            }

            var msgText = new TextBlock
            {
                Text         = message,
                Foreground   = text,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(msgText, 1);
            msgGrid.Children.Add(msgText);
            root.Children.Add(msgGrid);

            // Optional opt-out checkbox, aligned under the message text (past the icon column).
            if (checkboxLabel != null)
            {
                var check = new CheckBox
                {
                    Content    = checkboxLabel,
                    Foreground = text,
                    FontSize   = 12,
                    Margin     = new Thickness(20, 4, 20, 4)
                };
                check.Checked   += (_, _2) => ticked = true;
                check.Unchecked += (_, _2) => ticked = false;
                root.Children.Add(check);
            }

            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button MakeBtn(string label, MessageBoxResult res, bool accent = false, bool isDefault = false, bool isCancel = false)
            {
                var bgNorm = accent ? greenDim : panel;
                var bgHov  = accent ? greenHov : hover;
                var btn = new Button
                {
                    Content         = label,
                    Padding         = new Thickness(18, 6, 18, 6),
                    Margin          = new Thickness(8, 0, 0, 0),
                    Background      = bgNorm,
                    Foreground      = accent ? green : text,
                    BorderBrush     = accent ? green : border,
                    BorderThickness = new Thickness(1),
                    Cursor          = Cursors.Hand,
                    FontSize        = 12,
                    Template        = MakeBtnTemplate(),
                    IsDefault       = isDefault,
                    IsCancel        = isCancel
                };
                btn.Click      += (_, _2) => { result = res; win.Close(); };
                btn.MouseEnter += (_, _2) => btn.Background = bgHov;
                btn.MouseLeave += (_, _2) => btn.Background = bgNorm;
                return btn;
            }

            Button? defaultBtn = null;
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    defaultBtn = MakeBtn("OK", MessageBoxResult.OK, accent: true, isDefault: true, isCancel: true);
                    btnPanel.Children.Add(defaultBtn);
                    break;
                case MessageBoxButton.OKCancel:
                    defaultBtn = MakeBtn("OK", MessageBoxResult.OK, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel, isCancel: true));
                    break;
                case MessageBoxButton.YesNo:
                    defaultBtn = MakeBtn(yesLabel ?? "Yes", MessageBoxResult.Yes, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn(noLabel ?? "No", MessageBoxResult.No, isCancel: true));
                    break;
                case MessageBoxButton.YesNoCancel:
                    defaultBtn = MakeBtn("Yes", MessageBoxResult.Yes, accent: true, isDefault: true);
                    btnPanel.Children.Add(defaultBtn);
                    btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel, isCancel: true));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child   = btnPanel
            });

            if (defaultBtn != null)
            {
                var toFocus = defaultBtn;
                win.Loaded += (_, _2) => toFocus.Focus();
            }
            win.ShowDialog();
            return (result, ticked);
        }

        private static bool TryGetMessageBoxGlyph(
            MessageBoxImage image,
            System.Windows.Media.Brush accent,
            System.Windows.Media.Brush warning,
            System.Windows.Media.Brush danger,
            out string glyph,
            out System.Windows.Media.Brush brush)
        {
            switch (image)
            {
                case MessageBoxImage.Information:
                    glyph = "\uE946"; brush = accent; return true;
                case MessageBoxImage.Warning:
                    glyph = "\uE7BA"; brush = warning; return true;
                case MessageBoxImage.Error:
                    glyph = "\uEA39"; brush = danger; return true;
                case MessageBoxImage.Question:
                    glyph = "\uE9CE"; brush = accent; return true;
                default:
                    glyph = string.Empty; brush = accent; return false;
            }
        }
    }
}
