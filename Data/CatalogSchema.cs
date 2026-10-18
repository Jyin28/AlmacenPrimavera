using Microsoft.Data.Sqlite;

namespace AlmacenPrimavera.Data;

// One-time, non-destructive migration. Legacy tables remain intact as historical data.
public static class CatalogSchema
{
    public static void Initialize(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        Db.Exec(db, """
CREATE TABLE IF NOT EXISTS catalog_migrations(version INTEGER PRIMARY KEY);
CREATE TABLE IF NOT EXISTS catalog_categories(
 id INTEGER PRIMARY KEY AUTOINCREMENT, parent_id INTEGER REFERENCES catalog_categories(id),
 name TEXT NOT NULL, abbreviation TEXT NOT NULL DEFAULT '', target_markup TEXT NOT NULL DEFAULT '35', minimum_margin TEXT NOT NULL DEFAULT '20');
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_abbreviation ON catalog_categories(abbreviation COLLATE NOCASE) WHERE abbreviation<>'';
CREATE TABLE IF NOT EXISTS importers(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE, legacy_supplier_id INTEGER);
CREATE TABLE IF NOT EXISTS catalog_products(
 id INTEGER PRIMARY KEY AUTOINCREMENT, barcode TEXT UNIQUE COLLATE NOCASE, internal_code TEXT UNIQUE COLLATE NOCASE,
 name TEXT NOT NULL, category_id INTEGER REFERENCES catalog_categories(id), importer_id INTEGER REFERENCES importers(id),
 cost TEXT NOT NULL DEFAULT '0', selling_price TEXT NOT NULL DEFAULT '0', tax_rate TEXT NOT NULL DEFAULT '0', quantity TEXT NOT NULL DEFAULT '0',
 review_pending INTEGER NOT NULL DEFAULT 1, version INTEGER NOT NULL DEFAULT 1, active INTEGER NOT NULL DEFAULT 1);
CREATE INDEX IF NOT EXISTS ix_catalog_category ON catalog_products(category_id);
CREATE TABLE IF NOT EXISTS catalog_price_history(id INTEGER PRIMARY KEY, product_id INTEGER NOT NULL REFERENCES catalog_products(id), old_price TEXT NOT NULL, new_price TEXT NOT NULL, changed_at TEXT NOT NULL);
""");
        if (Db.Scalar(db, "SELECT 1 FROM catalog_migrations WHERE version=1") is null)
        {
            Db.Exec(db, """
INSERT INTO catalog_categories(id,parent_id,name,abbreviation,target_markup,minimum_margin)
 SELECT id,parent_id,name,code,CAST(target_markup AS TEXT),CAST(minimum_margin AS TEXT) FROM categories;
INSERT INTO importers(name,legacy_supplier_id)
 SELECT s.name || CASE WHEN (SELECT COUNT(*) FROM suppliers t WHERE t.name=s.name COLLATE NOCASE)>1 THEN ' (' || s.id || ')' ELSE '' END,s.id
 FROM suppliers s WHERE EXISTS(SELECT 1 FROM supplier_products sp WHERE sp.supplier_id=s.id) ORDER BY s.id;
INSERT INTO catalog_products(id,barcode,internal_code,name,category_id,importer_id,cost,selling_price,tax_rate,quantity,review_pending,active)
 SELECT p.id,(SELECT code FROM barcodes b WHERE b.product_id=p.id AND b.active=1 ORDER BY is_primary DESC,id LIMIT 1),
 p.internal_id,p.name,p.category_id,
 (SELECT i.id FROM supplier_products sp JOIN importers i ON i.legacy_supplier_id=sp.supplier_id WHERE sp.product_id=p.id ORDER BY sp.preferred DESC,sp.id LIMIT 1),
 CAST(p.cost AS TEXT),CAST(p.selling_price AS TEXT),CAST(p.tax_rate AS TEXT),
 CAST(COALESCE((SELECT SUM(on_hand) FROM inventory_balances b WHERE b.product_id=p.id),0) AS TEXT),1,p.active FROM products p;
INSERT INTO catalog_migrations(version) VALUES(1);
""");
        }
        CatalogNumbering.Initialize(db);
        tx.Commit();
    }
}
