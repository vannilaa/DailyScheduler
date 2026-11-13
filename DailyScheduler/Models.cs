namespace DailyScheduler
{
    /// <summary>One entry of the Line Production dropdown (table line_production).</summary>
    public class LineProduction
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string MapCode { get; set; }       // used in the sticker serial, e.g. "12"
    }

    /// <summary>One product from the master list (view v_product_pick).</summary>
    public class ProductInfo
    {
        public string Barcode { get; set; }
        public string DisplayName { get; set; }   // "<part number code> - <Model name>"
        public string PartNumber { get; set; }    // part number code if there is one, otherwise the plain part number
        public string Customer { get; set; }
        public string Model { get; set; }
        public string Part { get; set; }
        public string JobNo { get; set; }
        public string Assembly { get; set; }
        public int QtyPerBox { get; set; }        // 0 = not filled in yet
        public int TargetPerJam { get; set; }     // 0 = not filled in yet
        public string Printer { get; set; }       // default printer, may be null
        public bool Discontinued { get; set; }
        public string Position { get; set; }      // F / R / R2 / COVER ... (printed on the sticker), may be null
    }

    /// <summary>A saved schedule row, exactly as stored in schedule_item.</summary>
    public class ScheduleItemData
    {
        public int Urutan { get; set; }
        public string Barcode { get; set; }
        public int Jumlah { get; set; }
        public bool CetakBox { get; set; }
        public string Printer { get; set; }
    }
}