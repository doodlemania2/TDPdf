using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TDPdf.Controls
{
    /// <summary>
    /// Makes a panel of radio buttons behave like a Windows radio group: one Tab stop that lands
    /// on the selected option, with the arrow keys moving between the options (Space selects).
    /// The panel must hold only the group's radio buttons. Idea from upstream KillerPDF 1.8.92.
    /// </summary>
    public static class RadioGroupKeyboard
    {
        public static T Apply<T>(T group) where T : Panel
        {
            KeyboardNavigation.SetTabNavigation(group, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(group, KeyboardNavigationMode.Cycle);
            group.PreviewGotKeyboardFocus += OnPreviewGotKeyboardFocus;
            return group;
        }

        // Tab into the group lands on the selected option rather than whichever one last had
        // focus. Keyboard only: a click focuses the option it lands on, and arrows inside the
        // group (old focus already inside) move freely.
        private static void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not Panel group || e.NewFocus is not RadioButton target || target.IsChecked == true) return;
            if (e.OldFocus is Visual old && group.IsAncestorOf(old)) return;
            if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
            var selected = group.Children.OfType<RadioButton>()
                .FirstOrDefault(o => o.IsChecked == true && o.IsEnabled && o.IsVisible);
            if (selected is null) return;
            e.Handled = true;
            selected.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => selected.Focus()));
        }
    }
}
