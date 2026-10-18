using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AlmacenPrimavera.Data;

public sealed class StoreDb
{
    private readonly string _connectionString;
    public StoreDb(IWebHostEnvironment env)
    {
        var configured = Environment.GetEnvironmentVariable("STORE_DB_PATH");
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(env.ContentRootPath, "data", "store.db") : Path.GetFullPath(configured, env.ContentRootPath);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
        Seed(db);
        CatalogSchema.Initialize(db);
    }

    private static void Seed(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        long Count(string table)
        {
            using var c = db.CreateCommand(); c.Transaction = tx;
            c.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(c.ExecuteScalar() ?? 0L);
        }
        void Exec(string sql, params (string, object?)[] args)
        {
            using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = sql;
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
            c.ExecuteNonQuery();
        }

        if (Count("users") == 0)
        {
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var password = Environment.GetEnvironmentVariable("DEFAULT_ADMIN_PASSWORD") ?? "12345678";
            Exec("INSERT INTO users(id,username,display_name,role,password_hash,password_salt,active,created_at) VALUES(1,'admin','Administrador','administrator',@hash,@salt,1,@now)",
                ("@hash", HashPassword(password, salt)), ("@salt", salt), ("@now", Now()));
        }
        tx.Commit();
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public static string HashPassword(string password, string salt) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), 120_000, HashAlgorithmName.SHA256, 32));

    private const string Schema = """
CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE, display_name TEXT NOT NULL, role TEXT NOT NULL CHECK(role IN ('administrator','manager','inventory','purchasing','cashier')), password_hash TEXT NOT NULL, password_salt TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY, token_hash TEXT NOT NULL UNIQUE, user_id INTEGER NOT NULL REFERENCES users(id), expires_at TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS departments(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, code TEXT NOT NULL UNIQUE, active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS categories(id INTEGER PRIMARY KEY, department_id INTEGER NOT NULL REFERENCES departments(id), parent_id INTEGER REFERENCES categories(id), name TEXT NOT NULL, code TEXT NOT NULL UNIQUE, minimum_margin NUMERIC NOT NULL DEFAULT 20, target_markup NUMERIC NOT NULL DEFAULT 35, required_attributes TEXT NOT NULL DEFAULT '[]', active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS suppliers(id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT, phone TEXT, active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS locations(id INTEGER PRIMARY KEY, name TEXT NOT NULL, code TEXT NOT NULL UNIQUE, active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS product_families(id INTEGER PRIMARY KEY, name TEXT NOT NULL, description TEXT, created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS products(id INTEGER PRIMARY KEY, internal_id TEXT NOT NULL UNIQUE COLLATE NOCASE, family_id INTEGER REFERENCES product_families(id), name TEXT NOT NULL, short_description TEXT, category_id INTEGER NOT NULL REFERENCES categories(id), brand TEXT, product_type TEXT, unit_of_measure TEXT NOT NULL, package_size TEXT, attributes TEXT NOT NULL DEFAULT '{}', currency TEXT NOT NULL, cost NUMERIC NOT NULL CHECK(cost >= 0), selling_price NUMERIC NOT NULL CHECK(selling_price >= 0), tax_rate NUMERIC NOT NULL DEFAULT 0 CHECK(tax_rate >= 0), reorder_level NUMERIC NOT NULL DEFAULT 0, desired_stock NUMERIC NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1, discontinued INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE INDEX IF NOT EXISTS ix_products_name ON products(name COLLATE NOCASE); CREATE INDEX IF NOT EXISTS ix_products_category ON products(category_id,active); CREATE INDEX IF NOT EXISTS ix_products_brand ON products(brand COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS id_sequences(category_id INTEGER PRIMARY KEY REFERENCES categories(id), next_value INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS barcodes(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), code TEXT NOT NULL COLLATE NOCASE, type TEXT NOT NULL CHECK(type IN ('UPC','EAN','ISBN','GTIN','SUPPLIER','OTHER')), is_primary INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ux_active_barcode ON barcodes(code COLLATE NOCASE) WHERE active=1;
CREATE TABLE IF NOT EXISTS tags(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE); CREATE TABLE IF NOT EXISTS product_tags(product_id INTEGER NOT NULL REFERENCES products(id), tag_id INTEGER NOT NULL REFERENCES tags(id), PRIMARY KEY(product_id,tag_id));
CREATE TABLE IF NOT EXISTS supplier_products(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), supplier_id INTEGER NOT NULL REFERENCES suppliers(id), supplier_sku TEXT, barcode TEXT, cost NUMERIC NOT NULL, package_quantity NUMERIC NOT NULL DEFAULT 1, lead_time_days INTEGER NOT NULL DEFAULT 0, minimum_order_quantity NUMERIC NOT NULL DEFAULT 1, preferred INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, UNIQUE(product_id,supplier_id,supplier_sku));
CREATE TABLE IF NOT EXISTS supplier_cost_history(id INTEGER PRIMARY KEY, supplier_product_id INTEGER NOT NULL REFERENCES supplier_products(id), old_cost NUMERIC, new_cost NUMERIC NOT NULL, changed_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE TABLE IF NOT EXISTS inventory_balances(product_id INTEGER NOT NULL REFERENCES products(id), location_id INTEGER NOT NULL REFERENCES locations(id), on_hand NUMERIC NOT NULL DEFAULT 0, average_cost NUMERIC NOT NULL DEFAULT 0, updated_at TEXT NOT NULL, version INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(product_id,location_id));
CREATE TABLE IF NOT EXISTS stock_movements(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), location_id INTEGER NOT NULL REFERENCES locations(id), destination_location_id INTEGER REFERENCES locations(id), type TEXT NOT NULL CHECK(type IN ('opening','receipt','sale','return','damage','loss','transfer_out','transfer_in','correction','count')), quantity NUMERIC NOT NULL, unit_cost NUMERIC, reason TEXT NOT NULL, reference TEXT, occurred_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE INDEX IF NOT EXISTS ix_movements_product_date ON stock_movements(product_id,occurred_at DESC);
CREATE TABLE IF NOT EXISTS price_history(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), cost NUMERIC NOT NULL, selling_price NUMERIC NOT NULL, reason TEXT NOT NULL, effective_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE TABLE IF NOT EXISTS pricing_rules(id INTEGER PRIMARY KEY, category_id INTEGER REFERENCES categories(id), supplier_id INTEGER REFERENCES suppliers(id), min_margin NUMERIC, target_markup NUMERIC, rounding_ending INTEGER NOT NULL DEFAULT 95, approval_deviation NUMERIC NOT NULL DEFAULT 15, active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE IF NOT EXISTS price_recommendations(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES products(id), landed_cost NUMERIC NOT NULL, suggested_price NUMERIC NOT NULL, median_markup NUMERIC NOT NULL, weighted_markup NUMERIC NOT NULL, gross_margin NUMERIC NOT NULL, expected_profit NUMERIC NOT NULL, range_low NUMERIC NOT NULL, range_high NUMERIC NOT NULL, confidence TEXT NOT NULL, explanation TEXT NOT NULL, comparables TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','approved','rejected')), previous_price NUMERIC NOT NULL, decided_by INTEGER REFERENCES users(id), decided_at TEXT, decision_reason TEXT, created_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE TABLE IF NOT EXISTS purchase_orders(id INTEGER PRIMARY KEY, po_number TEXT NOT NULL UNIQUE, supplier_id INTEGER NOT NULL REFERENCES suppliers(id), location_id INTEGER NOT NULL REFERENCES locations(id), status TEXT NOT NULL CHECK(status IN ('draft','approved','ordered','partially_received','received','cancelled','closed')), currency TEXT NOT NULL, notes TEXT, created_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id), approved_by INTEGER REFERENCES users(id), approved_at TEXT);
CREATE TABLE IF NOT EXISTS purchase_order_lines(id INTEGER PRIMARY KEY, purchase_order_id INTEGER NOT NULL REFERENCES purchase_orders(id), product_id INTEGER NOT NULL REFERENCES products(id), ordered_quantity NUMERIC NOT NULL CHECK(ordered_quantity>0), received_quantity NUMERIC NOT NULL DEFAULT 0, unit_cost NUMERIC NOT NULL CHECK(unit_cost>=0));
CREATE TABLE IF NOT EXISTS goods_receipts(id INTEGER PRIMARY KEY, purchase_order_id INTEGER NOT NULL REFERENCES purchase_orders(id), reference TEXT, received_at TEXT NOT NULL, received_by INTEGER NOT NULL REFERENCES users(id));
CREATE TABLE IF NOT EXISTS inventory_counts(id INTEGER PRIMARY KEY, location_id INTEGER NOT NULL REFERENCES locations(id), status TEXT NOT NULL CHECK(status IN ('draft','counting','posted','cancelled')), notes TEXT, created_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id), posted_at TEXT);
CREATE TABLE IF NOT EXISTS inventory_count_lines(id INTEGER PRIMARY KEY, inventory_count_id INTEGER NOT NULL REFERENCES inventory_counts(id), product_id INTEGER NOT NULL REFERENCES products(id), expected_quantity NUMERIC NOT NULL, counted_quantity NUMERIC NOT NULL);
CREATE TABLE IF NOT EXISTS sales(id INTEGER PRIMARY KEY, sale_number TEXT NOT NULL UNIQUE, location_id INTEGER NOT NULL REFERENCES locations(id), status TEXT NOT NULL CHECK(status IN ('completed','voided')), currency TEXT NOT NULL, occurred_at TEXT NOT NULL, created_by INTEGER NOT NULL REFERENCES users(id));
CREATE TABLE IF NOT EXISTS sale_lines(id INTEGER PRIMARY KEY, sale_id INTEGER NOT NULL REFERENCES sales(id), product_id INTEGER NOT NULL REFERENCES products(id), quantity NUMERIC NOT NULL CHECK(quantity>0), unit_price NUMERIC NOT NULL CHECK(unit_price>=0), unit_cost NUMERIC NOT NULL CHECK(unit_cost>=0), tax_rate NUMERIC NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS audit_events(id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), action TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, old_values TEXT, new_values TEXT, reason TEXT, occurred_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_events(entity_type,entity_id,occurred_at DESC);
CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT,'audit events are append-only'); END;
CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT,'audit events are append-only'); END;
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by INTEGER REFERENCES users(id));
INSERT OR IGNORE INTO settings(key,value,updated_at) VALUES ('id_format','{DEPARTMENT}-{CATEGORY}-{SEQUENCE:6}',datetime('now')),('currency','CRC',datetime('now')),('cost_change_threshold','10',datetime('now')),('allow_negative_stock','false',datetime('now'));
""";
}
