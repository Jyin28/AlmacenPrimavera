using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AlmacenPrimavera.Data;

// Call within a write transaction: allocation and the corresponding save are atomic.
public static class CatalogNumbering
{
    static long L(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
    public static long Next(SqliteConnection db, string kind, long owner)
    {
        return L(Db.Scalar(db, """
INSERT INTO catalog_number_counters(kind,owner_id,last_number) VALUES(@kind,@owner,1)
ON CONFLICT(kind,owner_id) DO UPDATE SET last_number=last_number+1 RETURNING last_number
""", ("@kind",kind), ("@owner",owner)));
    }

    public static void RequireSubcategory(SqliteConnection db, long? id)
    {
        if (id is not null && Db.Scalar(db, "SELECT 1 FROM catalog_categories WHERE id=@id AND parent_id IS NOT NULL AND sequence_number IS NOT NULL", ("@id",id)) is null)
            throw new DomainException("Selecciona una subcategoría, no solo la categoría principal.");
    }

    public static void Apply(SqliteConnection db, long productId, long? subcategoryId)
    {
        RequireSubcategory(db,subcategoryId);
        var p=Db.One(db,"SELECT category_id,product_number,internal_code FROM catalog_products WHERE id=@id",("@id",productId));
        long? number=null; string? code=null;
        if (subcategoryId.HasValue)
        {
            number=Equals(p["category_id"],subcategoryId.Value) && p["product_number"] is not null
                ? L(p["product_number"]) : Next(db,"product",subcategoryId.Value);
            var c=Db.One(db,"SELECT c.sequence_number,r.abbreviation FROM catalog_categories c JOIN catalog_categories r ON r.id=c.parent_id WHERE c.id=@id",("@id",subcategoryId));
            code=$"{c["abbreviation"]}-{c["sequence_number"]}-{number}";
        }
        if (Equals(p["category_id"],subcategoryId) && Equals(p["product_number"],number) && Equals(p["internal_code"],code)) return;
        if (p["internal_code"] is string previous && previous!=code)
            Db.Exec(db,"INSERT INTO catalog_code_history(product_id,old_code,new_code,changed_at) VALUES(@id,@old,@new,@now)",
                ("@id",productId),("@old",previous),("@new",code),("@now",StoreDb.Now()));
        Db.Exec(db,"UPDATE catalog_products SET category_id=@cat,product_number=@num,internal_code=@code,version=version+1 WHERE id=@id",
            ("@id",productId),("@cat",subcategoryId),("@num",number),("@code",code));
    }

    public static void Initialize(SqliteConnection db)
    {
        if (Db.Scalar(db,"SELECT 1 FROM catalog_migrations WHERE version=2") is not null) return;
        Db.Exec(db,"""
CREATE TABLE catalog_taxonomy_before_numbering AS SELECT * FROM catalog_categories;
CREATE TABLE catalog_product_codes_before_numbering AS SELECT id,category_id,internal_code FROM catalog_products;
ALTER TABLE catalog_categories ADD COLUMN sequence_number INTEGER;
ALTER TABLE catalog_products ADD COLUMN product_number INTEGER;
CREATE TABLE catalog_number_counters(kind TEXT NOT NULL,owner_id INTEGER NOT NULL,last_number INTEGER NOT NULL CHECK(last_number>0),PRIMARY KEY(kind,owner_id));
CREATE TABLE catalog_code_history(id INTEGER PRIMARY KEY,product_id INTEGER NOT NULL REFERENCES catalog_products(id),old_code TEXT NOT NULL,new_code TEXT,changed_at TEXT NOT NULL);
""");
        var all=Db.Query(db,"SELECT * FROM catalog_categories ORDER BY id");
        var byId=all.ToDictionary(c=>L(c["id"]));
        // Existing nested paths become named subcategories of their original root.
        // Original taxonomy/code mappings remain available in the snapshot tables.
        Db.Exec(db,"UPDATE catalog_categories SET abbreviation='' WHERE parent_id IS NOT NULL");
        foreach (var root in all.Where(c=>c["parent_id"] is null))
        {
            var code=((string)root["abbreviation"]!).ToUpperInvariant();
            if (!Regex.IsMatch(code,"^[A-Z0-9]{1,12}$"))
            {
                var suffix=1;
                do { code="CAT"+suffix++; }
                while(Db.Scalar(db,"SELECT 1 FROM catalog_categories WHERE abbreviation=@c COLLATE NOCASE",("@c",code)) is not null);
            }
            Db.Exec(db,"UPDATE catalog_categories SET abbreviation=@code WHERE id=@id",("@code",code),("@id",root["id"]));
        }
        foreach (var child in all.Where(c=>c["parent_id"] is not null))
        {
            var path=new List<string>{(string)child["name"]!}; var current=child;
            var seen=new HashSet<long>{L(child["id"])};
            while (current["parent_id"] is not null)
            {
                var parentId=L(current["parent_id"]);
                if (!seen.Add(parentId) || !byId.TryGetValue(parentId,out current!)) throw new DomainException("Revisa la jerarquía de categorías antes de migrar la numeración.");
                if (current["parent_id"] is not null) path.Insert(0,(string)current["name"]!);
            }
            var rootId=L(current["id"]);
            var name=string.Join(" / ",path);
            if (Db.Scalar(db,"SELECT 1 FROM catalog_categories WHERE parent_id=@root AND name=@name COLLATE NOCASE AND id<>@id",("@root",rootId),("@name",name),("@id",child["id"])) is not null)
                name+=$" ({child["id"]})";
            Db.Exec(db,"UPDATE catalog_categories SET parent_id=@root,name=@name,sequence_number=@number WHERE id=@id",
                ("@root",rootId),("@name",name),("@number",Next(db,"subcategory",rootId)),("@id",child["id"]));
        }
        // Products directly on a root retain their category through a General subcategory.
        foreach (var root in all.Where(c=>c["parent_id"] is null))
        {
            if (Db.Scalar(db,"SELECT 1 FROM catalog_products WHERE category_id=@id LIMIT 1",("@id",root["id"])) is null) continue;
            var name="General"; var suffix=1;
            while (Db.Scalar(db,"SELECT 1 FROM catalog_categories WHERE parent_id=@id AND name=@name COLLATE NOCASE",("@id",root["id"]),("@name",name)) is not null)
                name="General (migrado "+suffix+++")";
            Db.Exec(db,"INSERT INTO catalog_categories(parent_id,name,abbreviation,sequence_number,target_markup,minimum_margin) VALUES(@root,@name,'',@num,@t,@m)",
                ("@root",root["id"]),("@name",name),("@num",Next(db,"subcategory",L(root["id"]))),("@t",root["target_markup"]),("@m",root["minimum_margin"]));
            var subcategoryId=L(Db.Scalar(db,"SELECT last_insert_rowid()"));
            Db.Exec(db,"UPDATE catalog_products SET category_id=@sub WHERE category_id=@root",("@sub",subcategoryId),("@root",root["id"]));
        }
        // Clear the old unique values together to avoid collisions with new automatic codes.
        Db.Exec(db,"UPDATE catalog_products SET internal_code=NULL,version=version+1");
        foreach (var p in Db.Query(db,"SELECT id,category_id FROM catalog_products ORDER BY id"))
            Apply(db,L(p["id"]),p["category_id"] is null?null:L(p["category_id"]));
        Db.Exec(db,"""
INSERT INTO catalog_code_history(product_id,old_code,new_code,changed_at)
 SELECT p.id,b.internal_code,p.internal_code,@now FROM catalog_products p JOIN catalog_product_codes_before_numbering b ON b.id=p.id
 WHERE b.internal_code IS NOT NULL AND b.internal_code IS NOT p.internal_code;
CREATE UNIQUE INDEX ux_catalog_subcategory_number ON catalog_categories(parent_id,sequence_number) WHERE parent_id IS NOT NULL;
CREATE UNIQUE INDEX ux_catalog_product_number ON catalog_products(category_id,product_number) WHERE category_id IS NOT NULL;
INSERT INTO catalog_migrations(version) VALUES(2);
""",("@now",StoreDb.Now()));
    }
}
