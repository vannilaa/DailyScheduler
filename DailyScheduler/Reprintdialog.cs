using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DailyScheduler
{
    /// <summary>
    /// Shown before printing when some of the chosen sticker numbers were printed already.
    /// A reason is required: OK stays disabled until one is chosen (and a note is typed when the reason is "Lainnya").
    /// </summary>
    public class ReprintDialog : Window
    {
        private const string OtherReason = "Lainnya";

        private static readonly string[] Reasons =
        {
            "Stiker rusak / sobek",
            "Hasil cetak miring / bergeser",
            "Hasil cetak pudar / tidak terbaca",
            "Printer macet / berhenti di tengah",
            "Data stiker salah",
            "Stiker hilang",
            OtherReason
        };

        private readonly ComboBox _reason = new ComboBox { FontSize = 16, Margin = new Thickness(0, 4, 0, 10) };
        private readonly TextBox _note = new TextBox
        {
            FontSize = 16,
            Height = 70,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 500
        };
        private readonly TextBlock _noteLabel = new TextBlock { Text = "Catatan (wajib):", FontSize = 16, Visibility = Visibility.Collapsed };
        private readonly Button _ok = new Button { Content = "Cetak ulang", Width = 130, Padding = new Thickness(5), Margin = new Thickness(0, 0, 8, 0), FontSize = 16, IsEnabled = false };

        public string Reason { get; private set; }
        public string Note { get; private set; }

        public ReprintDialog(IList<PrintedSticker> already)
        {
            Title = "Cetak ulang";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = already.Count.ToString("N0", CultureInfo.CurrentCulture) + " stiker sudah pernah dicetak:",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var list = new ListBox { MaxHeight = 160, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 14 };
            foreach (PrintedSticker p in already.Take(200))
            {
                list.Items.Add(string.Format(CultureInfo.InvariantCulture, "No. {0:D4}   dicetak {1}x   terakhir {2:dd-MM-yyyy HH:mm}  oleh {3}",
                                             p.Number, p.PrintCount, p.LastPrintedAt, p.LastUser));
            }
            if (already.Count > 200)
                list.Items.Add("... dan " + (already.Count - 200).ToString("N0", CultureInfo.CurrentCulture) + " lainnya.");
            root.Children.Add(list);

            root.Children.Add(new TextBlock { Text = "Alasan cetak ulang (wajib):", FontSize = 16, Margin = new Thickness(0, 12, 0, 0) });
            foreach (string reason in Reasons) _reason.Items.Add(reason);
            root.Children.Add(_reason);

            root.Children.Add(_noteLabel);
            _note.Margin = new Thickness(0, 4, 0, 14);
            _note.Visibility = Visibility.Collapsed;
            root.Children.Add(_note);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _ok.Click += delegate
            {
                Reason = _reason.SelectedItem as string;
                Note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim();
                DialogResult = true;
            };
            buttons.Children.Add(_ok);
            buttons.Children.Add(new Button { Content = "Batal", Width = 100, Padding = new Thickness(5), FontSize = 16, IsCancel = true });
            root.Children.Add(buttons);

            Content = root;

            _reason.SelectionChanged += delegate { UpdateOk(); };
            _note.TextChanged += delegate { UpdateOk(); };
        }

        private void UpdateOk()
        {
            string reason = _reason.SelectedItem as string;
            bool isOther = reason == OtherReason;

            // Show the note field only when "Lainnya" is selected.
            _noteLabel.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;
            _note.Visibility     = isOther ? Visibility.Visible : Visibility.Collapsed;

            bool ok = reason != null && (!isOther || !string.IsNullOrWhiteSpace(_note.Text));
            _ok.IsEnabled = ok;
        }
    }
}