using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DailyScheduler
{
    public partial class MainWindow : Window
    {
        static MainWindow()
        {
            // Watch every ComboBox's focus; the handler below only acts on the grid's filter boxes
            EventManager.RegisterClassHandler(typeof(ComboBox), UIElement.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(FilterCombo_GotKeyboardFocus));
        }


        private readonly ObservableCollection<ScheduleRow> _rows = new ObservableCollection<ScheduleRow>();

        public MainWindow()
        {
            InitializeComponent();

            _rows.CollectionChanged += Rows_CollectionChanged;
            dgSchedule.ItemsSource = _rows;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        // ------------------------------------------------------------------ startup

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // One round-trip for all three startup datasets.
                var (lines, products, printers) = Database.GetStartupData();

                ScheduleRow.AllProducts = products.Where(p => !p.Discontinued).ToList();
                ScheduleRow.Products = products.ToDictionary(p => p.Barcode);
                ScheduleRow.RebuildIndex();   // build per-field lookup for fast Recompute

                cbLineProduction.ItemsSource = lines;

                ScheduleRow.PrinterList.Clear();
                ScheduleRow.PrinterList.AddRange(printers);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Database tidak bisa dibuka:\n\n" + ex.Message, "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            dpTanggal.SelectedDate = DateTime.Today;

            // Hook these AFTER the initial values are set, so setup doesn't trigger a reload.
            dpTanggal.SelectedDateChanged += Selection_Changed;
            cbLineProduction.SelectionChanged += Selection_Changed;
        }

        // ------------------------------------------------------------------ load

        private LineProduction _selectedLine;   // the last line the user really picked
        private string _loadedKey;              // "date|lineId" of the schedule currently on screen
        private DateTime? _loadedDate;          // date of the schedule currently on screen
        private LineProduction _loadedLine;     // line of the schedule currently on screen
        private bool _dirty;                    // true = the grid has changes that are not saved yet
        private bool _reverting;                // true while we put the date / line controls back ourselves

        private void Selection_Changed(object sender, EventArgs e)
        {
            if (_reverting) return;

            var line = cbLineProduction.SelectedItem as LineProduction;

            // While the user types in the Line Production box its selection is empty. Keep the grid as it is.
            if (dpTanggal.SelectedDate == null || line == null)
            {
                if (line != null) _selectedLine = line;
                return;
            }

            string key = dpTanggal.SelectedDate.Value.ToString("yyyy-MM-dd") + "|" + line.Id;
            if (key == _loadedKey)              // this schedule is already on screen: do not reload (it would drop unsaved rows)
            {
                _selectedLine = line;
                return;
            }

            // Leaving a schedule with unsaved changes: ask first.
            if (_dirty && !ConfirmLeaveSchedule())
            {
                // Put date and line back, after this event has finished.
                Dispatcher.BeginInvoke(new Action(RevertSelection), DispatcherPriority.Background);
                return;
            }

            _selectedLine = line;
            _loadedKey = key;
            _loadedDate = dpTanggal.SelectedDate.Value;
            _loadedLine = line;
            LoadSchedule();
        }

        /// <summary>"Simpan dulu?" Yes = save and go on, No = throw the changes away and go on, Cancel = stay. Returns true to go on.</summary>
        private bool ConfirmLeaveSchedule()
        {
            var answer = MessageBox.Show("Ada perubahan yang belum disimpan.\n\nSimpan dulu?", "Simpan dulu?",
                                         MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) return SaveCurrent(false);   // false (e.g. incomplete row) = stay
            return true;
        }

        /// <summary>Puts the date and line controls back to the schedule that is still on screen.</summary>
        private void RevertSelection()
        {
            _reverting = true;
            try
            {
                var view = CollectionViewSource.GetDefaultView(cbLineProduction.ItemsSource);
                if (view != null) view.Filter = null;   // a filtered-out line can't be selected

                if (_loadedDate != null) dpTanggal.SelectedDate = _loadedDate;
                if (_loadedLine != null)
                {
                    cbLineProduction.SelectedItem = _loadedLine;
                    cbLineProduction.Text = _loadedLine.Name;
                }
                _selectedLine = _loadedLine;
            }
            finally { _reverting = false; }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_dirty && !ConfirmLeaveSchedule()) e.Cancel = true;
        }

        private void LineCombo_TextChanged(object sender, TextChangedEventArgs e)
        {
            var cmb = (ComboBox)sender;
            if (cmb.ItemsSource == null || _resettingText) return;

            var view = CollectionViewSource.GetDefaultView(cmb.ItemsSource);
            if (view == null) return;

            string text = cmb.Text;
            var selected = cmb.SelectedItem as LineProduction;

            // Placeholder, empty, or just the picked name: not typing, show the whole list
            if (string.IsNullOrWhiteSpace(text) || (selected != null && selected.Name == text))
            {
                view.Filter = null;
                return;
            }

            view.Filter = item => ((LineProduction)item).Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            if (cmb.IsKeyboardFocusWithin) OpenDropDownKeepCaret(cmb);
        }

        private void LineCombo_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var cmb = (ComboBox)sender;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                var tb = cmb.Template.FindName("PART_EditableTextBox", cmb) as TextBox;
                if (tb != null) tb.SelectAll();   // typing replaces the old text
            }), DispatcherPriority.Input);
        }

        private void LineCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var cmb = (ComboBox)sender;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (cmb.IsKeyboardFocusWithin || cmb.IsDropDownOpen) return;

                var view = CollectionViewSource.GetDefaultView(cmb.ItemsSource);
                if (view != null) view.Filter = null;        // before re-selecting, or a filtered-out line can't be selected

                _resettingText = true;
                try
                {
                    if (_selectedLine != null)
                    {
                        if (!ReferenceEquals(cmb.SelectedItem, _selectedLine)) cmb.SelectedItem = _selectedLine;
                        cmb.Text = _selectedLine.Name;       // put back the real line if the user left half-typed text
                    }
                    else
                    {
                        cmb.Text = string.Empty;
                    }
                }
                finally { _resettingText = false; }
            }), DispatcherPriority.Background);
        }

        // Enter picks the first match: type "rc0 5", press Enter.
        private void LineCombo_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            var cmb = (ComboBox)sender;
            var view = CollectionViewSource.GetDefaultView(cmb.ItemsSource);
            if (view == null || view.Filter == null) return;   // only while the user is searching

            var first = view.Cast<LineProduction>().FirstOrDefault();
            if (first == null) return;

            cmb.SelectedItem = first;
            cmb.IsDropDownOpen = false;
            e.Handled = true;
        }

        /// <summary>Loads the saved schedule for the chosen date + line production (unsaved edits are dropped).</summary>
        private void LoadSchedule()
        {
            ClearRows();

            try
            {
                if (_loadedDate == null || _loadedLine == null)
                    return;

                var saved = Database.LoadSchedule(_loadedDate.Value, _loadedLine.Id);
                foreach (var item in saved)
                    _rows.Add(ScheduleRow.FromSaved(item));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal membaca jadwal:\n\n" + ex.Message, "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _dirty = false;   // what is on screen now is what is in the database
            }
        }

        /// <summary>Clear() raises a Reset without the old rows, so unhook the rows ourselves first.</summary>
        private void ClearRows()
        {
            foreach (var r in _rows) r.PropertyChanged -= Row_PropertyChanged;
            _rows.Clear();
        }

        // ------------------------------------------------------------------ buttons

        private void btnTambahJob_Click(object sender, RoutedEventArgs e)
        {
            if (dpTanggal.SelectedDate == null || cbLineProduction.SelectedValue == null)
            {
                MessageBox.Show("Pilih tanggal dan line production dulu.");
                return;
            }

            _rows.Add(new ScheduleRow { Urutan = _rows.Count + 1 });
        }

        private void btnHapusJob_Click(object sender, RoutedEventArgs e)
        {
            var toDelete = _rows.Where(r => r.IsSelected).ToList();
            if (toDelete.Count == 0)
            {
                MessageBox.Show("Centang baris yang ingin dihapus.");
                return;
            }

            foreach (var row in toDelete) _rows.Remove(row);
            RenumberRows();
        }

        private void btnSimpan_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrent(true);
        }

        /// <summary>Validates and saves the schedule that is on screen. Returns true when it was saved.</summary>
        private bool SaveCurrent(bool announce)
        {
            if (_loadedDate == null || _loadedLine == null)
            {
                MessageBox.Show("Pilih tanggal dan line production dulu.");
                return false;
            }

            // Make sure a cell that is still being edited is committed before we read the rows.
            dgSchedule.CommitEdit(DataGridEditingUnit.Row, true);

            RenumberRows();

            if (_rows.Any(r => r.Barcode == null))
            {
                MessageBox.Show("Ada baris yang belum lengkap. Pilih Customer / Part Number / Model / Job No sampai satu part tersisa.");
                return false;
            }

            // Every row needs a Jumlah
            var noJumlah = _rows.Where(r => r.Jumlah <= 0).Select(r => r.Urutan.ToString()).ToList();
            if (noJumlah.Count > 0)
            {
                MessageBox.Show("Jumlah belum diisi di urutan: " + string.Join(", ", noJumlah) + ".\n\nIsi Jumlah dulu sebelum menyimpan.",
                                "Belum bisa disimpan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var items = _rows.Select(r => new ScheduleItemData
            {
                Urutan = r.Urutan,
                Barcode = r.Barcode,
                Jumlah = r.Jumlah,
                CetakBox = r.CetakBox,
                Printer = r.Printer
            }).ToList();

            try
            {
                // Saved under the schedule that is on screen (not whatever the date / line controls show right now).
                Database.SaveSchedule(_loadedDate.Value, _loadedLine.Id, items);
                _dirty = false;
                if (announce) MessageBox.Show("Tersimpan.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menyimpan:\n\n" + ex.Message, "Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // The print icon at the end of a row: opens the sticker window for that row.
        private void btnPrintRow_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement) == null ? null : ((FrameworkElement)sender).DataContext as ScheduleRow;
            if (row == null) return;

            // The serials are printed from what is in the database, so the schedule must be saved first.
            if (_dirty)
            {
                MessageBox.Show("Simpan jadwal dulu sebelum mencetak.\n\nTekan Simpan, lalu klik tombol cetak lagi.",
                                "Belum bisa mencetak", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_loadedDate == null || _loadedLine == null || row.Barcode == null || row.Jumlah <= 0)
            {
                MessageBox.Show("Baris ini belum lengkap (part atau Jumlah belum diisi).", "Belum bisa mencetak",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var window = new PrintWindow(row, _loadedDate.Value, _loadedLine) { Owner = this };
            window.ShowDialog();
        }

        private void btnExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ------------------------------------------------------------------ totals

        private void Rows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (ScheduleRow r in e.OldItems) r.PropertyChanged -= Row_PropertyChanged;
            if (e.NewItems != null)
                foreach (ScheduleRow r in e.NewItems) r.PropertyChanged += Row_PropertyChanged;

            _dirty = true;
            UpdateTotals();
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Anything but the delete-checkbox counts as a change that is not saved yet.
            if (e.PropertyName != "IsSelected") _dirty = true;

            // "Jumlah" is raised whenever the amount changes AND whenever a row's part changes.
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "Jumlah")
                UpdateTotals();
        }

        private void UpdateTotals()
        {
            // Single pass over _rows instead of three separate LINQ sums.
            int totalJml = 0; double totalBox = 0, totalJam = 0;
            foreach (var r in _rows) { totalJml += r.Jumlah; totalBox += r.JumlahBox ?? 0; totalJam += r.JumlahJam ?? 0; }

            txtTotalJumlah.Text = totalJml.ToString(CultureInfo.CurrentCulture);
            // Total Box: at most 3 decimals (6 -> "6", 2.2117647 -> "2.212"). Total Jam: hours:minutes.
            txtTotalBox.Text = totalBox.ToString("0.###", CultureInfo.CurrentCulture);
            txtTotalJam.Text = ScheduleRow.FormatJam(totalJam);
        }

        // ------------------------------------------------------------------ Jumlah box: digits only

        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Rejects any text input that is not a digit
            e.Handled = !e.Text.All(c => c >= '0' && c <= '9');
        }

        private void NumberOnly_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // The space bar does not always go through PreviewTextInput
            if (e.Key == Key.Space) { e.Handled = true; return; }

            // Tab / Enter = Jumlah of the next row, Shift+Tab = the row above
            if (e.Key == Key.Tab || e.Key == Key.Enter)
            {
                bool goUp = e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

                var tb = sender as TextBox;
                var row = tb == null ? null : tb.DataContext as ScheduleRow;
                if (row == null) return;

                int index = _rows.IndexOf(row) + (goUp ? -1 : 1);
                if (index < 0 || index >= _rows.Count) return;   // first / last row: normal Tab behaviour

                e.Handled = true;
                FocusJumlah(_rows[index]);
            }
        }

        private void FocusJumlah(ScheduleRow row)
        {
            dgSchedule.ScrollIntoView(row);
            dgSchedule.UpdateLayout();

            var container = dgSchedule.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
            if (container == null) return;

            TextBox target = FindJumlahBox(container);
            if (target == null) return;

            target.Focus();
            target.SelectAll();   // typing replaces the old amount
        }

        // Finds the Jumlah box inside a row: the TextBox bound to JumlahText
        private static TextBox FindJumlahBox(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                var tb = child as TextBox;
                if (tb != null)
                {
                    var be = tb.GetBindingExpression(TextBox.TextProperty);
                    if (be != null && be.ParentBinding.Path != null && be.ParentBinding.Path.Path == "JumlahText")
                        return tb;
                }

                TextBox found = FindJumlahBox(child);
                if (found != null) return found;
            }
            return null;
        }

        private void NumberOnly_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text == null || !text.All(c => c >= '0' && c <= '9'))
                e.CancelCommand();
        }

        // ------------------------------------------------------------------ searchable filter dropdowns

        // Set by code while we change a ComboBox's text ourselves
        private bool _resettingText;

        private static void SetListFilter(ComboBox cmb, string text)
        {
            var view = CollectionViewSource.GetDefaultView(cmb.ItemsSource);
            if (view == null) return;

            if (string.IsNullOrEmpty(text))
                view.Filter = null;
            else
                view.Filter = item => item is string s && s.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SearchableComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var cmb = sender as ComboBox;
            if (cmb == null || cmb.ItemsSource == null || _resettingText) return;

            var row = cmb.DataContext as ScheduleRow;

            // The row is rebuilding its lists from code; this is not the user typing.
            if (row != null && row.IsUpdating) return;

            string text = cmb.Text;

            // The user deleted the text: drop that choice (this is what un-filters the other dropdowns).
            if (string.IsNullOrEmpty(text))
            {
                SetListFilter(cmb, null);
                if (row != null && cmb.IsKeyboardFocusWithin)
                    row.ClearPick(cmb.Tag as string);
                return;
            }

            // The text is just the item that was picked: show the whole list next time, and do NOT reopen it.
            string selected = cmb.SelectedItem as string;
            if (selected != null && selected == text)
            {
                SetListFilter(cmb, null);
                return;
            }

            // The user is typing: narrow the list to items that contain the text.
            SetListFilter(cmb, text);
            if (cmb.IsKeyboardFocusWithin) cmb.IsDropDownOpen = cmb.Items.Count > 0;
        }

        // Clicking the text part of the box opens the list too (not just the little arrow).
        private static TextBox GetEditBox(ComboBox cmb)
        {
            return cmb.Template.FindName("PART_EditableTextBox", cmb) as TextBox;
        }

        // Opens the list while the user is typing WITHOUT letting WPF select the text (the next letter would replace it).
        private static void OpenDropDownKeepCaret(ComboBox cmb)
        {
            if (cmb.IsDropDownOpen || cmb.Items.Count == 0) return;

            TextBox tb = GetEditBox(cmb);
            string textAtOpen = tb != null ? tb.Text : null;
            int start = tb != null ? tb.SelectionStart : 0;
            int length = tb != null ? tb.SelectionLength : 0;

            cmb.IsDropDownOpen = true;

            if (tb != null)
            {
                tb.Select(start, length);
                // WPF may select the text a moment later. Put the caret back, but only if the user has not typed since.
                cmb.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (tb.Text == textAtOpen) tb.Select(start, length);
                }), DispatcherPriority.Background);
            }
        }

        // A click on the text part: select the old text (so typing replaces it) and open the list.
        private void SearchableComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var cmb = (ComboBox)sender;
            if (cmb.IsDropDownOpen) return;

            // The arrow button opens and closes the list by itself
            DependencyObject d = e.OriginalSource as DependencyObject;
            while (d != null && !ReferenceEquals(d, cmb))
            {
                if (d is ToggleButton) return;
                d = VisualTreeHelper.GetParent(d);
            }

            // Do it after the click has finished: opening the list in the middle of a click is unreliable inside a DataGrid.
            MouseButtonEventHandler up = null;
            up = delegate
            {
                cmb.PreviewMouseLeftButtonUp -= up;
                cmb.Dispatcher.BeginInvoke(new Action(delegate
                {
                    TextBox tb = GetEditBox(cmb);
                    if (tb != null) tb.SelectAll();
                    if (cmb.Items.Count > 0) cmb.IsDropDownOpen = true;
                }), DispatcherPriority.Background);
            };
            cmb.PreviewMouseLeftButtonUp += up;
        }
        // When the user leaves the box with text that was never picked, put back what the row really holds.
        private void SearchableComboBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var cmb = (ComboBox)sender;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (cmb.IsKeyboardFocusWithin || cmb.IsDropDownOpen) return;   // still being used (focus moved into the list)

                var row = cmb.DataContext as ScheduleRow;
                if (row == null) return;

                string shown = row.GetShown(cmb.Tag as string) ?? string.Empty;
                if (cmb.Text == shown) return;

                _resettingText = true;
                try
                {
                    cmb.Text = shown;
                    BindingOperations.GetBindingExpression(cmb, Selector.SelectedItemProperty)?.UpdateTarget();
                }
                finally
                {
                    _resettingText = false;
                }
                SetListFilter(cmb, null);
            }), DispatcherPriority.Background);
        }

        private static void FilterCombo_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            var cmb = sender as ComboBox;
            if (cmb == null || !(cmb.DataContext is ScheduleRow)) return;   // only the filter boxes inside the grid

            // Only when the focus comes from a mouse click (a click on a list item gives the focus back after the button is released)
            if (Mouse.LeftButton != MouseButtonState.Pressed) return;

            // Subscribe to the window's PreviewMouseLeftButtonUp so we catch release anywhere on screen —
            // no busy-wait polling, one event that fires exactly once then removes itself.
            var win = Window.GetWindow(cmb);
            if (win == null) return;

            MouseButtonEventHandler[] handlerRef = { null };
            handlerRef[0] = delegate
            {
                win.PreviewMouseLeftButtonUp -= handlerRef[0];
                if (!cmb.IsKeyboardFocusWithin) return;   // focus moved on before the button was released

                TextBox tb = GetEditBox(cmb);
                if (tb != null) tb.SelectAll();
                if (cmb.Items.Count > 0) cmb.IsDropDownOpen = true;
            };
            win.PreviewMouseLeftButtonUp += handlerRef[0];
        }
    }
}