using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DailyScheduler
{
    /// <summary>
    /// All database access lives here. If you ever move to SQL Server Express,
    /// this is the only file that has to change.
    /// </summary>
    public static class Database
    {
        // scheduler.db is copied next to the .exe (set "Copy to Output Directory" on the file).
        private static string DbPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scheduler.db"); }
        }

        private static SqliteConnection Open()
        {
            // Mode=ReadWrite: fail loudly if the file is missing instead of silently creating an empty database.
            var con = new SqliteConnection("Data Source=" + DbPath + ";Mode=ReadWrite");
            con.Open();

            // SQLite only enforces foreign keys when this is switched on, for every connection.
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys = ON";
                cmd.ExecuteNonQuery();
            }
            return con;
        }

        private static string Str(SqliteDataReader r, int i)
        {
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        private static string DateKey(DateTime d)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- lookups

        public static List<LineProduction> GetLineProductions()
        {
            var list = new List<LineProduction>();
            using (var con = Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name, map_code FROM line_production ORDER BY id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new LineProduction { Id = r.GetInt32(0), Name = r.GetString(1), MapCode = r.GetString(2) });
                }
            }
            return list;
        }

        public static List<ProductInfo> GetProducts()
        {
            var list = new List<ProductInfo>();
            using (var con = Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT barcode, COALESCE(part_number_code, part_number) || COALESCE(' - ' || model, ''), " +
                    "       customer, model, part, job_no, assembly, qty_per_box, target_per_jam, printer, " +
                    "       COALESCE(part_number_code, part_number), discontinued, position " +
                    "FROM product ORDER BY 11, 4";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ProductInfo
                        {
                            Barcode = r.GetString(0),
                            DisplayName = r.GetString(1),
                            Customer = Str(r, 2),
                            Model = Str(r, 3),
                            Part = Str(r, 4),
                            JobNo = Str(r, 5),
                            Assembly = Str(r, 6),
                            QtyPerBox = r.GetInt32(7),
                            TargetPerJam = r.GetInt32(8),
                            Printer = Str(r, 9),
                            PartNumber = r.GetString(10),
                            Discontinued = r.GetInt32(11) != 0,
                            Position = Str(r, 12)
                        });
                    }
                }
            }
            return list;
        }

        public static List<string> GetPrinters()
        {
            var list = new List<string>();
            using (var con = Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT printer FROM product WHERE printer IS NOT NULL ORDER BY printer";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(r.GetString(0));
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- schedule

        /// <summary>Rows of the schedule for this date + line production (empty list if none saved yet).</summary>
        public static List<ScheduleItemData> LoadSchedule(DateTime tanggal, int lineProductionId)
        {
            var list = new List<ScheduleItemData>();
            using (var con = Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT i.urutan, i.barcode, i.jumlah, i.cetak_box, i.printer " +
                    "FROM schedule s JOIN schedule_item i ON i.schedule_id = s.id " +
                    "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp " +
                    "ORDER BY i.urutan";
                cmd.Parameters.AddWithValue("@tanggal", DateKey(tanggal));
                cmd.Parameters.AddWithValue("@lp", lineProductionId);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new ScheduleItemData
                        {
                            Urutan = r.GetInt32(0),
                            Barcode = r.GetString(1),
                            Jumlah = r.GetInt32(2),
                            CetakBox = r.GetInt32(3) != 0,
                            Printer = Str(r, 4)
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>Replaces the whole schedule for this date + line production, all-or-nothing.</summary>
        public static void SaveSchedule(DateTime tanggal, int lineProductionId, IList<ScheduleItemData> items)
        {
            using (var con = Open())
            using (var tx = con.BeginTransaction())
            {
                // 1. make sure the schedule header exists
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO schedule (tanggal, line_production_id) VALUES (@tanggal, @lp)";
                    cmd.Parameters.AddWithValue("@tanggal", DateKey(tanggal));
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.ExecuteNonQuery();
                }

                // 2. find its id
                long scheduleId;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT id FROM schedule WHERE tanggal = @tanggal AND line_production_id = @lp";
                    cmd.Parameters.AddWithValue("@tanggal", DateKey(tanggal));
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    scheduleId = (long)cmd.ExecuteScalar();
                }

                // 3. remove the old rows
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM schedule_item WHERE schedule_id = @id";
                    cmd.Parameters.AddWithValue("@id", scheduleId);
                    cmd.ExecuteNonQuery();
                }

                // 4. insert the current rows
                foreach (var item in items)
                {
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            "INSERT INTO schedule_item (schedule_id, urutan, barcode, jumlah, cetak_box, printer) " +
                            "VALUES (@id, @urutan, @barcode, @jumlah, @cetak, @printer)";
                        cmd.Parameters.AddWithValue("@id", scheduleId);
                        cmd.Parameters.AddWithValue("@urutan", item.Urutan);
                        cmd.Parameters.AddWithValue("@barcode", item.Barcode);
                        cmd.Parameters.AddWithValue("@jumlah", item.Jumlah);
                        cmd.Parameters.AddWithValue("@cetak", item.CetakBox ? 1 : 0);
                        cmd.Parameters.AddWithValue("@printer", (object)item.Printer ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();   // if anything above threw, the transaction is rolled back on Dispose
            }
        }
    }
}