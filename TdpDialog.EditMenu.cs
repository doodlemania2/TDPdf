// TdpDialog: the "Edit menu" window (#237). Drag the rows of one right-click menu into the order you
// want, untick the ones you never use, or reset to the default. Alt+Up and Alt+Down move the focused
// row and Space shows or hides it, so nothing needs the mouse. Same shell and buttons as the other prompts.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TDPdf.Services;

namespace TDPdf
{
    internal static partial class TdpDialog
    {
        /// <summary>
        /// Edits <paramref name="editor"/> in place. <paramref name="describe"/> gives a row's name and
        /// glyph. Returns true when the user chose Save (the caller then stores <c>editor.ToLayout()</c>).
        /// </summary>
        public static bool ShowEditMenu(Window? owner, string menuName, MenuLayoutEditor editor,
                                        Func<string, (string Name, string? Glyph)> describe)
        {
            bool saved = false;
            var (win, root) = CreateShell(owner, "Edit Menu", 460);

            root.Children.Add(Pad(MakeLabel(
                $"Choose what the {menuName} shows and in what order. Drag a row, or press Alt+Up and Alt+Down. " +
                "Press Space, or use the box, to hide or show a row."), top: 16, bottom: 8));
            root.Children.Add(Pad(MakeLabel(
                "The row of buttons and the colour controls at the top of a menu stay where they are.", secondary: true), bottom: 8));

            var list = new ListBox
            {
                AllowDrop = true,
                SelectionMode = SelectionMode.Single,
                Background = Brush("BgDark"),
                BorderBrush = Brush("BorderDim"),
                Foreground = Brush("TextPrimary"),
                MaxHeight = 320,
                Margin = new Thickness(0, 0, 0, 4),
            };
            KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
            AutomationProperties.SetName(list, $"{menuName} items");
            AutomationProperties.SetHelpText(list, "Alt+Up and Alt+Down move the focused item. Space shows or hides it.");

            Point dragStart = default;
            int dragIndex = -1;

            void Refresh(int select)
            {
                list.Items.Clear();
                int n = editor.Rows.Count;
                for (int i = 0; i < n; i++)
                {
                    var (id, hidden) = editor.Rows[i];
                    var (name, glyph) = describe(id);
                    int index = i;

                    var check = new CheckBox
                    {
                        IsChecked = !hidden,
                        Focusable = false,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    AutomationProperties.SetName(check, $"Show {name}");
                    check.Click += (_, _) =>
                    {
                        editor.SetHidden(index, check.IsChecked != true);
                        Refresh(index);
                    };

                    var grip = new TextBlock
                    {
                        Text = "",   // GripperBarVertical
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Foreground = Brush("TextSecondary"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                    };
                    var icon = new TextBlock
                    {
                        Text = glyph ?? "",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Foreground = Brush("TextSecondary"),
                        Width = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var label = new TextBlock
                    {
                        Text = name,
                        Foreground = hidden ? Brush("TextSecondary") : Brush("TextPrimary"),
                        TextDecorations = hidden ? TextDecorations.Strikethrough : null,
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
                    row.Children.Add(grip);
                    row.Children.Add(check);
                    row.Children.Add(icon);
                    row.Children.Add(label);

                    var item = new ListBoxItem { Content = row };
                    AutomationProperties.SetName(item, name);
                    AutomationProperties.SetItemStatus(item, hidden ? "Hidden" : "Shown");
                    AutomationProperties.SetPositionInSet(item, i + 1);
                    AutomationProperties.SetSizeOfSet(item, n);
                    AutomationProperties.SetHelpText(item, "Alt+Up and Alt+Down move this item. Space shows or hides it.");
                    item.PreviewMouseLeftButtonDown += (_, e) =>
                    {
                        dragStart = e.GetPosition(list);
                        dragIndex = index;
                    };
                    item.PreviewMouseMove += (_, e) =>
                    {
                        if (e.LeftButton != MouseButtonState.Pressed || dragIndex != index) return;
                        var now = e.GetPosition(list);
                        if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                            && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                        dragIndex = -1;
                        DragDrop.DoDragDrop(item, new DataObject("tdpdf.menurow", index), DragDropEffects.Move);
                    };
                    list.Items.Add(item);
                }
                if (select >= 0 && select < n)
                {
                    list.SelectedIndex = select;
                    if (list.Items[select] is ListBoxItem sel)
                    {
                        sel.BringIntoView();
                        sel.Focus();
                    }
                }
            }

            int RowAt(Point p)
            {
                for (int i = 0; i < list.Items.Count; i++)
                {
                    if (list.Items[i] is not ListBoxItem it) continue;
                    var topLeft = it.TranslatePoint(new Point(0, 0), list);
                    if (p.Y < topLeft.Y + it.ActualHeight) return i;
                }
                return list.Items.Count - 1;
            }

            list.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent("tdpdf.menurow") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            list.Drop += (_, e) =>
            {
                if (!e.Data.GetDataPresent("tdpdf.menurow") || e.Data.GetData("tdpdf.menurow") is not int from) return;
                int to = RowAt(e.GetPosition(list));
                int landed = editor.Move(from, to);
                Refresh(landed);
                e.Handled = true;
            };
            list.PreviewKeyDown += (_, e) =>
            {
                int index = list.SelectedIndex;
                if (index < 0) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && key is Key.Up or Key.Down)
                {
                    int landed = editor.MoveBy(index, key == Key.Up ? -1 : 1);
                    Refresh(landed);
                    e.Handled = true;
                }
                else if (key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
                {
                    editor.ToggleHidden(index);
                    Refresh(index);
                    e.Handled = true;
                }
            };

            root.Children.Add(Pad(list, bottom: 8));

            var reset = MakeThemedButton("Reset to default", accent: false);
            reset.Click += (_, _) =>
            {
                editor.Reset();
                Refresh(0);
            };
            var save = MakeThemedButton("Save", accent: true);
            save.IsDefault = true;
            save.Click += (_, _) => { saved = true; win.Close(); };
            var cancel = MakeThemedButton("Cancel", accent: false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => win.Close();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(reset);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            root.Children.Add(new Border { Padding = new Thickness(16, 4, 16, 16), Child = buttons });

            Refresh(0);
            win.ShowDialog();
            return saved;
        }
    }
}
