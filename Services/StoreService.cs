using System.Globalization;
using System.Text.Json;
using AlmacenPrimavera.Data;

namespace AlmacenPrimavera;

public sealed class StoreService(StoreDb store)
{
    private static decimal Money(decimal v)=>Math.Round(v,2,MidpointRounding.AwayFromZero);

    public object CreateProduct(ProductCreate r,CurrentUser user)
    {
        using var db=store.Open();using var tx=db.BeginTransaction();
        var result=CreateProductCore(db,r,user);tx.Commit();return result;
    }

    private static object CreateProductCore(Microsoft.Data.Sqlite.SqliteConnection db,ProductCreate r,CurrentUser user)
    {
        if(string.IsNullOrWhiteSpace(r.Name))throw new DomainException("El nombre es obligatorio.","name");
        if(r.Cost<0||r.SellingPrice<0)throw new DomainException("Costo y precio no pueden ser negativos.","cost");
        if(r.TaxRate<0||r.TaxRate>100)throw new DomainException("El impuesto debe estar entre 0 y 100.","taxRate");
        if(r.LocationId<=0)throw new DomainException("Seleccione una ubicación.","locationId");
        var cat=Db.One(db,"SELECT c.id,c.code,d.code department_code,c.required_attributes FROM categories c JOIN departments d ON d.id=c.department_id WHERE c.id=@id AND c.active=1",("@id",r.CategoryId));
        if(cat.Count==0)throw new DomainException("Categoría inválida.","categoryId");
        var internalId=string.IsNullOrWhiteSpace(r.InternalId)?NextInternalId(db,r.CategoryId,cat["department_code"]!.ToString()!,cat["code"]!.ToString()!):r.InternalId.Trim().ToUpperInvariant();
        if(internalId.Length>40||!System.Text.RegularExpressions.Regex.IsMatch(internalId,"^[A-Z0-9][A-Z0-9-]*$"))throw new DomainException("Use solo letras, números y guiones en el ID interno.","internalId");
        if(!string.IsNullOrWhiteSpace(r.Barcode))EnsureBarcodeAvailable(db,r.Barcode.Trim(),null);
        var now=StoreDb.Now();
        Db.Exec(db,"""
INSERT INTO products(internal_id,family_id,name,short_description,category_id,brand,product_type,unit_of_measure,package_size,currency,cost,selling_price,tax_rate,reorder_level,desired_stock,active,created_at,updated_at,created_by)
VALUES(@iid,@family,@name,@description,@category,@brand,'Producto',@unit,@package,'CRC',@cost,@price,@tax,@reorder,@desired,1,@now,@now,@user)
""",("@iid",internalId),("@family",r.FamilyId),("@name",r.Name.Trim()),("@description",Clean(r.Description)),("@category",r.CategoryId),("@brand",Clean(r.Brand)),("@unit",r.UnitOfMeasure.Trim()),("@package",Clean(r.PackageSize)),("@cost",Money(r.Cost)),("@price",Money(r.SellingPrice)),("@tax",r.TaxRate),("@reorder",r.ReorderLevel),("@desired",r.DesiredStock),("@now",now),("@user",user.Id));
        var id=Convert.ToInt64(Db.Scalar(db,"SELECT last_insert_rowid()")!);
        if(!string.IsNullOrWhiteSpace(r.Barcode))Db.Exec(db,"INSERT INTO barcodes(product_id,code,type,is_primary,active,created_at) VALUES(@p,@code,@type,1,1,@now)",("@p",id),("@code",r.Barcode.Trim()),("@type",NormalizeBarcodeType(r.BarcodeType)),("@now",now));
        if(r.SupplierId.HasValue)Db.Exec(db,"INSERT INTO supplier_products(product_id,supplier_id,supplier_sku,cost,package_quantity,lead_time_days,minimum_order_quantity,preferred,updated_at) VALUES(@p,@s,@sku,@cost,1,0,1,1,@now)",("@p",id),("@s",r.SupplierId.Value),("@sku",Clean(r.SupplierSku)),("@cost",Money(r.Cost)),("@now",now));
        Db.Exec(db,"INSERT INTO price_history(product_id,cost,selling_price,reason,effective_at,created_by) VALUES(@p,@cost,@price,'Precio inicial',@now,@u)",("@p",id),("@cost",Money(r.Cost)),("@price",Money(r.SellingPrice)),("@now",now),("@u",user.Id));
        Db.Exec(db,"INSERT INTO inventory_balances(product_id,location_id,on_hand,average_cost,updated_at,version) VALUES(@p,@l,@q,@cost,@now,1)",("@p",id),("@l",r.LocationId),("@q",r.OpeningStock),("@cost",Money(r.Cost)),("@now",now));
        if(r.OpeningStock!=0)Db.Exec(db,"INSERT INTO stock_movements(product_id,location_id,type,quantity,unit_cost,reason,reference,occurred_at,created_by) VALUES(@p,@l,'opening',@q,@cost,'Inventario inicial',@ref,@now,@u)",("@p",id),("@l",r.LocationId),("@q",r.OpeningStock),("@cost",Money(r.Cost)),("@ref",internalId),("@now",now),("@u",user.Id));
        Db.Audit(db,user,"product.created","product",id.ToString(),null,new{internalId,r.Name,r.CategoryId,r.Barcode,r.Cost,r.SellingPrice,r.OpeningStock},"Registro de producto");
        return new{id,internalId,message="Producto creado correctamente"};
    }

    public object UpdateProduct(long id,ProductUpdate r,CurrentUser user)
    {
        if(string.IsNullOrWhiteSpace(r.Name)||string.IsNullOrWhiteSpace(r.InternalId))throw new DomainException("El nombre y el ID interno son obligatorios.");
        if(r.Cost<0||r.SellingPrice<0||r.TaxRate<0||r.TaxRate>100)throw new DomainException("Revise costo, precio e impuesto.");
        if(string.IsNullOrWhiteSpace(r.Reason))throw new DomainException("Indique el motivo de la edición.","reason");
        var normalizedId=r.InternalId.Trim().ToUpperInvariant();
        if(normalizedId.Length>40||!System.Text.RegularExpressions.Regex.IsMatch(normalizedId,"^[A-Z0-9][A-Z0-9-]*$"))throw new DomainException("Use solo letras, números y guiones en el ID interno.","internalId");
        using var db=store.Open();using var tx=db.BeginTransaction();
        var old=Db.One(db,"SELECT p.*,(SELECT code FROM barcodes WHERE product_id=p.id AND active=1 ORDER BY is_primary DESC LIMIT 1) barcode FROM products p WHERE p.id=@id",("@id",id));
        if(old.Count==0)throw new DomainException("Producto no encontrado.");
        var categoryExists=Convert.ToInt64(Db.Scalar(db,"SELECT COUNT(*) FROM categories WHERE id=@id AND active=1",("@id",r.CategoryId))!);if(categoryExists==0)throw new DomainException("Categoría inválida.");
        if(!string.IsNullOrWhiteSpace(r.Barcode))EnsureBarcodeAvailable(db,r.Barcode.Trim(),id);
        var now=StoreDb.Now();
        Db.Exec(db,"UPDATE products SET internal_id=@iid,name=@name,short_description=@description,category_id=@category,brand=NULL,unit_of_measure=@unit,package_size=@package,family_id=@family,cost=@cost,selling_price=@price,tax_rate=@tax,reorder_level=@reorder,desired_stock=@desired,updated_at=@now WHERE id=@id",("@iid",normalizedId),("@name",r.Name.Trim()),("@description",Clean(r.Description)),("@category",r.CategoryId),("@unit",r.UnitOfMeasure.Trim()),("@package",Clean(r.PackageSize)),("@family",r.FamilyId),("@cost",Money(r.Cost)),("@price",Money(r.SellingPrice)),("@tax",r.TaxRate),("@reorder",r.ReorderLevel),("@desired",r.DesiredStock),("@now",now),("@id",id));
        var oldBarcode=old["barcode"]?.ToString();var newBarcode=Clean(r.Barcode);
        if(!string.Equals(oldBarcode,newBarcode,StringComparison.OrdinalIgnoreCase))
        {
            Db.Exec(db,"UPDATE barcodes SET active=0,is_primary=0 WHERE product_id=@p AND active=1",("@p",id));
            if(newBarcode is not null)Db.Exec(db,"INSERT INTO barcodes(product_id,code,type,is_primary,active,created_at) VALUES(@p,@code,'OTHER',1,1,@now)",("@p",id),("@code",newBarcode),("@now",now));
        }
        Db.Exec(db,"DELETE FROM supplier_products WHERE product_id=@p",("@p",id));
        if(r.SupplierId.HasValue)Db.Exec(db,"INSERT INTO supplier_products(product_id,supplier_id,supplier_sku,cost,package_quantity,lead_time_days,minimum_order_quantity,preferred,updated_at) VALUES(@p,@s,NULL,@cost,1,0,1,1,@now)",("@p",id),("@s",r.SupplierId.Value),("@cost",Money(r.Cost)),("@now",now));
        var oldCost=Convert.ToDecimal(old["cost"],CultureInfo.InvariantCulture);var oldPrice=Convert.ToDecimal(old["selling_price"],CultureInfo.InvariantCulture);
        if(oldCost!=r.Cost||oldPrice!=r.SellingPrice)Db.Exec(db,"INSERT INTO price_history(product_id,cost,selling_price,reason,effective_at,created_by) VALUES(@p,@cost,@price,@reason,@now,@u)",("@p",id),("@cost",Money(r.Cost)),("@price",Money(r.SellingPrice)),("@reason",r.Reason.Trim()),("@now",now),("@u",user.Id));
        Db.Audit(db,user,old["internal_id"]!.ToString()!=normalizedId?"product.id_reassigned":"product.updated","product",id.ToString(),old,new{internalId=normalizedId,r.Name,r.Description,r.CategoryId,r.UnitOfMeasure,r.PackageSize,r.FamilyId,r.Cost,r.SellingPrice,r.TaxRate,r.ReorderLevel,r.DesiredStock,barcode=newBarcode,r.SupplierId},r.Reason);
        tx.Commit();return new{id,internalId=normalizedId,message="Producto actualizado correctamente"};
    }

    private static string NextInternalId(Microsoft.Data.Sqlite.SqliteConnection db,long categoryId,string department,string category)
    {
        Db.Exec(db,"INSERT INTO id_sequences(category_id,next_value) VALUES(@c,2) ON CONFLICT(category_id) DO UPDATE SET next_value=next_value+1",("@c",categoryId));
        var n=Convert.ToInt64(Db.Scalar(db,"SELECT next_value-1 FROM id_sequences WHERE category_id=@c",("@c",categoryId))!);
        return $"{department}-{category}-{n:000000}";
    }
    private static string? Clean(string? s)=>string.IsNullOrWhiteSpace(s)?null:s.Trim();
    private static string NormalizeBarcodeType(string? t){var value=(t??"OTHER").Trim().ToUpperInvariant();return new[]{"UPC","EAN","ISBN","GTIN","SUPPLIER","OTHER"}.Contains(value)?value:"OTHER";}
    private static void EnsureBarcodeAvailable(Microsoft.Data.Sqlite.SqliteConnection db,string barcode,long? productId)
    {
        var match=Db.One(db,"SELECT b.product_id,p.internal_id,p.name FROM barcodes b JOIN products p ON p.id=b.product_id WHERE b.code=@code AND b.active=1 AND (@p IS NULL OR b.product_id<>@p)",("@code",barcode),("@p",productId));
        if(match.Count>0)throw new DomainException($"El código ya está asignado a {match["internal_id"]} · {match["name"]}.","barcode");
    }

    public object Adjust(StockAdjustment r,CurrentUser user)
    {
        if(string.IsNullOrWhiteSpace(r.Reason))throw new DomainException("Indique el motivo del ajuste.");
        using var db=store.Open();using var tx=db.BeginTransaction();
        var row=Db.One(db,"SELECT COALESCE(b.on_hand,0) on_hand,p.cost,p.name FROM products p LEFT JOIN inventory_balances b ON b.product_id=p.id AND b.location_id=@l WHERE p.id=@p AND p.active=1",("@p",r.ProductId),("@l",r.LocationId));
        if(row.Count==0)throw new DomainException("Producto activo no encontrado.");
        var before=Convert.ToDecimal(row["on_hand"],CultureInfo.InvariantCulture);var delta=r.NewQuantity-before;
        if(r.NewQuantity<0)throw new DomainException("El inventario negativo está deshabilitado.");
        var now=StoreDb.Now();
        Db.Exec(db,"INSERT INTO inventory_balances(product_id,location_id,on_hand,average_cost,updated_at,version) VALUES(@p,@l,@q,@cost,@now,1) ON CONFLICT(product_id,location_id) DO UPDATE SET on_hand=@q,updated_at=@now,version=version+1",("@p",r.ProductId),("@l",r.LocationId),("@q",r.NewQuantity),("@cost",row["cost"]),("@now",now));
        Db.Exec(db,"INSERT INTO stock_movements(product_id,location_id,type,quantity,unit_cost,reason,reference,occurred_at,created_by) VALUES(@p,@l,'correction',@d,@cost,@reason,'AJUSTE',@now,@u)",("@p",r.ProductId),("@l",r.LocationId),("@d",delta),("@cost",row["cost"]),("@reason",r.Reason.Trim()),("@now",now),("@u",user.Id));
        Db.Audit(db,user,"inventory.adjusted","product",r.ProductId.ToString(),new{quantity=before},new{quantity=r.NewQuantity,delta},r.Reason);tx.Commit();return new{before,after=r.NewQuantity,delta};
    }

    public object Transfer(StockTransfer r,CurrentUser user)
    {
        if(r.FromLocationId==r.ToLocationId)throw new DomainException("Origen y destino deben ser diferentes.");if(r.Quantity<=0)throw new DomainException("La cantidad debe ser mayor que cero.");
        using var db=store.Open();using var tx=db.BeginTransaction();
        var source=Db.One(db,"SELECT on_hand,average_cost FROM inventory_balances WHERE product_id=@p AND location_id=@l",("@p",r.ProductId),("@l",r.FromLocationId));
        if(source.Count==0||Convert.ToDecimal(source["on_hand"],CultureInfo.InvariantCulture)<r.Quantity)throw new DomainException("Existencia insuficiente en la ubicación de origen.");
        var now=StoreDb.Now();var cost=source["average_cost"];
        Db.Exec(db,"UPDATE inventory_balances SET on_hand=on_hand-@q,updated_at=@now,version=version+1 WHERE product_id=@p AND location_id=@l AND on_hand>=@q",("@q",r.Quantity),("@now",now),("@p",r.ProductId),("@l",r.FromLocationId));
        Db.Exec(db,"INSERT INTO inventory_balances(product_id,location_id,on_hand,average_cost,updated_at,version) VALUES(@p,@l,@q,@cost,@now,1) ON CONFLICT(product_id,location_id) DO UPDATE SET on_hand=on_hand+@q,updated_at=@now,version=version+1",("@p",r.ProductId),("@l",r.ToLocationId),("@q",r.Quantity),("@cost",cost),("@now",now));
        var reference=$"TR-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{r.ProductId}";
        Db.Exec(db,"INSERT INTO stock_movements(product_id,location_id,destination_location_id,type,quantity,unit_cost,reason,reference,occurred_at,created_by) VALUES(@p,@from,@to,'transfer_out',-@q,@cost,@reason,@ref,@now,@u),(@p,@to,@from,'transfer_in',@q,@cost,@reason,@ref,@now,@u)",("@p",r.ProductId),("@from",r.FromLocationId),("@to",r.ToLocationId),("@q",r.Quantity),("@cost",cost),("@reason",r.Reason),("@ref",reference),("@now",now),("@u",user.Id));
        Db.Audit(db,user,"inventory.transferred","product",r.ProductId.ToString(),null,new{r.FromLocationId,r.ToLocationId,r.Quantity,reference},r.Reason);tx.Commit();return new{reference,quantity=r.Quantity};
    }

    public object RecordSale(SaleRequest r,CurrentUser user)
    {
        if(r.Lines.Count==0||r.Lines.Any(x=>x.Quantity<=0))throw new DomainException("La venta requiere líneas con cantidades válidas.");
        using var db=store.Open();using var tx=db.BeginTransaction();var now=StoreDb.Now();var number=Clean(r.Reference)??$"V-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        Db.Exec(db,"INSERT INTO sales(sale_number,location_id,status,currency,occurred_at,created_by) VALUES(@n,@l,'completed','CRC',@now,@u)",("@n",number),("@l",r.LocationId),("@now",now),("@u",user.Id));var saleId=Convert.ToInt64(Db.Scalar(db,"SELECT last_insert_rowid()")!);
        decimal total=0;
        foreach(var line in r.Lines)
        {
            var product=Db.One(db,"SELECT p.selling_price,p.cost,p.tax_rate,p.active,b.on_hand,b.average_cost FROM products p LEFT JOIN inventory_balances b ON b.product_id=p.id AND b.location_id=@l WHERE p.id=@p",("@p",line.ProductId),("@l",r.LocationId));
            if(product.Count==0||Convert.ToInt64(product["active"]!)!=1)throw new DomainException("Uno de los productos no está activo.");var stock=product["on_hand"] is null?0:Convert.ToDecimal(product["on_hand"],CultureInfo.InvariantCulture);if(stock<line.Quantity)throw new DomainException("Existencia insuficiente para completar la venta.");var price=Money(line.UnitPrice??Convert.ToDecimal(product["selling_price"],CultureInfo.InvariantCulture));var cost=Money(product["average_cost"] is null?Convert.ToDecimal(product["cost"],CultureInfo.InvariantCulture):Convert.ToDecimal(product["average_cost"],CultureInfo.InvariantCulture));
            Db.Exec(db,"INSERT INTO sale_lines(sale_id,product_id,quantity,unit_price,unit_cost,tax_rate) VALUES(@s,@p,@q,@price,@cost,@tax)",("@s",saleId),("@p",line.ProductId),("@q",line.Quantity),("@price",price),("@cost",cost),("@tax",product["tax_rate"]));
            Db.Exec(db,"UPDATE inventory_balances SET on_hand=on_hand-@q,updated_at=@now,version=version+1 WHERE product_id=@p AND location_id=@l AND on_hand>=@q",("@q",line.Quantity),("@now",now),("@p",line.ProductId),("@l",r.LocationId));
            Db.Exec(db,"INSERT INTO stock_movements(product_id,location_id,type,quantity,unit_cost,reason,reference,occurred_at,created_by) VALUES(@p,@l,'sale',-@q,@cost,'Venta registrada',@ref,@now,@u)",("@p",line.ProductId),("@l",r.LocationId),("@q",line.Quantity),("@cost",cost),("@ref",number),("@now",now),("@u",user.Id));total+=price*line.Quantity;
        }
        Db.Audit(db,user,"sale.completed","sale",saleId.ToString(),null,new{number,r.LocationId,r.Lines,total=Money(total)},"Venta registrada por interfaz POS");tx.Commit();return new{saleId,saleNumber=number,total=Money(total)};
    }

    public object AssignBarcode(long productId,BarcodeRequest r,CurrentUser user)
    {
        if(string.IsNullOrWhiteSpace(r.Code))throw new DomainException("El código es obligatorio.");if(string.IsNullOrWhiteSpace(r.Reason))throw new DomainException("Indique el motivo.");using var db=store.Open();using var tx=db.BeginTransaction();var existing=Db.One(db,"SELECT b.id,b.product_id,p.internal_id FROM barcodes b JOIN products p ON p.id=b.product_id WHERE b.code=@c AND b.active=1",("@c",r.Code.Trim()));
        if(existing.Count>0&&Convert.ToInt64(existing["product_id"]!)!=productId&&!r.ResolveConflict)throw new DomainException($"El código pertenece a {existing["internal_id"]}. Un administrador debe resolver el conflicto.");
        if(existing.Count>0&&Convert.ToInt64(existing["product_id"]!)!=productId){Db.Exec(db,"UPDATE barcodes SET active=0 WHERE id=@id",("@id",existing["id"]));Db.Audit(db,user,"barcode.conflict_resolved","product",productId.ToString(),existing,new{productId,code=r.Code},r.Reason);}
        if(r.IsPrimary)Db.Exec(db,"UPDATE barcodes SET is_primary=0 WHERE product_id=@p",("@p",productId));Db.Exec(db,"INSERT INTO barcodes(product_id,code,type,is_primary,active,created_at) VALUES(@p,@c,@t,@primary,1,@now)",("@p",productId),("@c",r.Code.Trim()),("@t",NormalizeBarcodeType(r.Type)),("@primary",r.IsPrimary?1:0),("@now",StoreDb.Now()));Db.Audit(db,user,"barcode.added","product",productId.ToString(),null,new{r.Code,r.Type,r.IsPrimary},r.Reason);tx.Commit();return new{productId,code=r.Code};
    }

    public object RecommendPrice(PriceRecommendationRequest r,CurrentUser user)
    {
        using var db=store.Open();
        var p=Db.One(db,"SELECT p.*,c.minimum_margin,c.target_markup,c.name category FROM products p JOIN categories c ON c.id=p.category_id WHERE p.id=@id",("@id",r.ProductId));if(p.Count==0)throw new DomainException("Producto no encontrado.");
        var baseCost=Convert.ToDecimal(p["cost"],CultureInfo.InvariantCulture);var landed=Money(baseCost+r.Freight+r.Duties+r.Handling+r.Other-r.Discount);if(landed<=0)throw new DomainException("El costo puesto en tienda debe ser mayor que cero.");
        var count=Math.Clamp(r.ComparableCount,2,10);
        var comps=Db.Query(db,@"SELECT p.id,p.internal_id,p.name,p.brand,p.cost,p.selling_price,
 ROUND((p.selling_price-p.cost)/p.cost*100,2) markup,
 (CASE WHEN p.category_id=@cat THEN 5 ELSE 0 END)+(CASE WHEN lower(COALESCE(p.brand,''))=lower(COALESCE(@brand,'')) AND @brand<>'' THEN 2 ELSE 0 END)+(CASE WHEN p.unit_of_measure=@unit THEN 1 ELSE 0 END) similarity
 FROM products p WHERE p.id<>@id AND p.active=1 AND p.discontinued=0 AND p.cost>0 AND p.selling_price>=p.cost AND (p.category_id=@cat OR p.brand=@brand)
 ORDER BY similarity DESC,ABS(p.cost-@cost) ASC,p.updated_at DESC LIMIT @take",("@cat",p["category_id"]),("@brand",p["brand"]??""),("@unit",p["unit_of_measure"]),("@id",r.ProductId),("@cost",baseCost),("@take",count));
        var markups=comps.Select(x=>Convert.ToDecimal(x["markup"],CultureInfo.InvariantCulture)).Order().ToList();
        var target=Convert.ToDecimal(p["target_markup"],CultureInfo.InvariantCulture);var median=markups.Count==0?target:(markups.Count%2==1?markups[markups.Count/2]:(markups[markups.Count/2-1]+markups[markups.Count/2])/2);
        var totalWeight=comps.Sum(x=>Convert.ToDecimal(x["similarity"],CultureInfo.InvariantCulture));var weighted=totalWeight==0?target:comps.Sum(x=>Convert.ToDecimal(x["markup"],CultureInfo.InvariantCulture)*Convert.ToDecimal(x["similarity"],CultureInfo.InvariantCulture))/totalWeight;
        var blended=comps.Count<2?target:(median*.55m+weighted*.30m+target*.15m);var raw=landed*(1+blended/100);var suggested=PsychologicalRound(raw,95);
        var margin=Money((suggested-landed)/suggested*100);var profit=Money(suggested-landed);var low=PsychologicalRound(landed*(1+Math.Max(0,blended-10)/100),95);var high=PsychologicalRound(landed*(1+(blended+10)/100),95);var confidence=comps.Count>=5?"Alta":comps.Count>=2?"Media":"Baja";
        var explanation=comps.Count>0?$"Se comparó con {comps.Count} producto(s) de la misma categoría o marca. La mediana de recargo es {median:0.0}% y la regla de la categoría apunta a {target:0.0}%. Se aplicó terminación psicológica .95.":$"No hay comparables suficientes; se aplicó el recargo objetivo de la categoría ({target:0.0}%) y terminación .95.";
        var now=StoreDb.Now();Db.Exec(db,"INSERT INTO price_recommendations(product_id,landed_cost,suggested_price,median_markup,weighted_markup,gross_margin,expected_profit,range_low,range_high,confidence,explanation,comparables,status,previous_price,created_at,created_by) VALUES(@p,@landed,@suggested,@median,@weighted,@margin,@profit,@low,@high,@confidence,@explanation,@comparables,'pending',@previous,@now,@u)",("@p",r.ProductId),("@landed",landed),("@suggested",suggested),("@median",Money(median)),("@weighted",Money(weighted)),("@margin",margin),("@profit",profit),("@low",low),("@high",high),("@confidence",confidence),("@explanation",explanation),("@comparables",JsonSerializer.Serialize(comps)),("@previous",p["selling_price"]),("@now",now),("@u",user.Id));
        var id=Convert.ToInt64(Db.Scalar(db,"SELECT last_insert_rowid()")!);Db.Audit(db,user,"price.recommended","product",r.ProductId.ToString(),new{price=p["selling_price"]},new{id,suggested,landed,margin},"Recomendación generada");
        return new{id,landedCost=landed,suggestedPrice=suggested,markup=Money((suggested-landed)/landed*100),grossMargin=margin,expectedProfit=profit,rangeLow=low,rangeHigh=high,medianMarkup=Money(median),weightedMarkup=Money(weighted),confidence,explanation,comparables=comps,warnings=Warnings(landed,suggested,Convert.ToDecimal(p["minimum_margin"],CultureInfo.InvariantCulture),margin)};
    }
    private static decimal PsychologicalRound(decimal value,int ending){var whole=Math.Floor(value/100)*100;var candidate=whole+ending;if(candidate<value)candidate+=100;return Money(candidate);}
    private static List<string> Warnings(decimal cost,decimal price,decimal minMargin,decimal margin){var x=new List<string>();if(price<cost)x.Add("El precio está por debajo del costo.");if(margin<minMargin)x.Add($"Margen menor al mínimo configurado ({minMargin:0.##}%).");return x;}

    public object DecidePrice(long id,PriceDecision r,CurrentUser user)
    {
        if(r.Decision is not("approved" or "rejected"))throw new DomainException("Decisión inválida.");if(string.IsNullOrWhiteSpace(r.Reason))throw new DomainException("Indique el motivo de la decisión.");
        using var db=store.Open();using var tx=db.BeginTransaction();var rec=Db.One(db,"SELECT * FROM price_recommendations WHERE id=@id AND status='pending'",("@id",id));if(rec.Count==0)throw new DomainException("Recomendación pendiente no encontrada.");var now=StoreDb.Now();
        Db.Exec(db,"UPDATE price_recommendations SET status=@s,decided_by=@u,decided_at=@now,decision_reason=@reason WHERE id=@id AND status='pending'",("@s",r.Decision),("@u",user.Id),("@now",now),("@reason",r.Reason.Trim()),("@id",id));
        if(r.Decision=="approved")
        {
            Db.Exec(db,"UPDATE products SET selling_price=@price,updated_at=@now WHERE id=@p",("@price",rec["suggested_price"]),("@now",now),("@p",rec["product_id"]));
            Db.Exec(db,"INSERT INTO price_history(product_id,cost,selling_price,reason,effective_at,created_by) SELECT product_id,landed_cost,suggested_price,@reason,@now,@u FROM price_recommendations WHERE id=@id",("@reason",r.Reason),("@now",now),("@u",user.Id),("@id",id));
        }
        Db.Audit(db,user,$"price.{r.Decision}","product",rec["product_id"]!.ToString()!,new{price=rec["previous_price"]},new{price=r.Decision=="approved"?rec["suggested_price"]:rec["previous_price"],recommendationId=id},r.Reason);tx.Commit();return new{id,status=r.Decision,applied=r.Decision=="approved"};
    }

    public object PreviewInventoryImport(string csv)
    {
        using var db=store.Open();var rows=ValidateInventoryCsv(csv,db);
        return new{rows=rows.Select(x=>new{row=x.Number,data=x.Data,product=x.ProductName,location=x.LocationName,currentQuantity=x.CurrentQuantity,newQuantity=x.Quantity,valid=x.Errors.Count==0,errors=x.Errors}),total=rows.Count,valid=rows.Count(x=>x.Errors.Count==0),invalid=rows.Count(x=>x.Errors.Count>0)};
    }
    public object ApplyInventoryImport(string csv,CurrentUser user)
    {
        using var db=store.Open();var rows=ValidateInventoryCsv(csv,db);if(rows.Count==0)throw new DomainException("El archivo no contiene filas de inventario.");if(rows.Any(x=>x.Errors.Count>0))throw new DomainException("Corrija todos los errores antes de actualizar el inventario.");
        using var tx=db.BeginTransaction();var now=StoreDb.Now();
        foreach(var row in rows)
        {
            var x=row.Data;long productId;
            if(row.ProductId.HasValue)productId=row.ProductId.Value;
            else
            {
                var categoryId=Convert.ToInt64(Db.Scalar(db,"SELECT id FROM categories WHERE code=@code AND active=1",("@code",x["category_code"]))!);long? supplierId=long.TryParse(x.GetValueOrDefault("supplier_id"),out var supplier)?supplier:null;
                CreateProductCore(db,new ProductCreate(x["name"],Clean(x.GetValueOrDefault("internal_id")),categoryId,null,Clean(x.GetValueOrDefault("description")),x.GetValueOrDefault("unit_of_measure")??"unidad",Clean(x.GetValueOrDefault("package_size")),ParseRequired(x,"cost"),ParseRequired(x,"selling_price"),ParseRequired(x,"tax_rate"),ParseOptional(x,"reorder_level")??0,ParseOptional(x,"desired_stock")??0,x["barcode"],"OTHER",supplierId,null,0,row.LocationId!.Value,null),user);
                productId=Convert.ToInt64(Db.Scalar(db,"SELECT product_id FROM barcodes WHERE code=@code AND active=1",("@code",x["barcode"]))!);
            }
            var before=Db.One(db,"SELECT * FROM products WHERE id=@id",("@id",productId));var category=string.IsNullOrWhiteSpace(x.GetValueOrDefault("category_code"))?null:Db.Scalar(db,"SELECT id FROM categories WHERE code=@code AND active=1",("@code",x["category_code"]));var newCost=ParseOptional(x,"cost");var newPrice=ParseOptional(x,"selling_price");var newTax=ParseOptional(x,"tax_rate");
            Db.Exec(db,"UPDATE products SET name=CASE WHEN @name='' THEN name ELSE @name END,short_description=COALESCE(@description,short_description),category_id=COALESCE(@category,category_id),unit_of_measure=CASE WHEN @unit='' THEN unit_of_measure ELSE @unit END,package_size=COALESCE(@package,package_size),cost=COALESCE(@cost,cost),selling_price=COALESCE(@price,selling_price),tax_rate=COALESCE(@tax,tax_rate),reorder_level=COALESCE(@reorder,reorder_level),desired_stock=COALESCE(@desired,desired_stock),updated_at=@now WHERE id=@id",("@name",x.GetValueOrDefault("name")??""),("@description",Clean(x.GetValueOrDefault("description"))),("@category",category),("@unit",x.GetValueOrDefault("unit_of_measure")??""),("@package",Clean(x.GetValueOrDefault("package_size"))),("@cost",newCost),("@price",newPrice),("@tax",newTax),("@reorder",ParseOptional(x,"reorder_level")),("@desired",ParseOptional(x,"desired_stock")),("@now",now),("@id",productId));
            if(newCost.HasValue||newPrice.HasValue)Db.Exec(db,"INSERT INTO price_history(product_id,cost,selling_price,reason,effective_at,created_by) SELECT id,cost,selling_price,@reason,@now,@u FROM products WHERE id=@id",("@reason",row.Reason),("@now",now),("@u",user.Id),("@id",productId));
            var quantity=row.Quantity!.Value;var delta=quantity-row.CurrentQuantity;var cost=Db.Scalar(db,"SELECT cost FROM products WHERE id=@id",("@id",productId));
            Db.Exec(db,"INSERT INTO inventory_balances(product_id,location_id,on_hand,average_cost,updated_at,version) VALUES(@p,@l,@q,@cost,@now,1) ON CONFLICT(product_id,location_id) DO UPDATE SET on_hand=@q,average_cost=@cost,updated_at=@now,version=version+1",("@p",productId),("@l",row.LocationId!.Value),("@q",quantity),("@cost",cost),("@now",now));
            Db.Exec(db,"INSERT INTO stock_movements(product_id,location_id,type,quantity,unit_cost,reason,reference,occurred_at,created_by) VALUES(@p,@l,'count',@delta,@cost,@reason,'CSV-INVENTARIO',@now,@u)",("@p",productId),("@l",row.LocationId.Value),("@delta",delta),("@cost",cost),("@reason",row.Reason),("@now",now),("@u",user.Id));
            Db.Audit(db,user,"inventory.csv_updated","product",productId.ToString(),new{product=before,quantity=row.CurrentQuantity,locationId=row.LocationId},new{productFields=x,quantity,delta,locationId=row.LocationId},row.Reason);
        }
        tx.Commit();return new{updated=rows.Count,message="Inventario actualizado correctamente"};
    }
    private static List<InventoryCsvRow> ValidateInventoryCsv(string csv,Microsoft.Data.Sqlite.SqliteConnection db)
    {
        var source=ParseCsv(csv);var result=new List<InventoryCsvRow>();var seen=new HashSet<string>();
        for(var i=0;i<source.Count;i++)
        {
            var x=source[i];var errors=new List<string>();var internalId=x.GetValueOrDefault("internal_id")??"";var barcode=x.GetValueOrDefault("barcode")??"";var locationCode=x.GetValueOrDefault("location_code")??"";
            if(string.IsNullOrWhiteSpace(internalId)&&string.IsNullOrWhiteSpace(barcode))errors.Add("Indique internal_id o barcode");
            var product=Db.One(db,"SELECT p.id,p.name,p.cost FROM products p LEFT JOIN barcodes b ON b.product_id=p.id AND b.active=1 WHERE p.active=1 AND ((@iid<>'' AND p.internal_id=@iid) OR (@barcode<>'' AND b.code=@barcode)) GROUP BY p.id",("@iid",internalId.Trim()),("@barcode",barcode.Trim()));
            var location=Db.One(db,"SELECT id,name FROM locations WHERE code=@code AND active=1",("@code",locationCode.Trim()));
            if(location.Count==0)errors.Add("location_code inválido");
            if(product.Count==0)
            {
                if(string.IsNullOrWhiteSpace(barcode))errors.Add("Un producto nuevo requiere barcode");if(string.IsNullOrWhiteSpace(x.GetValueOrDefault("name")))errors.Add("Un producto nuevo requiere name");
                var categoryCode=x.GetValueOrDefault("category_code")??"";if(Convert.ToInt64(Db.Scalar(db,"SELECT COUNT(*) FROM categories WHERE code=@code AND active=1",("@code",categoryCode))!)==0)errors.Add("category_code inválido");
                foreach(var field in new[]{"cost","selling_price","tax_rate"})if(ParseOptional(x,field) is null)errors.Add($"{field} inválido o faltante");
            }
            else foreach(var field in new[]{"cost","selling_price","tax_rate","reorder_level","desired_stock"})if(!string.IsNullOrWhiteSpace(x.GetValueOrDefault(field))&&ParseOptional(x,field) is null)errors.Add($"{field} inválido");
            decimal? quantity=null;if(decimal.TryParse(x.GetValueOrDefault("quantity"),NumberStyles.Number,CultureInfo.InvariantCulture,out var parsed)&&parsed>=0)quantity=parsed;else errors.Add("quantity debe ser cero o mayor");
            var reason=string.IsNullOrWhiteSpace(x.GetValueOrDefault("reason"))?"Actualización de inventario por CSV":x["reason"].Trim();
            long? productId=product.Count==0?null:Convert.ToInt64(product["id"]!);long? locationId=location.Count==0?null:Convert.ToInt64(location["id"]!);var current=productId.HasValue&&locationId.HasValue?Convert.ToDecimal(Db.Scalar(db,"SELECT COALESCE((SELECT on_hand FROM inventory_balances WHERE product_id=@p AND location_id=@l),0)",("@p",productId.Value),("@l",locationId.Value))!,CultureInfo.InvariantCulture):0;
            if(productId.HasValue&&locationId.HasValue&&!seen.Add($"{productId}:{locationId}"))errors.Add("Producto y ubicación repetidos en el archivo");
            result.Add(new InventoryCsvRow(i+2,x,productId,product.GetValueOrDefault("name")?.ToString()??x.GetValueOrDefault("name")??"Producto nuevo",product.GetValueOrDefault("cost")??ParseOptional(x,"cost"),locationId,location.GetValueOrDefault("name")?.ToString(),current,quantity,reason,errors));
        }
        return result;
    }
    private static List<Dictionary<string,string>> ParseCsv(string csv)
    {
        var lines=csv.Replace("\r","").Split('\n',StringSplitOptions.RemoveEmptyEntries);if(lines.Length<2)return[];var headers=CsvLine(lines[0]).Select(x=>x.Trim().ToLowerInvariant()).ToArray();var rows=new List<Dictionary<string,string>>();foreach(var line in lines.Skip(1)){var values=CsvLine(line);var row=new Dictionary<string,string>();for(var i=0;i<headers.Length;i++)row[headers[i]]=i<values.Count?values[i].Trim():"";rows.Add(row);}return rows;
    }
    private static decimal? ParseOptional(Dictionary<string,string> row,string field)=>decimal.TryParse(row.GetValueOrDefault(field),NumberStyles.Number,CultureInfo.InvariantCulture,out var value)&&value>=0?value:null;
    private static decimal ParseRequired(Dictionary<string,string> row,string field)=>ParseOptional(row,field)??throw new DomainException($"{field} inválido.");
    private static List<string> CsvLine(string line){var result=new List<string>();var current="";var quoted=false;for(var i=0;i<line.Length;i++){var ch=line[i];if(ch=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){current+='"';i++;}else quoted=!quoted;}else if(ch==','&&!quoted){result.Add(current);current="";}else current+=ch;}result.Add(current);return result;}
    private sealed record InventoryCsvRow(int Number,Dictionary<string,string> Data,long? ProductId,string? ProductName,object? Cost,long? LocationId,string? LocationName,decimal CurrentQuantity,decimal? Quantity,string Reason,List<string> Errors);
}
