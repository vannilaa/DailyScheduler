using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;

namespace DailyScheduler
{
    /// <summary>
    /// One row of the schedule grid.
    ///
    /// The user can pick a value in ANY of Customer, Part Number, Model or Job No.
    ///  - Every pick narrows the dropdowns of the OTHER three fields.
    ///  - A field whose value is the same for every product that is still possible fills itself in
    ///    (for example a unique Part Number or Job No fills in everything).
    ///  - When exactly one product is left, the row is complete: barcode, qty/box, target/jam, printer ...
    ///
    /// "Picks" (what the user chose) are kept separate from "shown" values (what the cells display),
    /// so auto-filled values never lock anything and the user can always change their mind.
    /// </summary>
    public class ScheduleRow : INotifyPropertyChanged
    {
        /// <summary>The whole product list. Filled once at startup (MainWindow_Loaded).</summary>
        public static List<ProductInfo> AllProducts = new List<ProductInfo>();

        /// <summary>Products by barcode, used when loading a saved schedule.</summary>
        public static Dictionary<string, ProductInfo> Products = new Dictionary<string, ProductInfo>();

        /// <summary>Printer names for the Printer dropdown. Filled once at startup (MainWindow_Loaded).</summary>
        public static readonly List<string> PrinterList = new List<string>();

        // The four filter fields, by index.
        private const int Cust = 0, PartNo = 1, ModelIdx = 2, Job = 3;

        // ---- product index (built once after AllProducts is loaded) ----
        // _fieldIndex[f]["Toyota"] = all products whose field f == "Toyota"
        // Allows GetMatchingProducts to start from the smallest matching subset
        // rather than scanning all of AllProducts every time.
        private static Dictionary<string, List<ProductInfo>>[] _fieldIndex;

        /// <summary>
        /// Call once after AllProducts is populated. Builds a per-field lookup so
        /// Recompute can start from the smallest matching subset instead of scanning
        /// all products 5 times on every keystroke.
        /// </summary>
        public static void RebuildIndex()
        {
            _fieldIndex = new Dictionary<string, List<ProductInfo>>[4];
            for (int f = 0; f < 4; f++)
            {
                _fieldIndex[f] = new Dictionary<string, List<ProductInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in AllProducts)
                {
                    string v = Field(p, f);
                    if (v == null) continue;
                    List<ProductInfo> bucket;
                    if (!_fieldIndex[f].TryGetValue(v, out bucket))
                        _fieldIndex[f][v] = bucket = new List<ProductInfo>();
                    bucket.Add(p);
                }
            }
        }

        /// <summary>
        /// Returns all products that satisfy every active pick, except the one for field
        /// <paramref name="skip"/> (pass -1 to apply all picks).  When the index is
        /// available the method starts from the smallest indexed bucket and only checks
        /// the remaining picks — O(subset) instead of O(AllProducts).
        /// </summary>
        private List<ProductInfo> GetMatchingProducts(int skip)
        {
            if (_fieldIndex == null)
                return AllProducts.Where(p => Matches(p, skip)).ToList();

            // Find the indexed pick whose bucket is smallest (excluding 'skip').
            List<ProductInfo> startSet = null;
            int startField = -1;
            for (int f = 0; f < 4; f++)
            {
                if (f == skip || _pick[f] == null) continue;
                List<ProductInfo> bucket;
                if (!_fieldIndex[f].TryGetValue(_pick[f], out bucket))
                    return new List<ProductInfo>();   // pick value not in master list → no candidates
                if (startSet == null || bucket.Count < startSet.Count)
                { startSet = bucket; startField = f; }
            }

            if (startSet == null) return AllProducts;   // no applicable picks → everything matches

            // Filter the smallest bucket by the remaining picks (skipping startField and skip).
            int capturedSkip = skip, capturedStart = startField;
            return startSet.Where(p =>
            {
                for (int f = 0; f < 4; f++)
                {
                    if (f == capturedSkip || f == capturedStart || _pick[f] == null) continue;
                    if (Field(p, f) != _pick[f]) return false;
                }
                return true;
            }).ToList();
        }

        private readonly string[] _pick = new string[4];                      // what the user chose (null = nothing)
        private readonly string[] _shown = new string[4];                     // what the cell displays
        private readonly List<string>[] _options = new List<string>[4];       // dropdown items per field

        private List<ProductInfo> _candidates = new List<ProductInfo>();      // products that still match every pick
        private ProductInfo _product;                                         // set only when the row is complete

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private bool _recomputeQueued;

        private int _urutan;
        private bool _isSelected;
        private int _jumlah;
        private bool _cetakBox;
        private string _printer;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>True while the row is rebuilding its dropdowns from code (the UI uses this to ignore the resulting text changes).</summary>
        public bool IsUpdating { get; private set; }

        public ScheduleRow() : this(true) { }

        // computeNow = false: the caller runs Recompute itself (FromSaved), so the work is not done twice.
        private ScheduleRow(bool computeNow)
        {
            if (computeNow) Recompute(false, null);
        }

        /// <summary>Creates a row from a saved schedule item.</summary>
        public static ScheduleRow FromSaved(ScheduleItemData item)
        {
            var row = new ScheduleRow(false);
            row._urutan = item.Urutan;
            row._jumlah = item.Jumlah;
            row._cetakBox = item.CetakBox;
            row._printer = item.Printer;

            // false = keep the saved printer; no picks, so every dropdown stays fully open.
            // (A barcode that is no longer in the master list still gets its dropdowns built.)
            ProductInfo p;
            row.Recompute(false, Products.TryGetValue(item.Barcode, out p) ? p : null);

            return row;
        }

        // ================================================================== the four filter dropdowns

        public List<string> CustomerOptions { get { return _options[Cust]; } }
        public List<string> PartNumberOptions { get { return _options[PartNo]; } }
        public List<string> ModelOptions { get { return _options[ModelIdx]; } }
        public List<string> JobNoOptions { get { return _options[Job]; } }

        // Selected values. Setting one = the user picked something in that cell.
        public string Customer { get { return _shown[Cust]; } set { SetPick(Cust, value); } }
        public string PartNumber { get { return _shown[PartNo]; } set { SetPick(PartNo, value); } }
        public string Model { get { return _shown[ModelIdx]; } set { SetPick(ModelIdx, value); } }
        public string JobNo { get { return _shown[Job]; } set { SetPick(Job, value); } }

        private static int FieldIndex(string fieldName)
        {
            switch (fieldName)
            {
                case "Customer": return Cust;
                case "PartNumber": return PartNo;
                case "Model": return ModelIdx;
                case "JobNo": return Job;
                default: return -1;
            }
        }

        /// <summary>The text a filter cell should currently show.</summary>
        public string GetShown(string fieldName)
        {
            int f = FieldIndex(fieldName);
            return f < 0 ? null : _shown[f];
        }

        private void SetPick(int field, string value)
        {
            // A ComboBox reports null when its text is edited or its list is swapped. That is not a choice.
            // (Deleting the text on purpose goes through ClearPick.)
            if (string.IsNullOrEmpty(value)) return;
            if (value == _pick[field]) return;

            _pick[field] = value;
            _shown[field] = value;
            QueueRecompute();
        }

        /// <summary>
        /// The user deleted the text of a filter cell.
        ///  - If that cell held one of their picks: drop that pick.
        ///  - If it only showed an auto-filled value: start over (drop all picks of the row).
        /// </summary>
        public void ClearPick(string fieldName)
        {
            int f = FieldIndex(fieldName);
            if (f < 0) return;

            if (_pick[f] != null)
                _pick[f] = null;
            else
                for (int i = 0; i < 4; i++) _pick[i] = null;

            Recompute(false, null);
        }

        // Recalculate after the ComboBox has finished handling the click (changing its list in the middle of it is fragile).
        private void QueueRecompute()
        {
            if (_recomputeQueued) return;
            _recomputeQueued = true;
            _dispatcher.BeginInvoke(new Action(delegate
            {
                _recomputeQueued = false;
                Recompute(true, null);
            }), DispatcherPriority.Background);
        }

        private static string Field(ProductInfo p, int field)
        {
            switch (field)
            {
                case Cust: return p.Customer;
                case PartNo: return p.PartNumber;
                case ModelIdx: return p.Model;
                default: return p.JobNo;
            }
        }

        /// <summary>Does the product match every pick, ignoring the field "skip" (-1 = ignore none)?</summary>
        private bool Matches(ProductInfo p, int skip)
        {
            for (int f = 0; f < 4; f++)
            {
                if (f == skip || _pick[f] == null) continue;
                if (Field(p, f) != _pick[f]) return false;
            }
            return true;
        }

        private void Recompute(bool setDefaultPrinter, ProductInfo forced)
        {
            IsUpdating = true;
            try
            {
                ProductInfo before = _product;

                // Dropdown items per field: the values that still fit the OTHER picks (so you can always change a pick).
                for (int f = 0; f < 4; f++)
                {
                    var matching = GetMatchingProducts(f);   // uses index; much cheaper than scanning AllProducts
                    var values = new List<string>();
                    var seen = new HashSet<string>();
                    foreach (ProductInfo p in matching)
                    {
                        string v = Field(p, f);
                        if (v != null && seen.Add(v)) values.Add(v);
                    }
                    values.Sort(StringComparer.OrdinalIgnoreCase);
                    _options[f] = values;
                }

                // Products that still fit ALL the user's picks.
                var cand = GetMatchingProducts(-1);

                bool hasManualPick = _pick.Any(p => p != null);

                // Auto-fill: when the user has picked at least one field (not on initial empty choosing),
                // any field where remaining candidates offer exactly ONE non-empty value should be auto-filled.
                // This narrows the remaining candidates, which can in turn settle other fields, so repeat until stable.
                if (hasManualPick)
                {
                    bool narrowed = true;
                    while (narrowed)
                    {
                        narrowed = false;
                        for (int f = 0; f < 4; f++)
                        {
                            if (_pick[f] != null) continue;

                            var values = cand.Select(p => Field(p, f)).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
                            if (values.Count == 1)
                            {
                                string only = values[0];
                                if (cand.Any(p => Field(p, f) != only))
                                {
                                    cand = cand.Where(p => Field(p, f) == only).ToList();
                                    narrowed = true;
                                }
                            }
                        }
                    }
                }
                _candidates = cand;

                // What each cell shows:
                // If manually picked: that pick.
                // If not initial choosing and candidates offer exactly 1 non-empty value (or options list has 1 item): that value.
                // Otherwise: null.
                for (int f = 0; f < 4; f++)
                {
                    if (_pick[f] != null)
                    {
                        _shown[f] = _pick[f];
                    }
                    else if (hasManualPick)
                    {
                        var values = _candidates.Select(p => Field(p, f)).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
                        if (values.Count == 1)
                            _shown[f] = values[0];
                        else if (_options[f].Count == 1)
                            _shown[f] = _options[f][0];
                        else
                            _shown[f] = null;
                    }
                    else
                    {
                        _shown[f] = null;
                    }
                }

                // One product left = the row is complete. (If several products look identical in all four
                // fields, the first of them is used.)
                bool everythingDecided = _candidates.Count > 0;
                for (int f = 0; f < 4; f++)
                {
                    if (string.IsNullOrEmpty(_shown[f]))
                        everythingDecided = false;
                }

                if (_candidates.Count == 1 || (_candidates.Count > 1 && everythingDecided))
                    _product = _candidates[0];
                else
                    _product = null;

                if (forced != null)
                {
                    _product = forced;
                    for (int f = 0; f < 4; f++) _shown[f] = Field(forced, f);
                }

                if (setDefaultPrinter && _product != null && !ReferenceEquals(_product, before))
                    _printer = _product.Printer;

                // Tell the grid: dropdown items first, then the selected values (so selections are not lost).
                OnPropertyChanged("CustomerOptions");
                OnPropertyChanged("PartNumberOptions");
                OnPropertyChanged("ModelOptions");
                OnPropertyChanged("JobNoOptions");

                OnPropertyChanged("Customer");
                OnPropertyChanged("PartNumber");
                OnPropertyChanged("Model");
                OnPropertyChanged("JobNo");
                OnPropertyChanged("Part");
                OnPropertyChanged("Assembly");
                OnPropertyChanged("Barcode");
                OnPropertyChanged("NomorBarcode");
                OnPropertyChanged("QtyPerBox");
                OnPropertyChanged("TargetPerJam");
                OnPropertyChanged("JumlahBox");
                OnPropertyChanged("JumlahBoxNotWhole");
                OnPropertyChanged("JumlahJam");
                OnPropertyChanged("JumlahJamText");
                OnPropertyChanged("Printer");
                OnPropertyChanged("IsPartial");
                OnPropertyChanged("CandidateHint");
                OnPropertyChanged("Jumlah");     // also tells the window to update the totals
            }
            finally
            {
                IsUpdating = false;
            }
        }

        // ================================================================== "still undecided" tint

        /// <summary>True when the user has picked something but more than one product still matches. The grid tints the row.</summary>
        public bool IsPartial
        {
            get { return _product == null && _pick.Any(p => p != null); }
        }

        /// <summary>Tooltip for a tinted row; null (no tooltip) otherwise.</summary>
        public string CandidateHint
        {
            get
            {
                return IsPartial
                    ? _candidates.Count + " produk masih cocok. Pilih Customer / Part Number / Model / Job No lagi."
                    : null;
            }
        }

        // ================================================================== read-only info

        /// <summary>Barcode of the chosen product, or null while the row is still incomplete.</summary>
        public string Barcode { get { return _product == null ? null : _product.Barcode; } }
        public string NomorBarcode { get { return Barcode; } }

        public string Part { get { return SingleOrNull(p => p.Part); } }
        public string Assembly { get { return SingleOrNull(p => p.Assembly); } }

        public int? QtyPerBox { get { return _product == null ? (int?)null : _product.QtyPerBox; } }
        public int? TargetPerJam { get { return _product == null ? (int?)null : _product.TargetPerJam; } }

        // Calculated exactly, no rounding. Blank while the row is incomplete or the product's value is 0 ("not filled in yet").
        public double? JumlahBox
        {
            get
            {
                if (_product == null || _product.QtyPerBox <= 0) return null;
                return (double)_jumlah / _product.QtyPerBox;
            }
        }

        /// <summary>True when Jumlah does not fill a whole number of boxes (e.g. 25 pcs at 12 per box). The grid shows Jml Box in red.</summary>
        public bool JumlahBoxNotWhole
        {
            get
            {
                if (_product == null || _product.QtyPerBox <= 0) return false;
                return _jumlah % _product.QtyPerBox != 0;   // whole-number check, no floating point
            }
        }

        public double? JumlahJam
        {
            get
            {
                if (_product == null || _product.TargetPerJam <= 0) return null;
                return (double)_jumlah / _product.TargetPerJam;
            }
        }

        /// <summary>Jml Jam for display as hours:minutes (decimal hours are hard to read: 0.1 h = 6 minutes).</summary>
        public string JumlahJamText
        {
            get
            {
                double? h = JumlahJam;
                return h.HasValue ? FormatJam(h.Value) : string.Empty;
            }
        }

        /// <summary>Decimal hours -> "H:MM", e.g. 1.4167 -> "1:25". Minutes are rounded to the nearest whole minute.</summary>
        public static string FormatJam(double hours)
        {
            int totalMinutes = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", totalMinutes / 60, totalMinutes % 60);
        }

        // The value if every remaining candidate agrees on it, otherwise null.
        private string SingleOrNull(Func<ProductInfo, string> selector)
        {
            if (_product != null) return selector(_product);
            var distinct = _candidates.Select(selector).Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : null;
        }

        // ================================================================== editable by the user

        /// <summary>Jumlah as a number (used for saving and for the totals).</summary>
        public int Jumlah
        {
            get { return _jumlah; }
            set
            {
                _jumlah = value;
                OnPropertyChanged("Jumlah");
                OnPropertyChanged("JumlahText");
                OnPropertyChanged("JumlahBox");
                OnPropertyChanged("JumlahBoxNotWhole");
                OnPropertyChanged("JumlahJam");
                OnPropertyChanged("JumlahJamText");
            }
        }

        /// <summary>
        /// What the Jumlah box is bound to. Empty means 0, so deleting the text never leaves the
        /// number and the screen out of step.
        /// </summary>
        public string JumlahText
        {
            get { return _jumlah == 0 ? string.Empty : _jumlah.ToString(CultureInfo.InvariantCulture); }
            set
            {
                int n = 0;
                if (!string.IsNullOrEmpty(value) &&
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out n))
                    return;   // not a plain number: ignore

                _jumlah = n;
                OnPropertyChanged("Jumlah");      // totals
                OnPropertyChanged("JumlahBox");
                OnPropertyChanged("JumlahBoxNotWhole");
                OnPropertyChanged("JumlahJam");
                OnPropertyChanged("JumlahJamText");
                // JumlahText is not raised here on purpose: it would rewrite what the user is typing.
            }
        }

        public bool CetakBox
        {
            get { return _cetakBox; }
            set { _cetakBox = value; OnPropertyChanged("CetakBox"); }
        }

        public string Printer
        {
            get { return _printer; }
            set
            {
                // A ComboBox reports null when its list is swapped; that is not a choice. Same value = no change.
                if (value == null || value == _printer) return;
                _printer = value;
                OnPropertyChanged("Printer");
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged("IsSelected"); }
        }

        public int Urutan
        {
            get { return _urutan; }
            set { _urutan = value; OnPropertyChanged("Urutan"); }
        }
    }
}