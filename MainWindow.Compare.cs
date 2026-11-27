// MainWindow — Tools ▸ Compare PDFs (#239).
// Picks the second document, takes read-only snapshots of both, and opens the separate compare window.
// The window itself, the diff and the page pairing live in Services/ (CompareWindow, PdfCompare*).

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PdfSharpCore.Pdf;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private CompareWindow? _compareWindow;

        private void ComparePdfs_Click(object sender, RoutedEventArgs e) => ShowCompare();

        /// <summary>Compare the open document with another open tab or a file on disk, in its own window.</summary>
        private void ShowCompare()
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (_compareWindow is { IsLoaded: true })
            {
                _compareWindow.Activate();
                return;
            }
            CommitActiveTextBox();

            var leftCtx = _ctx;
            var others = _tabs.Where(t => !ReferenceEquals(t, leftCtx) && t.Doc is not null).ToList();
            DocumentContext? otherTab = null;
            string? otherFile = null;

            int pick = PickCompareTarget(others.Select(t => string.IsNullOrEmpty(t.DisplayName) ? "(untitled)" : t.DisplayName).ToList());
            if (pick < 0) return;
            if (pick < others.Count) otherTab = others[pick];
            else
            {
                var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Compare with" };
                if (dlg.ShowDialog(this) != true) return;
                otherFile = dlg.FileName;
            }

            string leftName = string.IsNullOrEmpty(leftCtx.DisplayName) ? "(untitled)" : leftCtx.DisplayName;
            string rightName = otherTab is not null
                ? (string.IsNullOrEmpty(otherTab.DisplayName) ? "(untitled)" : otherTab.DisplayName)
                : Path.GetFileName(otherFile!);

            var made = new List<string>();
            string Snapshot(PdfDocument doc)
            {
                string path = TempFilePolicy.NewPath("compare");
                made.Add(path);
                doc.Save(path);
                return path;
            }
            void Release()
            {
                foreach (var p in made) TempFilePolicy.TryDelete(p);
                made.Clear();
            }

            var sources = new CompareSources(leftName, rightName,
                take: () =>
                {
                    // Both are snapshots of the document as it is in memory now (unsaved edits included),
                    // so comparing never touches the open documents or their files.
                    var left = leftCtx.Doc ?? throw new InvalidOperationException($"{leftName} is no longer open.");
                    string l = Snapshot(left);
                    string r;
                    if (otherTab is not null)
                    {
                        var right = otherTab.Doc ?? throw new InvalidOperationException($"{rightName} is no longer open.");
                        r = Snapshot(right);
                    }
                    else
                    {
                        if (!File.Exists(otherFile!)) throw new FileNotFoundException($"{rightName} is no longer there.");
                        r = otherFile!;
                    }
                    return (l, r);
                },
                release: Release);

            var win = new CompareWindow(this, sources);
            _compareWindow = win;
            win.Closed += (_, _) => { if (ReferenceEquals(_compareWindow, win)) _compareWindow = null; };
            win.Show();
        }

        /// <summary>
        /// Asks which document to compare with: one of the other open tabs, or "Choose a file…" (the last
        /// index, which is <paramref name="tabNames"/>.Count). -1 when cancelled.
        /// </summary>
        private int PickCompareTarget(IReadOnlyList<string> tabNames)
        {
            var (win, body) = TdpDialog.CreateShell(this, "Compare PDFs", 420);
            win.ShowInTaskbar = false;
            win.Foreground = BrushResource("TextPrimary");
            win.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            win.FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "Compare the open document with:",
                Foreground = BrushResource("TextSecondary"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var list = new ListBox
            {
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                MaxHeight = 220,
                Margin = new Thickness(0, 0, 0, 12)
            };
            foreach (var n in tabNames) list.Items.Add(n);
            list.Items.Add("Choose a file…");
            list.SelectedIndex = 0;
            root.Children.Add(list);

            int result = -1;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button
            {
                Content = "Cancel", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgPanel"), Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"), Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true
            };
            var ok = new Button
            {
                Content = "Compare", Width = 96, Height = 30,
                Background = BrushResource("AccentGreen"), Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold, BorderBrush = BrushResource("AccentGreen"),
                Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            body.Children.Add(root);

            void Accept()
            {
                result = list.SelectedIndex;
                win.DialogResult = true;
            }
            ok.Click += (_, _) => Accept();
            cancel.Click += (_, _) => win.DialogResult = false;
            list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) Accept(); };
            win.ShowDialog();
            return result;
        }
    }
}
