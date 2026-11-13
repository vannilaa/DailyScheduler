using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;

namespace DailyScheduler
{
    /// <summary>
    /// All database access lives here (SQL Server / SQL Server Express).
    /// Connection settings are read from  %LocalAppData%\DailyScheduler\db.ini  (see ConnectionString below).
    /// </summary>
    public static class Database
    {
        // ---------------------------------------------------------------- connection

        private const string SettingsTemplate =
            "# Pengaturan database SQL Server (DailyScheduler)\r\n" +
            "#\r\n" +
            "# Server  : nama atau IP komputer server. Contoh:\r\n" +
            "#             .\\SQLEXPRESS                 (SQL Express di komputer ini)\r\n" +
            "#             192.168.1.10\\SQLEXPRESS      (komputer lain, pakai nama instance)\r\n" +
            "#             192.168.1.10,1433            (komputer lain, pakai nomor port)\r\n" +
            "# Database: nama database (biarkan DailyScheduler)\r\n" +
            "# User / Password: login SQL Server. Kosongkan keduanya untuk memakai login Windows.\r\n" +
            "\r\n" +
            "Server=.\\SQLEXPRESS\r\n" +
            "Database=DailyScheduler\r\n" +
            "User=scheduler_app\r\n" +
            "Password=\r\n";

        private static string _connectionString;

        /// <summary>The settings file, so the error message can tell the user where to look.</summary>
        public static string SettingsPath
        {
            get
            {
                // 1) db.ini next to the .exe: one file for every computer (put it in the publish folder)
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db.ini");
                if (File.Exists(beside)) return beside;

                // 2) otherwise the per-user file, as before
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DailyScheduler");
                return Path.Combine(dir, "db.ini");
            }
        }

        private static string ConnectionString
        {
            get
            {
                if (_connectionString != null) return _connectionString;

                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, SettingsTemplate, new UTF8Encoding(false));
                    throw new InvalidOperationException(
                        "Pengaturan database belum ada.\n\nSebuah file contoh sudah dibuat di:\n" + path +
                        "\n\nIsi Server, User dan Password di file itu, simpan, lalu buka aplikasi lagi.");
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                string server, database, user, password;
                values.TryGetValue("Server", out server);
                values.TryGetValue("Database", out database);
                values.TryGetValue("User", out user);
                values.TryGetValue("Password", out password);

                if (string.IsNullOrEmpty(server))
                    throw new InvalidOperationException("Isi \"Server=\" di file pengaturan database:\n" + path);

                var builder = new SqlConnectionStringBuilder();
                builder.DataSource = server;
                builder.InitialCatalog = string.IsNullOrEmpty(database) ? "DailyScheduler" : database;
                builder.ConnectTimeout = 8;                 // do not hang for a long time when the server is off / wrong IP
                builder.TrustServerCertificate = true;      // SQL Express uses a self-signed certificate
                builder.Encrypt = false;                    // skip the TLS handshake that fails on this setup
                builder.ApplicationName = "DailyScheduler";
                if (string.IsNullOrEmpty(user))
                {
                    builder.IntegratedSecurity = true;      // Windows login
                }
                else
                {
                    builder.UserID = user;
                    builder.Password = password ?? "";
                }

                _connectionString = builder.ConnectionString;
                return _connectionString;
            }
        }

        private static SqlConnection Open()
        {
            var con = new SqlConnection(ConnectionString);
            con.Open();
            return con;
        }

        private static string Str(SqlDataReader r, int i)
        {
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        private static void AddDate(SqlCommand cmd, string name, DateTime d)
        {
            cmd.Parameters.Add(name, SqlDbType.Date).Value = d.Date;
        }

        // ---------------------------------------------------------------- lookups

        /// <summary>
        /// Loads all three startup datasets in a single round-trip (one connection, one
        /// multi-statement batch, three result sets) instead of three separate calls.
        /// </summary>
        public static (List<LineProduction> Lines, List<ProductInfo> Products, List<string> Printers) GetStartupData()
        {
            var lines = new List<LineProduction>();
            var products = new List<ProductInfo>();
            var printers = new List<string>();

            using (var con = Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText =
                    // result set 1 — line_production
                    "SELECT id, name, map_code FROM line_production ORDER BY id; " +
                    // result set 2 — product master
                    "SELECT barcode, COALESCE(part_number_code, part_number) + COALESCE(' - ' + model, ''), " +
                    "       customer, model, part, job_no, assembly, qty_per_box, target_per_jam, printer, " +
                    "       COALESCE(part_number_code, part_number), discontinued, position " +
                    "FROM product ORDER BY COALESCE(part_number_code, part_number), model; " +
                    // result set 3 — distinct printers
                    "SELECT DISTINCT printer FROM product WHERE printer IS NOT NULL ORDER BY printer";

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        lines.Add(new LineProduction { Id = r.GetInt32(0), Name = r.GetString(1), MapCode = r.GetString(2) });

                    r.NextResult();
                    while (r.Read())
                        products.Add(new ProductInfo
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
                            Discontinued = r.GetBoolean(11),
                            Position = Str(r, 12)
                        });

                    r.NextResult();
                    while (r.Read()) printers.Add(r.GetString(0));
                }
            }
            return (lines, products, printers);
        }

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
                    "SELECT barcode, COALESCE(part_number_code, part_number) + COALESCE(' - ' + model, ''), " +
                    "       customer, model, part, job_no, assembly, qty_per_box, target_per_jam, printer, " +
                    "       COALESCE(part_number_code, part_number), discontinued, position " +
                    "FROM product ORDER BY COALESCE(part_number_code, part_number), model";
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
                            Discontinued = r.GetBoolean(11),
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
                AddDate(cmd, "@tanggal", tanggal);
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
                            CetakBox = r.GetBoolean(3),
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
                // 1. make sure the schedule header exists (the lock stops two PCs from creating it at the same moment)
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "IF NOT EXISTS (SELECT 1 FROM schedule WITH (UPDLOCK, HOLDLOCK) WHERE tanggal = @tanggal AND line_production_id = @lp) " +
                        "INSERT INTO schedule (tanggal, line_production_id) VALUES (@tanggal, @lp)";
                    AddDate(cmd, "@tanggal", tanggal);
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.ExecuteNonQuery();
                }

                // 2. find its id
                int scheduleId;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT id FROM schedule WHERE tanggal = @tanggal AND line_production_id = @lp";
                    AddDate(cmd, "@tanggal", tanggal);
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    scheduleId = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // 3. remove the old rows
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM schedule_item WHERE schedule_id = @id";
                    cmd.Parameters.AddWithValue("@id", scheduleId);
                    cmd.ExecuteNonQuery();
                }

                // 4. insert the current rows in chunks (same multi-row approach as FillNumberTable)
                // 6 params per row × 100 rows = 600 params — well below SQL Server's 2 100-param limit.
                const int insertChunk = 100;
                for (int ci = 0; ci < items.Count; ci += insertChunk)
                {
                    int count = Math.Min(insertChunk, items.Count - ci);
                    var sql = new StringBuilder(
                        "INSERT INTO schedule_item (schedule_id, urutan, barcode, jumlah, cetak_box, printer) VALUES ");
                    using (var cmd = con.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        for (int j = 0; j < count; j++)
                        {
                            var item = items[ci + j];
                            if (j > 0) sql.Append(',');
                            sql.Append("(@id").Append(j).Append(",@u").Append(j)
                               .Append(",@b").Append(j).Append(",@j").Append(j)
                               .Append(",@c").Append(j).Append(",@p").Append(j).Append(')');
                            cmd.Parameters.AddWithValue("@id" + j, scheduleId);
                            cmd.Parameters.AddWithValue("@u" + j, item.Urutan);
                            cmd.Parameters.AddWithValue("@b" + j, item.Barcode);
                            cmd.Parameters.AddWithValue("@j" + j, item.Jumlah);
                            cmd.Parameters.AddWithValue("@c" + j, item.CetakBox);
                            cmd.Parameters.AddWithValue("@p" + j, (object)item.Printer ?? DBNull.Value);
                        }
                        cmd.CommandText = sql.ToString();
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();   // if anything above threw, the transaction is rolled back on Dispose
            }
        }

        // ---------------------------------------------------------------- print history

        /// <summary>Puts the sticker numbers (and their serials) into a temporary table #pn, so one query can handle thousands of them.</summary>
        private static void FillNumberTable(SqlConnection con, SqlTransaction tx, IList<int> numbers, Func<int, string> serialOf)
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "CREATE TABLE #pn (no INT NOT NULL PRIMARY KEY, serial NVARCHAR(150) NOT NULL)";
                cmd.ExecuteNonQuery();
            }

            const int chunk = 300;   // 2 parameters per row: stays far below SQL Server's limit of 2100
            for (int i = 0; i < numbers.Count; i += chunk)
            {
                int count = Math.Min(chunk, numbers.Count - i);
                var sql = new StringBuilder("INSERT INTO #pn (no, serial) VALUES ");
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    for (int j = 0; j < count; j++)
                    {
                        int number = numbers[i + j];
                        if (j > 0) sql.Append(',');
                        sql.Append("(@n").Append(j).Append(",@s").Append(j).Append(')');
                        cmd.Parameters.AddWithValue("@n" + j, number);
                        cmd.Parameters.AddWithValue("@s" + j, serialOf(number));
                    }
                    cmd.CommandText = sql.ToString();
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Which of these sticker numbers were printed before (same date + line + product)? Empty list = all new.</summary>
        public static List<PrintedSticker> FindPrinted(DateTime tanggal, int lineProductionId, string barcode, IList<int> numbers)
        {
            var list = new List<PrintedSticker>();
            if (numbers.Count == 0) return list;

            using (var con = Open())
            {
                FillNumberTable(con, null, numbers, n => "");   // serials are not needed just to look up

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT s.sticker_no, s.print_count, s.last_printed_at, j.windows_user " +
                        "FROM print_sticker s " +
                        "JOIN #pn n ON n.no = s.sticker_no " +
                        "JOIN print_job j ON j.id = s.last_job_id " +
                        "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode " +
                        "ORDER BY s.sticker_no";
                    AddDate(cmd, "@tanggal", tanggal);
                    cmd.Parameters.AddWithValue("@lp", lineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", barcode);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new PrintedSticker
                            {
                                Number = r.GetInt32(0),
                                PrintCount = r.GetInt32(1),
                                LastPrintedAt = r.GetDateTime(2),
                                LastUser = r.GetString(3)
                            });
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Records one print: the job, every sticker (new ones are added, reprinted ones get print_count + 1) and which
        /// stickers belong to this job. All-or-nothing. Returns the new print_job id.
        /// </summary>
        public static int LogPrint(PrintLogEntry e)
        {
            using (var con = Open())
            using (var tx = con.BeginTransaction(IsolationLevel.Serializable))   // two PCs printing the same numbers cannot slip past each other
            {
                FillNumberTable(con, tx, e.Numbers, e.SerialOf);

                // how many of these numbers were printed before this job?
                int reprints;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM print_sticker s JOIN #pn n ON n.no = s.sticker_no " +
                        "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode";
                    AddDate(cmd, "@tanggal", e.Tanggal);
                    cmd.Parameters.AddWithValue("@lp", e.LineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", e.Barcode);
                    reprints = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // the job itself
                int jobId;
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO print_job (tanggal, line_production_id, line_name, map_code, barcode, part_number, model, printer, " +
                        "                       sticker_size, sticker_count, is_reprint, reprint_count, reprint_reason, reprint_note, windows_user, machine_name) " +
                        "OUTPUT INSERTED.id " +
                        "VALUES (@tanggal, @lp, @lineName, @mapCode, @barcode, @partNumber, @model, @printer, " +
                        "        @size, @count, @isReprint, @reprints, @reason, @note, @user, @machine)";
                    AddDate(cmd, "@tanggal", e.Tanggal);
                    cmd.Parameters.AddWithValue("@lp", e.LineProductionId);
                    cmd.Parameters.AddWithValue("@lineName", e.LineName ?? "");
                    cmd.Parameters.AddWithValue("@mapCode", e.MapCode ?? "");
                    cmd.Parameters.AddWithValue("@barcode", e.Barcode);
                    cmd.Parameters.AddWithValue("@partNumber", e.PartNumber ?? "");
                    cmd.Parameters.AddWithValue("@model", (object)e.Model ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@printer", (object)e.Printer ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@size", e.StickerSize ?? "");
                    cmd.Parameters.AddWithValue("@count", e.Numbers.Count);
                    cmd.Parameters.AddWithValue("@isReprint", reprints > 0);
                    cmd.Parameters.AddWithValue("@reprints", reprints);
                    cmd.Parameters.AddWithValue("@reason", (object)e.Reason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@note", (object)e.Note ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user", Environment.UserName);
                    cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
                    jobId = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // stickers: reprinted ones count up, new ones are added, then link all of them to this job
                using (var cmd = con.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 120;   // up to 20,000 stickers in one job
                    cmd.CommandText =
                        "UPDATE s SET print_count = s.print_count + 1, last_printed_at = SYSDATETIME(), last_job_id = @job " +
                        "FROM print_sticker s JOIN #pn n ON n.no = s.sticker_no " +
                        "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode; " +

                        "INSERT INTO print_sticker (tanggal, line_production_id, barcode, sticker_no, serial, print_count, " +
                        "                           first_printed_at, last_printed_at, first_job_id, last_job_id) " +
                        "SELECT @tanggal, @lp, @barcode, n.no, n.serial, 1, SYSDATETIME(), SYSDATETIME(), @job, @job " +
                        "FROM #pn n " +
                        "WHERE NOT EXISTS (SELECT 1 FROM print_sticker s WHERE s.tanggal = @tanggal AND s.line_production_id = @lp " +
                        "                  AND s.barcode = @barcode AND s.sticker_no = n.no); " +

                        "INSERT INTO print_job_sticker (print_job_id, print_sticker_id) " +
                        "SELECT @job, s.id FROM print_sticker s JOIN #pn n ON n.no = s.sticker_no " +
                        "WHERE s.tanggal = @tanggal AND s.line_production_id = @lp AND s.barcode = @barcode;";
                    cmd.Parameters.AddWithValue("@job", jobId);
                    AddDate(cmd, "@tanggal", e.Tanggal);
                    cmd.Parameters.AddWithValue("@lp", e.LineProductionId);
                    cmd.Parameters.AddWithValue("@barcode", e.Barcode);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return jobId;
            }
        }
    }

    /// <summary>A sticker number that was printed before (answer of Database.FindPrinted).</summary>
    public class PrintedSticker
    {
        public int Number { get; set; }
        public int PrintCount { get; set; }
        public DateTime LastPrintedAt { get; set; }
        public string LastUser { get; set; }
    }

    /// <summary>Everything Database.LogPrint needs to record one print.</summary>
    public class PrintLogEntry
    {
        public DateTime Tanggal { get; set; }
        public int LineProductionId { get; set; }
        public string LineName { get; set; }
        public string MapCode { get; set; }
        public string Barcode { get; set; }
        public string PartNumber { get; set; }
        public string Model { get; set; }
        public string Printer { get; set; }
        public string StickerSize { get; set; }
        public IList<int> Numbers { get; set; }
        public Func<int, string> SerialOf { get; set; }   // the exact text of the QR code for a sticker number
        public string Reason { get; set; }                // only for a reprint
        public string Note { get; set; }
    }
}