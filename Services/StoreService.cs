using System.Globalization;
using System.Text.RegularExpressions;
using AlmacenPrimavera.Data;
using Microsoft.Data.Sqlite;

namespace AlmacenPrimavera;

public sealed class StoreService(StoreDb store)
{
    static decimal D(object? v) => Convert.ToDecimal(v, CultureInfo.InvariantCulture);
    static long L(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);
    static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    const string ProductSql = """
SELECT p.*,r.id root_category_id,r.name category,r.abbreviation category_code,c.name subcategory,c.sequence_number subcategory_number,i.name importer
FROM catalog_products p LEFT JOIN catalog_categories c ON c.id=p.category_id
LEFT JOIN catalog_categories r ON r.id=c.parent_id LEFT JOIN importers i ON i.id=p.importer_id
""";
    static Dictionary<string,object?> Numeric(Dictionary<string,object?> p)
    {
        foreach (var key in new[] { "cost", "selling_price", "quantity", "tax_rate", "target_markup", "minimum_margin" })
            if (p.ContainsKey(key)) p[key] = D(p[key]);
        return p;
    }
    static Dictionary<string,object?> GetProduct(SqliteConnection db, long id)
    {
        var p = Db.One(db, ProductSql + " WHERE p.id=@id AND p.active=1", ("@id", id));
        if (p.Count == 0) throw new DomainException("Producto no encontrado.", 404);
        return Numeric(p);
    }
    public object Lookups()
    {
        using var db = store.Open();
        return new {
            categories = Db.Query(db, "SELECT * FROM catalog_categories WHERE parent_id IS NULL ORDER BY name").Select(Numeric),
            subcategories = Db.Query(db, "SELECT * FROM catalog_categories WHERE parent_id IS NOT NULL ORDER BY parent_id,sequence_number").Select(Numeric),
            importers = Db.Query(db, "SELECT id,name FROM importers ORDER BY id"),
            pending = L(Db.Scalar(db, "SELECT COUNT(*) FROM catalog_products WHERE active=1 AND review_pending=1"))
        };
    }
    public object Products(string? q, long? categoryId, bool pending, int page)
    {
        using var db = store.Open();
        page = Math.Max(1, page);
        const string where = """
 WHERE p.active=1 AND (@pending=0 OR p.review_pending=1)
 AND (@cat IS NULL OR p.category_id IN (WITH RECURSIVE descendants(id) AS (
 SELECT id FROM catalog_categories WHERE id=@cat UNION SELECT c.id FROM catalog_categories c JOIN descendants d ON c.parent_id=d.id) SELECT id FROM descendants))
 AND (@q='' OR instr(lower(p.name),lower(@q))>0 OR instr(lower(COALESCE(p.barcode,'')),lower(@q))>0
 OR instr(lower(COALESCE(p.internal_code,'')),lower(@q))>0 OR instr(lower(COALESCE(i.name,'')),lower(@q))>0)
""";
        (string,object?)[] args = [("@pending",pending?1:0),("@cat",categoryId),("@q",q?.Trim()??""),("@skip",(long)(page-1)*50)];
        var total = L(Db.Scalar(db, "SELECT COUNT(*) FROM catalog_products p LEFT JOIN importers i ON i.id=p.importer_id" + where, args));
        return new { items=Db.Query(db, ProductSql+where+" ORDER BY p.id DESC LIMIT 50 OFFSET @skip",args).Select(Numeric),total,page,pages=Math.Max(1,(int)Math.Ceiling(total/50m)) };
    }
    public object Product(long id) { using var db = store.Open(); return GetProduct(db,id); }
    static string Text(string? value, string label, int max = 160)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > max || text.Any(char.IsControl)) throw new DomainException($"{label}: ingresa entre 1 y {max} caracteres, sin saltos de línea.");
        return text;
    }
    static void Exists(SqliteConnection db, string table, long? id, string label)
    {
        if (id is not null && Db.Scalar(db,$"SELECT 1 FROM {table} WHERE id=@id",("@id",id)) is null)
            throw new DomainException($"{label} no existe.");
    }
    public object SaveSubcategory(long? id, SubcategoryEdit r) => SaveCategory(id,new(r.Name,null,r.CategoryId,r.TargetMarkup,r.MinimumMargin),true);
    public object SaveCategory(long? id, CategoryEdit r, bool subcategory=false)
    {
        var name = Text(r.Name,"Nombre");
        var code = subcategory ? "" : (r.Abbreviation??"").Trim().ToUpperInvariant();
        if (!subcategory && !Regex.IsMatch(code,@"^[A-Z0-9]{1,12}$")) throw new DomainException("La categoría requiere una abreviación de 1 a 12 letras o números, sin espacios.");
        if (!subcategory && r.ParentId is not null) throw new DomainException("Usa la sección Subcategorías para crear o editar una subcategoría.");
        if (r.TargetMarkup < 0 || r.TargetMarkup > 1000 || r.MinimumMargin < 0 || r.MinimumMargin >= 100) throw new DomainException("Recargo: entre 0 y 1000 %. Margen mínimo: entre 0 y menos de 100 %.");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        Exists(db,"catalog_categories",id,"La categoría");
        var old=id.HasValue?Db.One(db,"SELECT * FROM catalog_categories WHERE id=@id",("@id",id)):null;
        if (old is not null && subcategory != (old["parent_id"] is not null)) throw new DomainException("Categorías y subcategorías se editan en sus secciones respectivas.");
        if (subcategory && (r.ParentId is null || Db.Scalar(db,"SELECT 1 FROM catalog_categories WHERE id=@id AND parent_id IS NULL",("@id",r.ParentId)) is null))
            throw new DomainException("La subcategoría debe pertenecer a una categoría principal. Solo se admiten estos dos niveles.");
        if (Db.Scalar(db,"SELECT 1 FROM catalog_categories WHERE name=@n COLLATE NOCASE AND parent_id IS @p AND id<>@id",("@n",name),("@p",r.ParentId),("@id",id??0)) is not null)
            throw new DomainException("Ya existe una categoría con ese nombre en este nivel.");
        long? sequence=subcategory ? old is not null && Equals(old["parent_id"],r.ParentId) ? L(old["sequence_number"]) : CatalogNumbering.Next(db,"subcategory",r.ParentId!.Value) : null;
        (string,object?)[] args=[("@id",id),("@n",name),("@a",code),("@p",r.ParentId),("@num",sequence),("@t",S(r.TargetMarkup)),("@m",S(r.MinimumMargin))];
        if(id is null) { Db.Exec(db,"INSERT INTO catalog_categories(name,abbreviation,parent_id,sequence_number,target_markup,minimum_margin) VALUES(@n,@a,@p,@num,@t,@m)",args); id=L(Db.Scalar(db,"SELECT last_insert_rowid()")); }
        else {
            Db.Exec(db,"UPDATE catalog_categories SET name=@n,abbreviation=@a,parent_id=@p,sequence_number=@num,target_markup=@t,minimum_margin=@m WHERE id=@id",args);
            foreach(var p in Db.Query(db,"SELECT id,category_id FROM catalog_products WHERE category_id=@id OR category_id IN (SELECT id FROM catalog_categories WHERE parent_id=@id)",("@id",id)))
                CatalogNumbering.Apply(db,L(p["id"]),L(p["category_id"]));
            if (subcategory && (D(old!["target_markup"])!=r.TargetMarkup || D(old["minimum_margin"])!=r.MinimumMargin))
                Db.Exec(db,"UPDATE catalog_products SET version=version+1 WHERE category_id=@id",("@id",id));
        }
        var result=Numeric(Db.One(db,"SELECT * FROM catalog_categories WHERE id=@id",("@id",id)));
        tx.Commit(); return result;
    }
    public object SaveImporter(long? id, ImporterEdit r)
    {
        var name=Text(r.Name,"Importador");
        using var db=store.Open();
        Exists(db,"importers",id,"El importador");
        if(id is null) { Db.Exec(db,"INSERT INTO importers(name) VALUES(@n)",("@n",name)); id=L(Db.Scalar(db,"SELECT last_insert_rowid()")); }
        else Db.Exec(db,"UPDATE importers SET name=@n WHERE id=@id",("@n",name),("@id",id));
        return new {id,name};
    }
    public object SaveProduct(long? id,ProductEdit r)
    {
        var name=Text(r.Name,"Nombre");
        var barcode=Text(r.Barcode,"Código de barras",100);
        ExcelFiles.ValidateNumbers(r.Cost,r.SellingPrice,r.Quantity,r.TaxRate);
        using var db=store.Open(); using var tx=db.BeginTransaction();
        CatalogNumbering.RequireSubcategory(db,r.CategoryId);
        Exists(db,"importers",r.ImporterId,"El importador");
        Dictionary<string,object?>? old=null;
        if(id.HasValue) {
            old=GetProduct(db,id.Value);
            if(L(old["version"])!=r.Version) throw new DomainException("Este producto cambió. Ábrelo de nuevo antes de guardar.",409);
        }
        else {
            Db.Exec(db,"INSERT INTO catalog_products(name) VALUES(@n)",("@n",name));
            id=L(Db.Scalar(db,"SELECT last_insert_rowid()"));
        }
        CatalogNumbering.Apply(db,id.Value,r.CategoryId);
        var needsReview=old is null || L(old["review_pending"])==1 || D(old["cost"])!=Money(r.Cost) || old["category_id"] as long?!=r.CategoryId || old["importer_id"] as long?!=r.ImporterId || r.CategoryId is null || r.ImporterId is null;
        Db.Exec(db,"""
UPDATE catalog_products SET name=@n,barcode=@b,importer_id=@imp,cost=@c,selling_price=@s,tax_rate=@t,quantity=@q,review_pending=@pending,version=version+1 WHERE id=@id
""",("@id",id),("@n",name),("@b",barcode),("@imp",r.ImporterId),
            ("@c",S(Money(r.Cost))),("@s",S(Money(r.SellingPrice))),("@t",S(r.TaxRate)),("@q",S(r.Quantity)),("@pending",needsReview?1:0));
        if(old is not null && D(old["selling_price"])!=Money(r.SellingPrice)) History(db,id.Value,D(old["selling_price"]),Money(r.SellingPrice));
        var result=GetProduct(db,id.Value); tx.Commit(); return result;
    }
    public object Classify(Classification r)
    {
        if(r.Ids is null || r.Ids.Length==0 || r.Ids.Length>1000) throw new DomainException("Selecciona entre 1 y 1000 productos.");
        if(r.CategoryId is null && r.ImporterId is null) throw new DomainException("Selecciona una subcategoría o un importador.");
        using var db=store.Open(); using var tx=db.BeginTransaction();
        CatalogNumbering.RequireSubcategory(db,r.CategoryId); Exists(db,"importers",r.ImporterId,"El importador");
        foreach(var id in r.Ids.Distinct().Order()) {
            var p=GetProduct(db,id);
            var category=r.CategoryId??(p["category_id"] is null?null:L(p["category_id"]));
            var importer=r.ImporterId??(p["importer_id"] is null?null:L(p["importer_id"]));
            CatalogNumbering.Apply(db,id,category);
            Db.Exec(db,"UPDATE catalog_products SET importer_id=@i,review_pending=1,version=version+1 WHERE id=@id",
                ("@i",importer),("@id",id));
        }
        tx.Commit(); return new {updated=r.Ids.Distinct().Count()};
    }
    public object Import(ImportRequest r)
    {
        ExcelFiles.ValidateRows(r.Rows);
        using var db=store.Open(); using var tx=db.BeginTransaction();
        var created=0; var updated=0;
        foreach(var row in r.Rows) {
            var p=Db.One(db,"SELECT * FROM catalog_products WHERE barcode=@b",("@b",row.Barcode.Trim()));
            if(p.Count==0) {
                Db.Exec(db,"INSERT INTO catalog_products(barcode,name,cost,selling_price,tax_rate,quantity) VALUES(@b,@n,@c,@s,@t,@q)",
                    ("@b",row.Barcode.Trim()),("@n",row.Name.Trim()),("@c",S(Money(row.Cost))),("@s",S(Money(row.SellingPrice))),("@t",S(row.TaxRate)),("@q",S(row.Quantity)));
                created++;
            } else {
                var costChanged=D(p["cost"])!=Money(row.Cost);
                Db.Exec(db,"""
UPDATE catalog_products SET name=@n,cost=@c,selling_price=@s,tax_rate=@t,quantity=@q,
 review_pending=CASE WHEN @changed=1 OR active=0 THEN 1 ELSE review_pending END,active=1,version=version+1 WHERE id=@id
""",("@id",p["id"]),("@n",row.Name.Trim()),("@c",S(Money(row.Cost))),("@s",S(Money(row.SellingPrice))),("@t",S(row.TaxRate)),("@q",S(row.Quantity)),("@changed",costChanged?1:0));
                if(D(p["selling_price"])!=Money(row.SellingPrice)) History(db,L(p["id"]),D(p["selling_price"]),Money(row.SellingPrice));
                updated++;
            }
        }
        tx.Commit(); return new {created,updated,message="Cantidades actualizadas al total fusionado. Los productos nuevos quedan por categorizar y revisar."};
    }
    public List<MergedProduct> ExportRows()
    {
        using var db=store.Open();
        return Db.Query(db,ProductSql+" WHERE p.active=1 ORDER BY p.id").Select(p=>new MergedProduct((string?)p["barcode"]??"", (string)p["name"]!,D(p["quantity"]),D(p["cost"]),D(p["selling_price"]),D(p["tax_rate"]),
            (string?)p["internal_code"],(string?)p["category_code"],(string?)p["importer"],p["importer_id"] is null?null:L(p["importer_id"]))).ToList();
    }
    public object Recommend(PriceRequest r)
    {
        if(r.ExtraCost<0 || r.ExtraCost>1_000_000_000 || r.Markup is <0 or >1000 || r.RoundTo is not (1 or 5 or 10 or 50 or 100)) throw new DomainException("Revisa el costo adicional, el recargo y el redondeo.");
        using var db=store.Open();
        var p=GetProduct(db,r.ProductId);
        if(p["category_id"] is null) throw new DomainException("Asigna una categoría y subcategoría para recibir una recomendación.");
        var cat=Db.One(db,"SELECT * FROM catalog_categories WHERE id=@id",("@id",p["category_id"]));
        var landed=Money(D(p["cost"])+r.ExtraCost);
        if(landed<=0) throw new DomainException("El precio de compra debe ser mayor que cero.");
        var peers=Db.Query(db,ProductSql+"""
 WHERE p.active=1 AND p.review_pending=0 AND p.id<>@id AND p.category_id=@cat
 ORDER BY CASE WHEN p.importer_id=@importer THEN 0 ELSE 1 END,p.id DESC LIMIT 100
""",("@id",r.ProductId),("@cat",p["category_id"]),("@importer",p["importer_id"]))
            .Where(x=>D(x["cost"])>0 && D(x["selling_price"])>D(x["cost"]))
            .Select(x=>new {id=L(x["id"]),name=(string)x["name"]!,cost=D(x["cost"]),price=D(x["selling_price"]),
                markup=(D(x["selling_price"])/D(x["cost"])-1)*100, sameImporter=p["importer_id"] is not null && Equals(x["importer_id"],p["importer_id"])})
            .Where(x=>x.markup<=1000).ToList();
        var sameImporter=peers.Where(x=>x.sameImporter).ToList();
        if(sameImporter.Count>=3) peers=sameImporter;
        var marks=peers.Select(x=>x.markup).Order().ToList();
        decimal? median=marks.Count==0?null:marks.Count%2==1?marks[marks.Count/2]:(marks[marks.Count/2-1]+marks[marks.Count/2])/2;
        var target=D(cat["target_markup"]);
        var markup=r.Markup??(peers.Count>=3?median!.Value:target);
        var minimum=landed/(1-D(cat["minimum_margin"])/100);
        var suggested=Money(Math.Ceiling(Math.Max(landed*(1+markup/100),minimum)/r.RoundTo)*r.RoundTo);
        var margin=(suggested-landed)/suggested*100;
        return new {productId=r.ProductId,version=L(p["version"]),name=p["name"],currentPrice=D(p["selling_price"]),landedCost=landed,
            suggestedPrice=suggested,markup=Money((suggested/landed-1)*100),grossMargin=Money(margin),profit=Money(suggested-landed),
            targetMarkup=target,minimumMargin=D(cat["minimum_margin"]),medianMarkup=median.HasValue?Money(median.Value):(decimal?)null,
            comparableCount=peers.Count,comparables=peers.Take(10),
            explanation=r.Markup.HasValue?"Recargo elegido manualmente, respetando el margen mínimo de la subcategoría.":
                peers.Count>=3?"Mediana del recargo de productos revisados de la misma subcategoría; se prioriza el mismo importador cuando hay al menos 3.":
                "Todavía no hay 3 productos revisados comparables. Se usa el recargo configurado en la subcategoría.",
            note="Compra y venta deben usar la misma base de impuestos. El impuesto se conserva como dato; no se suma otra vez al precio."};
    }
    public object ApplyPrice(PriceApply r)
    {
        if(r.SellingPrice<=0 || r.SellingPrice>1_000_000_000) throw new DomainException("El precio de venta debe ser mayor que cero.");
        using var db=store.Open(); using var tx=db.BeginTransaction();
        var p=GetProduct(db,r.ProductId);
        if(L(p["version"])!=r.Version) throw new DomainException("El producto cambió. Solicita una recomendación nueva.",409);
        if(r.CompleteReview && (p["category_id"] is null || p["importer_id"] is null || string.IsNullOrWhiteSpace((string?)p["barcode"]) || D(p["cost"])<=0))
            throw new DomainException("Para completar la revisión, indica código de barras, categoría y subcategoría, importador y precio de compra mayor que cero.");
        Db.Exec(db,"UPDATE catalog_products SET selling_price=@s,review_pending=CASE WHEN @complete=1 THEN 0 ELSE review_pending END,version=version+1 WHERE id=@id",
            ("@s",S(Money(r.SellingPrice))),("@complete",r.CompleteReview?1:0),("@id",r.ProductId));
        History(db,r.ProductId,D(p["selling_price"]),Money(r.SellingPrice));
        tx.Commit(); return new {applied=true,completed=r.CompleteReview};
    }
    static void History(SqliteConnection db,long id,decimal oldPrice,decimal newPrice) =>
        Db.Exec(db,"INSERT INTO catalog_price_history(product_id,old_price,new_price,changed_at) VALUES(@id,@old,@new,@now)",
            ("@id",id),("@old",S(oldPrice)),("@new",S(newPrice)),("@now",StoreDb.Now()));
}
