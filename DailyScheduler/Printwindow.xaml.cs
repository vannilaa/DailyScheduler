using QRCoder;
using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DailyScheduler
{
    /// <summary>
    /// Prints serial stickers for ONE schedule row.
    /// One printed "lembar" (strip) is 10 cm wide and holds 5 stickers of 2x2 cm, or 4 stickers of 2.5x2.5 cm.
    /// Each sticker: QR code in the middle, Job No + Model on the left (vertical), Part Number on the right (vertical),
    /// Position + Part on top, and the serial (date.map code.barcode.number) at the bottom.
    /// </summary>
    public partial class PrintWindow : Window
    {
        private const int MaxStickers = 20000;     // safety limit for one print job
        private const int MaxPreviewStrips = 8;    // the preview only shows the first strips
        private const double StripMm = 100;        // strip width (all layouts)

        // Remembered between runs (a small file in the user's AppData folder), so nothing has to be set again for every row.
        private static int _lastLayout = 0;
        private static int _lastRotation = 0;

        // Fine position per printer AND strip size: { x, y } in mm, + = right / down. Each printer feeds its labels a bit differently.
        private static readonly Dictionary<string, double[]> _offsets =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);

        // Shared QR generator — stateless, safe to reuse across all stickers.
        private static readonly QRCodeGenerator _qrGenerator = new QRCodeGenerator();

        private static double[] GetOffsets(string printer, int layout)
        {
            double[] o;
            if (_offsets.TryGetValue(printer + "|" + layout, out o)) return o;
            return new[] { layout == 2 ? 1.0 : 1.5, 0.0 };   // first guess: these printers print a little too far left
        }
        private static bool _settingsLoaded;

        private static string SettingsPath
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DailyScheduler");
                return System.IO.Path.Combine(dir, "print.ini");
            }
        }

        private static void LoadSettings()
        {
            if (_settingsLoaded) return;
            _settingsLoaded = true;
            try
            {
                if (!System.IO.File.Exists(SettingsPath)) return;
                foreach (string line in System.IO.File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    double d; int i;
                    if (key == "layout" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out i)) _lastLayout = i;
                    else if (key == "rotation" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out i)) _lastRotation = i;
                    else if (key == "offset")   // offset=printer|layout|x|y
                    {
                        string[] part = value.Split('|');
                        int layout; double ox, oy;
                        if (part.Length == 4 &&
                            int.TryParse(part[1], NumberStyles.None, CultureInfo.InvariantCulture, out layout) &&
                            double.TryParse(part[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ox) &&
                            double.TryParse(part[3], NumberStyles.Float, CultureInfo.InvariantCulture, out oy))
                            _offsets[part[0] + "|" + layout] = new[] { ox, oy };
                    }
                }
            }
            catch (Exception) { }   // settings are a convenience; ignore a broken file
        }

        private static void SaveSettings()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath));
                var lines = new List<string>
                {
                    "layout=" + _lastLayout.ToString(CultureInfo.InvariantCulture),
                    "rotation=" + _lastRotation.ToString(CultureInfo.InvariantCulture)
                };
                foreach (var kv in _offsets)
                    lines.Add("offset=" + kv.Key + "|" + kv.Value[0].ToString(CultureInfo.InvariantCulture) + "|" +
                              kv.Value[1].ToString(CultureInfo.InvariantCulture));
                System.IO.File.WriteAllLines(SettingsPath, lines);
            }
            catch (Exception) { }
        }

        /// <summary>Reads a number of millimetres from a box ("1.5" or "1,5" or "-2"); empty or wrong = 0. Limited to +-20 mm.</summary>
        private static double ReadMm(TextBox box)
        {
            double d;
            string s = (box.Text ?? "").Trim().Replace(',', '.');
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return 0;
            return Math.Max(-20, Math.Min(20, d));
        }

        private readonly ProductInfo _product;
        private readonly DateTime _date;
        private readonly string _mapCode;
        private readonly string _lineName;
        private readonly int _lineId;
        private readonly int _jumlah;
        private readonly string _rowPrinter;
        private readonly Dictionary<string, PrintQueue> _queues =
            new Dictionary<string, PrintQueue>(StringComparer.OrdinalIgnoreCase);

        private List<int> _numbers = new List<int>();
        private bool _ready;   // false while the window is still being built (ignore the events that fires)

        public PrintWindow(ScheduleRow row, DateTime date, LineProduction line)
        {
            InitializeComponent();

            _product = ScheduleRow.Products[row.Barcode];
            _date = date;
            _mapCode = line.MapCode;
            _lineName = line.Name;
            _lineId = line.Id;
            _jumlah = row.Jumlah;
            _rowPrinter = row.Printer;

            tbFrom.GotKeyboardFocus += delegate { rbRange.IsChecked = true; };
            tbTo.GotKeyboardFocus += delegate { rbRange.IsChecked = true; };
            tbList.GotKeyboardFocus += delegate { rbList.IsChecked = true; };

            LoadSettings();
            cbLayout.SelectedIndex = _lastLayout;
            cbRotation.SelectedIndex = _lastRotation;

            txtProduct.Text = _product.PartNumber + (string.IsNullOrEmpty(_product.Model) ? "" : " - " + _product.Model);
            txtInfo.Text = (_product.Customer ?? "") +
                           "\nJumlah: " + _jumlah.ToString("N0", CultureInfo.CurrentCulture) +
                           "   |   Tanggal: " + _date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) +
                           "\nLine: " + _lineName + " (" + _mapCode + ")   |   Barcode: " + _product.Barcode;

            LoadPrinters();

            _ready = true;
            ShowOffsets();
            UpdateAll();
        }

        // ------------------------------------------------------------------ printers

        private void LoadPrinters()
        {
            try
            {
                var server = new LocalPrintServer();
                var types = new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections };
                foreach (PrintQueue q in server.GetPrintQueues(types))
                    _queues[q.FullName] = q;
            }
            catch (Exception ex)
            {
                txtPrinterNote.Text = "Daftar printer tidak bisa dibaca: " + ex.Message;
            }

            cbPrinter.ItemsSource = _queues.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

            // The row's printer if this computer has it, otherwise the Windows default printer (and say so).
            string pick = null;
            if (!string.IsNullOrEmpty(_rowPrinter))
                pick = _queues.Keys.FirstOrDefault(k => string.Equals(k, _rowPrinter, StringComparison.OrdinalIgnoreCase));

            if (pick == null)
            {
                if (!string.IsNullOrEmpty(_rowPrinter))
                    txtPrinterNote.Text = "Printer \"" + _rowPrinter + "\" tidak ada di komputer ini. Pilih printer lain.";
                try { pick = LocalPrintServer.GetDefaultPrintQueue().FullName; }
                catch (Exception) { }
            }

            if (pick != null && _queues.ContainsKey(pick)) cbPrinter.SelectedItem = pick;
        }

        // ------------------------------------------------------------------ which stickers

        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAll();
        }

        // ---- fine position (per printer + strip size) ----

        private bool _loadingOffsets;

        private string OffsetKey()
        {
            return (cbPrinter.SelectedItem as string ?? "") + "|" + LayoutIndex;
        }

        /// <summary>Shows the saved offsets of the chosen printer + strip size in the two boxes.</summary>
        private void ShowOffsets()
        {
            double[] o = GetOffsets(cbPrinter.SelectedItem as string ?? "", LayoutIndex);
            _loadingOffsets = true;
            try
            {
                tbOffsetX.Text = o[0].ToString("0.##", CultureInfo.InvariantCulture);
                tbOffsetY.Text = o[1].ToString("0.##", CultureInfo.InvariantCulture);
            }
            finally { _loadingOffsets = false; }
        }

        // Mouse wheel / arrow keys / the little arrow buttons change the value in steps: 0.5 mm, or 0.1 mm while Shift is held.
        private void Nudge(string axis, int direction)
        {
            TextBox box = axis == "Y" ? tbOffsetY : tbOffsetX;
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.1 : 0.5;
            double value = Math.Round(ReadMm(box) + direction * step, 2);
            box.Text = Math.Max(-20, Math.Min(20, value)).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void Offset_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Nudge(((FrameworkElement)sender).Tag as string, e.Delta > 0 ? 1 : -1);
            e.Handled = true;   // do not scroll the options panel at the same time
        }

        private void Offset_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Up && e.Key != Key.Down) return;
            Nudge(((FrameworkElement)sender).Tag as string, e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }

        private void OffsetButton_Click(object sender, RoutedEventArgs e)
        {
            string[] tag = (((FrameworkElement)sender).Tag as string ?? "X:1").Split(':');   // "X:1" or "Y:-1"
            Nudge(tag[0], tag.Length > 1 && tag[1] == "-1" ? -1 : 1);
        }

        private void Offset_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _loadingOffsets) return;
            _offsets[OffsetKey()] = new[] { ReadMm(tbOffsetX), ReadMm(tbOffsetY) };
        }

        // Printer or strip size changed: load the offsets that belong to that combination.
        private void Setup_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            ShowOffsets();
            UpdateAll();
        }

        private void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(c => c >= '0' && c <= '9');
        }

        // The three strip layouts (same order as the Ukuran stiker dropdown). Every strip is 10 cm wide.
        private static readonly int[] PerStripByLayout = { 5, 4, 8 };
        private static readonly double[] HeightMmByLayout = { 20, 25, 15 };

        private int LayoutIndex { get { return Math.Max(0, Math.Min(cbLayout.SelectedIndex, PerStripByLayout.Length - 1)); } }
        private int StickerPerStrip { get { return PerStripByLayout[LayoutIndex]; } }
        private double StickerH { get { return HeightMmByLayout[LayoutIndex]; } }
        private double StickerW { get { return StripMm / StickerPerStrip; } }   // 20, 25 or 12.5 mm
        private double RotationAngle { get { return Math.Max(0, cbRotation.SelectedIndex) * 90.0; } }

        private static bool TryInt(string s, out int value)
        {
            return int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Reads the options and returns the sticker numbers (sorted, no duplicates). error != null when the input is wrong.</summary>
        private List<int> ParseSelection(out string error)
        {
            error = null;
            var none = new List<int>();
            var segments = new List<int[]>();   // each = { from, to }

            if (rbAll.IsChecked == true)
            {
                segments.Add(new[] { 1, _jumlah });
            }
            else if (rbRange.IsChecked == true)
            {
                int from = 1, to = _jumlah;
                if (!string.IsNullOrWhiteSpace(tbFrom.Text) && !TryInt(tbFrom.Text, out from)) { error = "Isi \"Dari\" dengan angka."; return none; }
                if (!string.IsNullOrWhiteSpace(tbTo.Text) && !TryInt(tbTo.Text, out to)) { error = "Isi \"Sampai\" dengan angka."; return none; }
                if (from < 1 || to > _jumlah || from > to)
                {
                    error = "Rentang harus antara 1 dan " + _jumlah + ", dan \"Dari\" tidak boleh lebih besar dari \"Sampai\".";
                    return none;
                }
                segments.Add(new[] { from, to });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(tbList.Text)) return none;   // nothing typed yet: no error, just nothing to print

                foreach (string token in tbList.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int a = 0, b = 0;
                    bool ok;
                    int dash = token.IndexOf('-');
                    if (dash > 0)   // "10-20"
                        ok = TryInt(token.Substring(0, dash), out a) && TryInt(token.Substring(dash + 1), out b);
                    else            // "80"
                    {
                        ok = TryInt(token, out a);
                        b = a;
                    }

                    if (!ok) { error = "\"" + token + "\" bukan nomor yang benar."; return none; }
                    if (a < 1 || b > _jumlah || a > b)
                    {
                        error = "\"" + token + "\" di luar batas. Nomor harus antara 1 dan " + _jumlah + ".";
                        return none;
                    }
                    segments.Add(new[] { a, b });
                }
            }

            long total = segments.Sum(s => (long)s[1] - s[0] + 1);
            if (total > MaxStickers)
            {
                error = "Terlalu banyak (" + total.ToString("N0", CultureInfo.CurrentCulture) + " stiker). Maksimal " +
                        MaxStickers.ToString("N0", CultureInfo.CurrentCulture) + " sekali cetak. Pakai Rentang.";
                return none;
            }

            // For a single contiguous range (All / Range mode) the numbers are already sorted
            // and duplicate-free — skip the SortedSet and allocate directly.
            if (segments.Count == 1)
            {
                var seg = segments[0];
                var result = new List<int>((int)total);
                for (int n = seg[0]; n <= seg[1]; n++) result.Add(n);
                return result;
            }

            // Manual list mode: may contain overlapping ranges — deduplicate and sort via SortedSet.
            var set = new SortedSet<int>();
            foreach (int[] s in segments)
                for (int n = s[0]; n <= s[1]; n++) set.Add(n);
            return set.ToList();
        }

        private void UpdateAll()
        {
            if (!_ready) return;

            string error;
            _numbers = ParseSelection(out error);
            txtError.Text = error ?? "";

            int per = StickerPerStrip;
            int strips = (_numbers.Count + per - 1) / per;

            if (error != null)
                txtSummary.Text = "";
            else if (_numbers.Count == 0)
                txtSummary.Text = "Isi nomor stiker yang mau dicetak.";
            else
            {
                int lastFill = _numbers.Count - (strips - 1) * per;
                txtSummary.Text = _numbers.Count.ToString("N0", CultureInfo.CurrentCulture) + " stiker = " +
                                  strips.ToString("N0", CultureInfo.CurrentCulture) + " lembar (" + per + " stiker per lembar)" +
                                  "\nPertama: " + Serial(_numbers[0]) +
                                  "\nTerakhir: " + Serial(_numbers[_numbers.Count - 1]) +
                                  (lastFill < per ? "\nLembar terakhir terisi " + lastFill + " dari " + per + "." : "");
            }

            btnPrint.IsEnabled = error == null && _numbers.Count > 0 && cbPrinter.SelectedItem != null;
            BuildPreview();
        }

        // ------------------------------------------------------------------ the sticker

        private string Serial(int number)
        {
            return _date.ToString("yyMMdd", CultureInfo.InvariantCulture) + "." + _mapCode + "." + _product.Barcode + "." +
                   number.ToString("D4", CultureInfo.InvariantCulture);
        }

        private static double Mm(double mm) { return mm * 96.0 / 25.4; }   // WPF units are 1/96 inch

        private static string Join(string separator, params string[] parts)
        {
            return string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        // ---- The 1.25 cm / 1.5 cm stock (8 per strip) ----------------------------------------------
        // Actual sticker size on the liner.
        private const double CompactStickerWMm = 12.0;
        private const double CompactStickerHMm = 11.5;

        /// <summary>
        /// Vector text in Arial Narrow with balanced stroke reinforcement for thermal printing —
        /// punchy and solid so printheads never fade or print hairline gaps, while keeping
        /// character counters completely open and crisp (no bitmap smudging or blur).
        /// </summary>
        private static FrameworkElement FitText(string text, double maxFontSize, FontWeight weight, double angle, double margin, bool reinforce = true)
        {
            Func<TextBlock> makeText = () => new TextBlock
            {
                Text = text ?? "",
                FontFamily = new FontFamily("Arial Narrow, Arial"),
                FontSize = maxFontSize,
                FontWeight = weight,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center
            };

            FrameworkElement content;
            if (reinforce)
            {
                // Subtle ~0.35px offset: reinforces stroke density on thermal printheads
                // so lines never print faint or broken, while avoiding the bloated smudge of bitmap thresholding.
                double d = Math.Max(0.25, Math.Min(0.5, maxFontSize * 0.028));
                var stack = new Grid();
                stack.Children.Add(makeText());
                var r = makeText();
                r.RenderTransform = new TranslateTransform(d, d * 0.5);
                stack.Children.Add(r);
                content = stack;
            }
            else
            {
                content = makeText();
            }

            if (angle != 0) content.LayoutTransform = new RotateTransform(angle);

            return new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Margin = new Thickness(margin),
                Child = content
            };
        }

        /// <summary>
        /// QR code generated as an exact 1-bit/grayscale bitmap displayed with NearestNeighbor scaling,
        /// ensuring every module is a razor-sharp square with zero anti-aliasing blur.
        /// </summary>
        private static FrameworkElement MakeQrBitmap(string text, double margin, QRCodeGenerator.ECCLevel level = QRCodeGenerator.ECCLevel.M)
        {
            var data = _qrGenerator.CreateQrCode(text, level);
            var m = data.ModuleMatrix;
            int total = m.Count;

            int minX = total, minY = total, maxX = -1, maxY = -1;
            for (int y = 0; y < total; y++)
                for (int x = 0; x < total; x++)
                    if (m[y][x])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            if (maxX < 0) return new Border();

            int size = Math.Max(maxX - minX + 1, maxY - minY + 1);
            var raw = new byte[size * size];
            for (int y = 0; y < size; y++)
            {
                int srcY = y + minY;
                for (int x = 0; x < size; x++)
                {
                    int srcX = x + minX;
                    bool dark = srcY < total && srcX < total && m[srcY][srcX];
                    raw[y * size + x] = dark ? (byte)0 : (byte)255;
                }
            }

            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, raw, size);
            bitmap.Freeze();

            var img = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(margin)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            return img;
        }

        private static void Place(Grid grid, UIElement element, int row, int column, int columnSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            if (columnSpan > 1) Grid.SetColumnSpan(element, columnSpan);
            grid.Children.Add(element);
        }

        /// <summary>
        /// One 1.25 cm sticker (Layout 2: 8 per strip, 12.5 x 15 mm slot).
        /// Left (vertical): map code + date + number.
        /// Right: QR code with part number directly underneath it.
        /// </summary>
        private FrameworkElement BuildCompactSticker(int number, double slotWMm, double slotHMm)
        {
            double sw = Mm(CompactStickerWMm), sh = Mm(CompactStickerHMm);
            string serial = Serial(number);

            string pn = _product.PartNumber ?? "";
            int dash = pn.IndexOf('-');
            string pnText = (pn.Length > 11 && dash > 0 && dash < pn.Length - 1)
                ? pn.Substring(0, dash + 1) + "\n" + pn.Substring(dash + 1)
                : pn;

            string leftText = _mapCode + "." + _date.ToString("yyMMdd", CultureInfo.InvariantCulture) +
                              number.ToString("D4", CultureInfo.InvariantCulture);

            double leftW = sw * 0.22;
            double rightW = sw - leftW;
            double qrSize = Math.Min(rightW - Mm(0.3), sh * 0.62);

            var grid = new Grid { Width = sw, Height = sh, Background = Brushes.White };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftW) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left: Map code + date + number (turned -90, reading bottom to top)
            Place(grid, FitText(leftText, Mm(2.2), FontWeights.SemiBold, -90, Mm(0.15), reinforce: true), 0, 0);

            // Right column: contains QR code on top, Part Number below
            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(qrSize) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // QR Code (ECC Level L = fewest modules, largest individual dot size)
            Place(rightGrid, MakeQrBitmap(serial, margin: 0, level: QRCodeGenerator.ECCLevel.L), 0, 0);

            // Part Number below QR code (bold, reinforced)
            Place(rightGrid, FitText(pnText, Mm(2.6), FontWeights.Bold, 0, Mm(0.1), reinforce: true), 1, 0);

            Place(grid, rightGrid, 0, 1);

            return new Border
            {
                Width = Mm(slotWMm),
                Height = Mm(slotHMm),
                Background = Brushes.White,
                Child = grid
            };
        }

        /// <summary>
        /// One 2 cm or 2.5 cm sticker (square).
        /// Top: Position + Part.
        /// Left: Job No / Model (turned -90).
        /// Middle: QR code (pixel-perfect bitmap).
        /// Right: Part Number (turned -90).
        /// Bottom: Serial number.
        /// </summary>
        private FrameworkElement BuildSticker(int number, double wMm, double hMm)
        {
            double w = Mm(wMm), h = Mm(hMm);
            double side = w * 0.14;                 // left / right vertical text columns
            double bar = Math.Min(w, h) * 0.13;     // top / bottom horizontal text rows
            double pad = Mm(0.3);
            double fontSize = bar * 1.5;
            string serial = Serial(number);

            var grid = new Grid { Width = w, Height = h, Background = Brushes.White };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(side) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(side) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bar) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bar) });

            // Top: Position + Part (bold heading, reinforced)
            Place(grid, FitText(Join(" \u00B7 ", _product.Position, _product.Part), fontSize, FontWeights.Bold, 0, pad, reinforce: true), 0, 0, 3);

            // Left: Job No / Model (reads bottom to top, semibold, reinforced)
            Place(grid, FitText(Join(" / ", _product.JobNo, _product.Model), fontSize, FontWeights.SemiBold, -90, pad, reinforce: true), 1, 0, 1);

            // Middle: QR code (pixel-perfect bitmap with NearestNeighbor - crystal sharp, 0 blur)
            Place(grid, MakeQrBitmap(serial, Mm(0.3), QRCodeGenerator.ECCLevel.M), 1, 1, 1);

            // Right: Part Number (reads bottom to top, bold primary identifier, reinforced)
            Place(grid, FitText(_product.PartNumber, fontSize, FontWeights.Bold, -90, pad, reinforce: true), 1, 2, 1);

            // Bottom: Serial (semibold so digits 0, 6, 8, 9 have open, readable counters)
            Place(grid, FitText(serial, fontSize, FontWeights.SemiBold, 0, pad, reinforce: true), 2, 0, 3);

            return grid;
        }

        /// <summary>One strip (10 cm wide). Slots without a number stay blank.</summary>
        private FrameworkElement BuildStrip(IList<int> numbers, double wMm, double hMm, int perStrip)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
            bool compact = wMm < 15;

            for (int i = 0; i < perStrip; i++)
            {
                if (i < numbers.Count)
                {
                    panel.Children.Add(compact
                        ? BuildCompactSticker(numbers[i], wMm, hMm)
                        : BuildSticker(numbers[i], wMm, hMm));
                }
                else
                {
                    panel.Children.Add(new Border { Width = Mm(wMm), Height = Mm(hMm), Background = Brushes.White });
                }
            }
            return panel;
        }

        // ------------------------------------------------------------------ preview

        private void BuildPreview()
        {
            spPreview.Children.Clear();
            if (_numbers.Count == 0) return;

            int per = StickerPerStrip;
            double wMm = StickerW, hMm = StickerH;
            int strips = (_numbers.Count + per - 1) / per;
            int shown = Math.Min(strips, MaxPreviewStrips);

            for (int i = 0; i < shown; i++)
            {
                var chunk = _numbers.Skip(i * per).Take(per).ToList();

                spPreview.Children.Add(new TextBlock
                {
                    Text = "Lembar " + (i + 1) + "   (" + string.Join(", ", chunk.Select(n => n.ToString("D4", CultureInfo.InvariantCulture))) + ")",
                    Foreground = Brushes.Gray,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 2)
                });

                spPreview.Children.Add(new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 12),
                    Child = new Viewbox { Width = 600, Stretch = Stretch.Uniform, Child = BuildStrip(chunk, wMm, hMm, per) }
                });
            }

            if (strips > shown)
            {
                spPreview.Children.Add(new TextBlock
                {
                    Text = "... dan " + (strips - shown).ToString("N0", CultureInfo.CurrentCulture) + " lembar lainnya.",
                    Foreground = Brushes.Gray
                });
            }
        }

        // ------------------------------------------------------------------ printing

        /// <summary>
        /// Asks the printer driver for a page of exactly the strip size. Returns the ticket to print with and the page size the
        /// driver really gave us; sizeChanged = true when the driver did not accept our size (it kept its own paper size).
        /// </summary>
        private static PrintTicket PrepareTicket(PrintQueue queue, Size expected, out Size pageSize, out bool sizeChanged)
        {
            pageSize = expected;
            sizeChanged = false;
            try
            {
                PrintTicket current = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
                PrintTicket wanted = current.Clone();
                wanted.PageMediaSize = new PageMediaSize(expected.Width, expected.Height);

                PrintTicket validated = queue.MergeAndValidatePrintTicket(current, wanted).ValidatedPrintTicket;

                PageMediaSize got = validated.PageMediaSize;
                if (got != null && got.Width.HasValue && got.Height.HasValue)
                {
                    pageSize = new Size(got.Width.Value, got.Height.Value);
                    double tolerance = Mm(1.5);
                    sizeChanged = Math.Abs(pageSize.Width - expected.Width) > tolerance ||
                                  Math.Abs(pageSize.Height - expected.Height) > tolerance;
                }
                return validated;
            }
            catch (Exception)
            {
                return null;   // print with the printer's own settings
            }
        }

        private FixedDocument BuildDocument(List<int> numbers, double wMm, double hMm, int perStrip, double angle, Size pageSize, double offsetXMm, double offsetYMm)
        {
            var document = new FixedDocument();
            document.DocumentPaginator.PageSize = pageSize;

            for (int i = 0; i < numbers.Count; i += perStrip)
            {
                // GetRange is O(count) — avoids the O(i) re-walk that Skip(i) does on each iteration.
                FrameworkElement strip = BuildStrip(numbers.GetRange(i, Math.Min(perStrip, numbers.Count - i)), wMm, hMm, perStrip);
                if (angle != 0) strip.LayoutTransform = new RotateTransform(angle);

                var page = new FixedPage { Width = pageSize.Width, Height = pageSize.Height, Background = Brushes.White };
                FixedPage.SetLeft(strip, Mm(offsetXMm));   // fine position: moves the whole strip on the page
                FixedPage.SetTop(strip, Mm(offsetYMm));
                page.Children.Add(strip);

                page.Measure(pageSize);
                page.Arrange(new Rect(new Point(0, 0), pageSize));
                page.UpdateLayout();

                var content = new PageContent();
                ((IAddChild)content).AddChild(page);
                document.Pages.Add(content);
            }
            return document;
        }

        /// <summary>A single test strip: a frame around every slot, an arrow that points up, and the slot number.</summary>
        private FixedDocument BuildTestDocument(double wMm, double hMm, int perStrip, double angle, Size pageSize, double offsetXMm, double offsetYMm)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
            for (int i = 0; i < perStrip; i++)
            {
                var cell = new Grid { Width = Mm(wMm), Height = Mm(hMm) };
                cell.Children.Add(new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(Mm(0.3)) });
                cell.Children.Add(new TextBlock
                {
                    Text = "\u25B2\n" + (i + 1),
                    TextAlignment = TextAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    FontSize = Math.Min(Mm(wMm), Mm(hMm)) * 0.3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                strip.Children.Add(cell);
            }
            if (angle != 0) strip.LayoutTransform = new RotateTransform(angle);

            var document = new FixedDocument();
            document.DocumentPaginator.PageSize = pageSize;

            var page = new FixedPage { Width = pageSize.Width, Height = pageSize.Height, Background = Brushes.White };
            FixedPage.SetLeft(strip, Mm(offsetXMm));
            FixedPage.SetTop(strip, Mm(offsetYMm));
            page.Children.Add(strip);
            page.Measure(pageSize);
            page.Arrange(new Rect(new Point(0, 0), pageSize));
            page.UpdateLayout();

            var content = new PageContent();
            ((IAddChild)content).AddChild(page);
            document.Pages.Add(content);
            return document;
        }

        private void btnPrint_Click(object sender, RoutedEventArgs e) { Print(false); }

        // One strip with only frames and numbers (no data): shows where the printer really puts the print, for setting the offsets.
        private void btnTest_Click(object sender, RoutedEventArgs e) { Print(true); }

        private void Print(bool test)
        {
            string name = cbPrinter.SelectedItem as string;
            PrintQueue queue;
            if (name == null || !_queues.TryGetValue(name, out queue))
            {
                MessageBox.Show("Pilih printer dulu.", "Cetak", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<int> numbers;
            if (test)
                numbers = Enumerable.Range(1, StickerPerStrip).ToList();   // one strip
            else
            {
                string error;
                numbers = ParseSelection(out error);
                if (error != null || numbers.Count == 0) return;
            }

            int per = StickerPerStrip;
            double hMm = StickerH, wMm = StickerW;
            double angle = RotationAngle;
            int strips = (numbers.Count + per - 1) / per;

            // The page the driver must give us: 10 cm x sticker height (swapped when the strip is turned 90 / 270 degrees).
            bool turned = angle == 90 || angle == 270;
            var expected = turned ? new Size(Mm(hMm), Mm(StripMm)) : new Size(Mm(StripMm), Mm(hMm));

            Size pageSize;
            bool sizeChanged;
            PrintTicket ticket = PrepareTicket(queue, expected, out pageSize, out sizeChanged);

            if (sizeChanged)
            {
                string msg = string.Format(CultureInfo.CurrentCulture,
                    "Driver printer memakai ukuran kertas {0:0.#} x {1:0.#} mm, bukan {2:0.#} x {3:0.#} mm.\n\n" +
                    "Atur ukuran kertas di Printer Preferences menjadi {2:0.#} x {3:0.#} mm supaya hasil tidak terpotong.\n\nTetap cetak?",
                    pageSize.Width * 25.4 / 96, pageSize.Height * 25.4 / 96, expected.Width * 25.4 / 96, expected.Height * 25.4 / 96);
                if (MessageBox.Show(msg, "Ukuran kertas berbeda", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            string ask = test
                ? "Cetak 1 lembar TES (hanya kotak dan nomor) ke " + name + "?"
                : string.Format(CultureInfo.CurrentCulture, "Cetak {0:N0} stiker ({1:N0} lembar) ke {2}?", numbers.Count, strips, name);
            if (MessageBox.Show(ask, "Cetak", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Were some of these numbers printed before? Then a reason is required (nothing is printed without it).
            string reprintReason = null, reprintNote = null;
            if (!test)
            {
                List<PrintedSticker> already;
                try
                {
                    already = Database.FindPrinted(_date, _lineId, _product.Barcode, numbers);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Riwayat cetak tidak bisa dibaca, jadi belum dicetak:\n\n" + ex.Message, "Database",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (already.Count > 0)
                {
                    var reprint = new ReprintDialog(already) { Owner = this };
                    if (reprint.ShowDialog() != true) return;
                    reprintReason = reprint.Reason;
                    reprintNote = reprint.Note;
                }
            }

            _lastLayout = cbLayout.SelectedIndex;
            _lastRotation = cbRotation.SelectedIndex;
            _offsets[OffsetKey()] = new[] { ReadMm(tbOffsetX), ReadMm(tbOffsetY) };
            SaveSettings();

            Cursor = Cursors.Wait;
            try
            {
                FixedDocument document = test
                    ? BuildTestDocument(wMm, hMm, per, angle, pageSize, ReadMm(tbOffsetX), ReadMm(tbOffsetY))
                    : BuildDocument(numbers, wMm, hMm, per, angle, pageSize, ReadMm(tbOffsetX), ReadMm(tbOffsetY));

                var dialog = new PrintDialog();
                dialog.PrintQueue = queue;                       // set the queue first: it resets the ticket
                if (ticket != null) dialog.PrintTicket = ticket;
                dialog.PrintDocument(document.DocumentPaginator, "Stiker " + _product.Barcode + " " + _date.ToString("yyMMdd", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Arrow;
                MessageBox.Show("Gagal mencetak:\n\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }

            if (!test)
            {
                try
                {
                    Database.LogPrint(new PrintLogEntry
                    {
                        Tanggal = _date,
                        LineProductionId = _lineId,
                        LineName = _lineName,
                        MapCode = _mapCode,
                        Barcode = _product.Barcode,
                        PartNumber = _product.PartNumber,
                        Model = _product.Model,
                        Printer = name,
                        StickerSize = string.Format(CultureInfo.InvariantCulture, "{0:0.##} x {1:0.##} mm", wMm, hMm),
                        Numbers = numbers,
                        SerialOf = Serial,
                        Reason = reprintReason,
                        Note = reprintNote
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Stiker sudah dikirim ke printer, tetapi riwayat cetaknya GAGAL disimpan:\n\n" + ex.Message +
                                    "\n\nCatat nomor stiker yang baru dicetak ini secara manual.", "Riwayat cetak",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            MessageBox.Show("Terkirim ke printer.", "Cetak", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}