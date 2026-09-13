using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlmacenPrimavera;
using AlmacenPrimavera.Data;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StoreDb>();
builder.Services.AddSingleton<StoreService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

var database = app.Services.GetRequiredService<StoreDb>();
database.Initialize();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; img-src 'self' data:; font-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth/login"))
    {
        var user = Auth.CurrentUser(ctx, database);
        if (user is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Sesión requerida" }); return; }
        ctx.Items["user"] = user;
    }
    await next();
});

app.MapPost("/api/auth/login", (LoginRequest request, HttpContext ctx) =>
{
    using var db = database.Open();
    using var cmd = Db.Cmd(db, "SELECT id,username,display_name,role,password_hash,password_salt FROM users WHERE username=@u AND active=1", ("@u", request.Username.Trim()));
    using var reader = cmd.ExecuteReader();
    if (!reader.Read() || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(reader.GetString(4)), Convert.FromBase64String(StoreDb.HashPassword(request.Password, reader.GetString(5)))))
        return Results.Json(new { error = "Usuario o contraseña incorrectos" }, statusCode: 401);
    var user = new CurrentUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    reader.Close();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var hash = Auth.TokenHash(token);
    Db.Exec(db, "DELETE FROM sessions WHERE expires_at < @now; INSERT INTO sessions(token_hash,user_id,expires_at,created_at) VALUES(@t,@u,@exp,@now)",
        ("@t", hash), ("@u", user.Id), ("@exp", DateTimeOffset.UtcNow.AddHours(12).ToString("O")), ("@now", StoreDb.Now()));
    ctx.Response.Cookies.Append("primavera_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), Path = "/" });
    return Results.Ok(new { user.Id, user.Username, user.DisplayName, user.Role, permissions = Auth.Permissions(user.Role) });
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies.TryGetValue("primavera_session", out var token))
    {
        using var db = database.Open(); Db.Exec(db, "DELETE FROM sessions WHERE token_hash=@t", ("@t", Auth.TokenHash(token)));
    }
    ctx.Response.Cookies.Delete("primavera_session");
    return Results.NoContent();
});
app.MapGet("/api/auth/me", (HttpContext ctx) => { var u = ctx.UserRecord(); return Results.Ok(new { u.Id, u.Username, u.DisplayName, u.Role, permissions = Auth.Permissions(u.Role) }); });

app.MapGet("/api/lookups", () =>
{
    using var db = database.Open();
    return Results.Ok(new {
        categories = Db.Query(db, "SELECT c.id,c.name,c.code,c.parent_id,d.name department,d.code department_code,c.minimum_margin,c.target_markup FROM categories c JOIN departments d ON d.id=c.department_id WHERE c.active=1 ORDER BY d.name,c.name"),
        suppliers = Db.Query(db, "SELECT id,name FROM suppliers WHERE active=1 ORDER BY name"),
        locations = Db.Query(db, "SELECT id,name,code FROM locations WHERE active=1 ORDER BY name"),
        families = Db.Query(db, "SELECT id,name,description FROM product_families ORDER BY name"),
        settings = Db.Query(db, "SELECT key,value FROM settings ORDER BY key")
    });
});

app.MapGet("/api/catalog-settings", (HttpContext ctx) =>
{
    var u=ctx.Require("settings.write");if(u.Error is not null)return u.Error;using var db=database.Open();
    return Results.Ok(new{
        departments=Db.Query(db,"SELECT id,name,code,active FROM departments ORDER BY name"),
        categories=Db.Query(db,"SELECT c.id,c.department_id,c.parent_id,c.name,c.code,c.minimum_margin,c.target_markup,c.active,d.name department FROM categories c JOIN departments d ON d.id=c.department_id ORDER BY d.name,c.name"),
        suppliers=Db.Query(db,"SELECT id,name,email,phone,active FROM suppliers ORDER BY name")
    });
});

app.MapPost("/api/categories", (CategoryEdit r,HttpContext ctx) =>
{
    var u=ctx.Require("settings.write");if(u.Error is not null)return u.Error;
    var error=ValidateCategory(r);if(error is not null)return Results.BadRequest(new{error});
    try{using var db=database.Open();Db.Exec(db,"INSERT INTO categories(department_id,parent_id,name,code,minimum_margin,target_markup,required_attributes,active) VALUES(@d,@parent,@name,@code,@margin,@markup,'[]',@active)",("@d",r.DepartmentId),("@parent",r.ParentId),("@name",r.Name.Trim()),("@code",r.Code.Trim().ToUpperInvariant()),("@margin",r.MinimumMargin),("@markup",r.TargetMarkup),("@active",r.Active?1:0));var id=Convert.ToInt64(Db.Scalar(db,"SELECT last_insert_rowid()")!);Db.Audit(db,u.User!,"category.created","category",id.ToString(),null,r,r.Reason);return Results.Ok(new{id});}
    catch(SqliteException e) when(e.SqliteErrorCode==19){return Results.Conflict(new{error="El código ya existe o la relación seleccionada no es válida."});}
});

app.MapPut("/api/categories/{id:long}", (long id,CategoryEdit r,HttpContext ctx) =>
{
    var u=ctx.Require("settings.write");if(u.Error is not null)return u.Error;
    var error=ValidateCategory(r);if(error is not null)return Results.BadRequest(new{error});if(r.ParentId==id)return Results.BadRequest(new{error="Una categoría no puede ser su propia categoría superior."});
    try{using var db=database.Open();var old=Db.One(db,"SELECT * FROM categories WHERE id=@id",("@id",id));if(old.Count==0)return Results.NotFound(new{error="Categoría no encontrada."});Db.Exec(db,"UPDATE categories SET department_id=@d,parent_id=@parent,name=@name,code=@code,minimum_margin=@margin,target_markup=@markup,active=@active WHERE id=@id",("@d",r.DepartmentId),("@parent",r.ParentId),("@name",r.Name.Trim()),("@code",r.Code.Trim().ToUpperInvariant()),("@margin",r.MinimumMargin),("@markup",r.TargetMarkup),("@active",r.Active?1:0),("@id",id));Db.Audit(db,u.User!,"category.updated","category",id.ToString(),old,r,r.Reason);return Results.Ok(new{id});}
    catch(SqliteException e) when(e.SqliteErrorCode==19){return Results.Conflict(new{error="El código ya existe o la categoría todavía está siendo utilizada."});}
});

app.MapPost("/api/suppliers", (SupplierEdit r,HttpContext ctx) =>
{
    var u=ctx.Require("settings.write");if(u.Error is not null)return u.Error;if(string.IsNullOrWhiteSpace(r.Name))return Results.BadRequest(new{error="El nombre es obligatorio."});
    using var db=database.Open();Db.Exec(db,"INSERT INTO suppliers(name,email,phone,active) VALUES(@name,@email,@phone,@active)",("@name",r.Name.Trim()),("@email",CleanValue(r.Email)),("@phone",CleanValue(r.Phone)),("@active",r.Active?1:0));var id=Convert.ToInt64(Db.Scalar(db,"SELECT last_insert_rowid()")!);Db.Audit(db,u.User!,"supplier.created","supplier",id.ToString(),null,r,r.Reason);return Results.Ok(new{id});
});

app.MapPut("/api/suppliers/{id:long}", (long id,SupplierEdit r,HttpContext ctx) =>
{
    var u=ctx.Require("settings.write");if(u.Error is not null)return u.Error;if(string.IsNullOrWhiteSpace(r.Name))return Results.BadRequest(new{error="El nombre es obligatorio."});
    using var db=database.Open();var old=Db.One(db,"SELECT * FROM suppliers WHERE id=@id",("@id",id));if(old.Count==0)return Results.NotFound(new{error="Proveedor no encontrado."});Db.Exec(db,"UPDATE suppliers SET name=@name,email=@email,phone=@phone,active=@active WHERE id=@id",("@name",r.Name.Trim()),("@email",CleanValue(r.Email)),("@phone",CleanValue(r.Phone)),("@active",r.Active?1:0),("@id",id));Db.Audit(db,u.User!,"supplier.updated","supplier",id.ToString(),old,r,r.Reason);return Results.Ok(new{id});
});

app.MapGet("/api/dashboard", () =>
{
    using var db = database.Open();
    var kpis = Db.One(db, """
SELECT COUNT(*) products,
 COALESCE(SUM(CASE WHEN stock<=0 THEN 1 ELSE 0 END),0) out_of_stock,
 COALESCE(SUM(CASE WHEN stock>0 AND stock<=reorder_level THEN 1 ELSE 0 END),0) low_stock,
 COALESCE(SUM(stock*cost),0) inventory_value
FROM (SELECT p.id,p.cost,p.reorder_level,COALESCE(SUM(b.on_hand),0) stock FROM products p LEFT JOIN inventory_balances b ON b.product_id=p.id WHERE p.active=1 GROUP BY p.id)
""");
    return Results.Ok(new {
        kpis,
        lowStock = Db.Query(db, "SELECT p.id,p.internal_id,p.name,p.reorder_level,COALESCE(SUM(b.on_hand),0) stock FROM products p LEFT JOIN inventory_balances b ON b.product_id=p.id WHERE p.active=1 GROUP BY p.id HAVING stock<=p.reorder_level ORDER BY stock LIMIT 6"),
        activity = Db.Query(db, "SELECT a.action,a.entity_type,a.entity_id,a.reason,a.occurred_at,u.display_name user FROM audit_events a JOIN users u ON u.id=a.user_id ORDER BY a.occurred_at DESC LIMIT 7"),
        movements = Db.Query(db, "SELECT m.type,m.quantity,m.occurred_at,p.name,l.name location FROM stock_movements m JOIN products p ON p.id=m.product_id JOIN locations l ON l.id=m.location_id ORDER BY m.occurred_at DESC LIMIT 7")
    });
});

app.MapGet("/api/products", (HttpRequest req) =>
{
    using var db = database.Open();
    var q = req.Query["q"].ToString().Trim();
    var filter = req.Query["filter"].ToString();
    var category = long.TryParse(req.Query["category"], out var cat) ? cat : 0;
    var page = Math.Max(1, int.TryParse(req.Query["page"], out var p) ? p : 1);
    var size = Math.Clamp(int.TryParse(req.Query["size"], out var s) ? s : 25, 1, 100);
    var where = new List<string> { "1=1" };
    if (q.Length > 0) where.Add("(p.internal_id LIKE @q OR p.name LIKE @q OR EXISTS(SELECT 1 FROM barcodes x WHERE x.product_id=p.id AND x.active=1 AND x.code LIKE @q))");
    if (category > 0) where.Add("p.category_id=@cat");
    where.Add(filter switch { "low" => "stock>0 AND stock<=p.reorder_level", "out" => "stock<=0", "inactive" => "p.active=0", "incomplete" => "(NOT EXISTS(SELECT 1 FROM barcodes x WHERE x.product_id=p.id AND x.active=1) OR NOT EXISTS(SELECT 1 FROM supplier_products sp WHERE sp.product_id=p.id))", _ => "p.active=1" });
    var core = $"FROM products p JOIN categories c ON c.id=p.category_id JOIN departments d ON d.id=c.department_id LEFT JOIN (SELECT product_id,SUM(on_hand) stock FROM inventory_balances GROUP BY product_id) b ON b.product_id=p.id WHERE {string.Join(" AND ",where)}";
    (string, object?)[] args = [("@q", $"%{q}%"), ("@cat", category), ("@take", size), ("@skip", (page-1)*size)];
    var items = Db.Query(db, $"SELECT p.id,p.internal_id,p.name,p.brand,p.unit_of_measure,p.cost,p.selling_price,p.currency,p.reorder_level,p.active,c.name category,d.name department,COALESCE(b.stock,0) stock,(SELECT code FROM barcodes x WHERE x.product_id=p.id AND x.active=1 ORDER BY is_primary DESC LIMIT 1) barcode {core} ORDER BY p.updated_at DESC LIMIT @take OFFSET @skip", args);
    var total = Convert.ToInt32(Db.Scalar(db, $"SELECT COUNT(*) {core}", args) ?? 0);
    return Results.Ok(new { items, page, size, total, pages = Math.Max(1, (int)Math.Ceiling(total/(double)size)) });
});

app.MapGet("/api/products/{id:long}", (long id) =>
{
    using var db = database.Open();
    var product = Db.One(db, "SELECT p.*,c.name category,c.code category_code,d.name department,d.code department_code,f.name family_name FROM products p JOIN categories c ON c.id=p.category_id JOIN departments d ON d.id=c.department_id LEFT JOIN product_families f ON f.id=p.family_id WHERE p.id=@id", ("@id",id));
    if (product.Count == 0) return Results.NotFound(new { error="Producto no encontrado" });
    return Results.Ok(new { product,
        barcodes=Db.Query(db,"SELECT id,code,type,is_primary FROM barcodes WHERE product_id=@id AND active=1 ORDER BY is_primary DESC",("@id",id)),
        stock=Db.Query(db,"SELECT b.location_id,l.name location,b.on_hand,b.average_cost,b.version FROM inventory_balances b JOIN locations l ON l.id=b.location_id WHERE b.product_id=@id",("@id",id)),
        suppliers=Db.Query(db,"SELECT sp.id,sp.supplier_id,s.name,sp.cost,sp.package_quantity,sp.lead_time_days,sp.minimum_order_quantity,sp.preferred FROM supplier_products sp JOIN suppliers s ON s.id=sp.supplier_id WHERE sp.product_id=@id",("@id",id)),
        variants=Db.Query(db,"SELECT v.id,v.internal_id,v.name,v.package_size,v.cost,v.selling_price,COALESCE(SUM(b.on_hand),0) stock FROM products v LEFT JOIN inventory_balances b ON b.product_id=v.id WHERE v.family_id=(SELECT family_id FROM products WHERE id=@id) AND v.id<>@id GROUP BY v.id ORDER BY v.name",("@id",id)),
        priceHistory=Db.Query(db,"SELECT h.*,u.display_name user FROM price_history h JOIN users u ON u.id=h.created_by WHERE h.product_id=@id ORDER BY effective_at DESC LIMIT 20",("@id",id)),
        movements=Db.Query(db,"SELECT m.*,l.name location,u.display_name user FROM stock_movements m JOIN locations l ON l.id=m.location_id JOIN users u ON u.id=m.created_by WHERE m.product_id=@id ORDER BY occurred_at DESC LIMIT 30",("@id",id)),
        audit=Db.Query(db,"SELECT a.*,u.display_name user FROM audit_events a JOIN users u ON u.id=a.user_id WHERE a.entity_type='product' AND a.entity_id=@id ORDER BY occurred_at DESC LIMIT 30",("@id",id))
    });
});

app.MapPost("/api/products", (ProductCreate r, HttpContext ctx, StoreService service) =>
{
    var u = ctx.Require("product.write"); if (u.Error is not null) return u.Error;
    try { return Results.Created("/api/products", service.CreateProduct(r, u.User!)); }
    catch (DomainException e) { return Results.BadRequest(new { error=e.Message, field=e.Field }); }
    catch (SqliteException e) when (e.SqliteErrorCode == 19) { return Results.Conflict(new { error="El ID interno o código de barras ya pertenece a otro producto." }); }
});

app.MapPut("/api/products/{id:long}", (long id,ProductUpdate r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("product.write");if(u.Error is not null)return u.Error;
    using(var checkDb=database.Open())
    {
        var current=Db.One(checkDb,"SELECT internal_id,cost,selling_price FROM products WHERE id=@id",("@id",id));
        if(current.Count==0)return Results.NotFound(new{error="Producto no encontrado"});
        var idChanged=!string.Equals(current["internal_id"]?.ToString(),r.InternalId?.Trim(),StringComparison.OrdinalIgnoreCase);
        if(idChanged&&u.User!.Role!="administrator")return Results.Json(new{error="Solo un administrador puede reasignar el ID interno."},statusCode:403);
        var financialChanged=Convert.ToDecimal(current["cost"])!=r.Cost||Convert.ToDecimal(current["selling_price"])!=r.SellingPrice;
        if(financialChanged&&!Auth.Permissions(u.User!.Role).Contains("price.approve"))return Results.Json(new{error="No tiene permiso para cambiar costo o precio."},statusCode:403);
    }
    try{return Results.Ok(service.UpdateProduct(id,r,u.User!));}
    catch(DomainException e){return Results.BadRequest(new{error=e.Message,field=e.Field});}
    catch(SqliteException e) when(e.SqliteErrorCode==19){return Results.Conflict(new{error="El ID interno o código de barras ya está en uso."});}
});

app.MapPatch("/api/products/{id:long}/status", (long id, StatusRequest r, HttpContext ctx) =>
{
    var u=ctx.Require("product.deactivate"); if(u.Error is not null)return u.Error;
    using var db=database.Open(); using var tx=db.BeginTransaction();
    var old=Db.One(db,"SELECT active FROM products WHERE id=@id",("@id",id)); if(old.Count==0)return Results.NotFound();
    Db.Exec(db,"UPDATE products SET active=@a,updated_at=@now WHERE id=@id",("@a",r.Active?1:0),("@now",StoreDb.Now()),("@id",id));
    Db.Audit(db,u.User!,r.Active?"product.activated":"product.deactivated","product",id.ToString(),old,new{active=r.Active},r.Reason);
    tx.Commit(); return Results.Ok(new{active=r.Active});
});

app.MapPost("/api/inventory/adjust", (StockAdjustment r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("inventory.adjust");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.Adjust(r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});
app.MapPost("/api/inventory/transfer", (StockTransfer r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("inventory.adjust");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.Transfer(r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});

app.MapPost("/api/sales", (SaleRequest r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("sale.record");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.RecordSale(r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});

app.MapPost("/api/products/{id:long}/barcodes", (long id,BarcodeRequest r,HttpContext ctx,StoreService service) =>
{
    var permission=r.ResolveConflict?"barcode.resolve":"product.write";var u=ctx.Require(permission);if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.AssignBarcode(id,r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});

app.MapPost("/api/pricing/recommend", (PriceRecommendationRequest r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("price.view");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.RecommendPrice(r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});
app.MapPost("/api/pricing/{id:long}/decision", (long id,PriceDecision r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("price.approve");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.DecidePrice(id,r,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});

app.MapGet("/api/reports/reorder", (HttpContext ctx) =>
{
    var u=ctx.Require("reports.basic");if(u.Error is not null)return u.Error;
    using var db=database.Open();
    return Results.Ok(Db.Query(db,"""
SELECT p.id,p.internal_id,p.name,c.name category,COALESCE(SUM(b.on_hand),0) stock,p.reorder_level,p.desired_stock,
 MAX(0,p.desired_stock-COALESCE(SUM(b.on_hand),0)) suggested_quantity,
 COALESCE((SELECT sp.lead_time_days FROM supplier_products sp WHERE sp.product_id=p.id ORDER BY preferred DESC LIMIT 1),0) lead_time_days
FROM products p JOIN categories c ON c.id=p.category_id LEFT JOIN inventory_balances b ON b.product_id=p.id
WHERE p.active=1 GROUP BY p.id HAVING stock<=p.reorder_level ORDER BY (p.reorder_level-stock) DESC
"""));
});
app.MapGet("/api/reports/inventory", (HttpContext ctx) =>
{
    var u=ctx.Require("reports.profit");if(u.Error is not null)return u.Error;
    using var db=database.Open();
    return Results.Ok(Db.Query(db,"SELECT p.internal_id,p.name,c.name category,l.name location,b.on_hand,p.cost,p.selling_price,ROUND(b.on_hand*p.cost,2) cost_value,ROUND(b.on_hand*p.selling_price,2) retail_value,ROUND(CASE WHEN p.selling_price>0 THEN (p.selling_price-p.cost)/p.selling_price*100 ELSE 0 END,2) margin FROM inventory_balances b JOIN products p ON p.id=b.product_id JOIN categories c ON c.id=p.category_id JOIN locations l ON l.id=b.location_id ORDER BY c.name,p.name,l.name"));
});
app.MapGet("/api/audit", (HttpContext ctx) =>
{
    var u=ctx.Require("audit.view");if(u.Error is not null)return u.Error; using var db=database.Open();
    return Results.Ok(Db.Query(db,"SELECT a.*,u.display_name user FROM audit_events a JOIN users u ON u.id=a.user_id ORDER BY a.occurred_at DESC LIMIT 200"));
});

app.MapGet("/api/import/template", () => Results.Text("internal_id,barcode,name,category_code,description,package_size,unit_of_measure,supplier_id,cost,selling_price,tax_rate,quantity,location_code,reorder_level,desired_stock,reason\n,7440000000000,Producto ejemplo,SODA,,500 ml,unidad,,1000,1495,13,25,MAIN,5,20,Carga inicial", "text/csv", Encoding.UTF8));
app.MapPost("/api/import/preview", async (HttpRequest req,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("product.import");if(u.Error is not null)return u.Error;
    using var reader=new StreamReader(req.Body,Encoding.UTF8);var csv=await reader.ReadToEndAsync();return Results.Ok(service.PreviewInventoryImport(csv));
});
app.MapPost("/api/import/apply", (ImportApply r,HttpContext ctx,StoreService service) =>
{
    var u=ctx.Require("product.import");if(u.Error is not null)return u.Error;
    try{return Results.Ok(service.ApplyInventoryImport(r.Csv,u.User!));}catch(DomainException e){return Results.BadRequest(new{error=e.Message});}
});

app.MapFallbackToFile("index.html");
app.Run();

static string? ValidateCategory(CategoryEdit r)
{
    if(string.IsNullOrWhiteSpace(r.Name)||string.IsNullOrWhiteSpace(r.Code))return "Nombre y código son obligatorios.";
    if(!System.Text.RegularExpressions.Regex.IsMatch(r.Code.Trim(),"^[A-Za-z0-9-]{2,12}$"))return "El código debe tener de 2 a 12 letras, números o guiones.";
    if(r.MinimumMargin<0||r.MinimumMargin>=100||r.TargetMarkup<0)return "Revise el margen mínimo y el recargo objetivo.";
    if(string.IsNullOrWhiteSpace(r.Reason))return "Indique el motivo del cambio.";
    return null;
}
static string? CleanValue(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();

public record LoginRequest(string Username,string Password);
public record CategoryEdit(long DepartmentId,long? ParentId,string Name,string Code,decimal MinimumMargin,decimal TargetMarkup,bool Active,string Reason);
public record SupplierEdit(string Name,string? Email,string? Phone,bool Active,string Reason);
public record ProductCreate(string Name,string? InternalId,long CategoryId,string? Brand,string? Description,string UnitOfMeasure,string? PackageSize,decimal Cost,decimal SellingPrice,decimal TaxRate,decimal ReorderLevel,decimal DesiredStock,string? Barcode,string BarcodeType,long? SupplierId,string? SupplierSku,decimal OpeningStock,long LocationId,long? FamilyId);
public record ProductUpdate(string InternalId,string Name,string? Description,long CategoryId,string UnitOfMeasure,string? PackageSize,decimal Cost,decimal SellingPrice,decimal TaxRate,decimal ReorderLevel,decimal DesiredStock,string? Barcode,long? SupplierId,long? FamilyId,string Reason);
public record StatusRequest(bool Active,string Reason);
public record StockAdjustment(long ProductId,long LocationId,decimal NewQuantity,string Reason);
public record StockTransfer(long ProductId,long FromLocationId,long ToLocationId,decimal Quantity,string Reason);
public record SaleRequest(long LocationId,string? Reference,List<SaleLineRequest> Lines);
public record SaleLineRequest(long ProductId,decimal Quantity,decimal? UnitPrice);
public record BarcodeRequest(string Code,string Type,bool IsPrimary,bool ResolveConflict,string Reason);
public record PriceRecommendationRequest(long ProductId,decimal Freight=0,decimal Duties=0,decimal Handling=0,decimal Discount=0,decimal Other=0,int ComparableCount=5);
public record PriceDecision(string Decision,string Reason);
public record ImportApply(string Csv);
public record CurrentUser(long Id,string Username,string DisplayName,string Role);

public sealed class DomainException(string message,string? field=null):Exception(message){public string? Field{get;}=field;}

static class HttpExtensions
{
    public static CurrentUser UserRecord(this HttpContext c)=>(CurrentUser)c.Items["user"]!;
    public static (CurrentUser? User,IResult? Error) Require(this HttpContext c,string permission)
    {
        var u=c.UserRecord(); return Auth.Permissions(u.Role).Contains(permission)?(u,null):(null,Results.Json(new{error="No tiene permiso para esta acción"},statusCode:403));
    }
}

static class Auth
{
    private static readonly Dictionary<string,string[]> Grants=new()
    {
        ["administrator"]=["product.write","product.deactivate","product.import","inventory.adjust","price.view","price.approve","sale.record","barcode.resolve","reports.basic","reports.profit","audit.view","settings.write"],
        ["manager"]=["product.write","product.deactivate","product.import","inventory.adjust","price.view","price.approve","sale.record","barcode.resolve","reports.basic","reports.profit","audit.view"],
        ["inventory"]=["product.write","product.import","inventory.adjust","price.view","reports.basic"],
        ["purchasing"]=["product.write","price.view","reports.basic","reports.profit"],
        ["cashier"]=["price.view","sale.record","reports.basic"]
    };
    public static string[] Permissions(string role)=>Grants.GetValueOrDefault(role,[]);
    public static string TokenHash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static CurrentUser? CurrentUser(HttpContext ctx,StoreDb store)
    {
        if(!ctx.Request.Cookies.TryGetValue("primavera_session",out var token)||token.Length<32)return null;
        using var db=store.Open();using var cmd=Db.Cmd(db,"SELECT u.id,u.username,u.display_name,u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@t AND s.expires_at>@now AND u.active=1",("@t",TokenHash(token)),("@now",StoreDb.Now()));
        using var r=cmd.ExecuteReader();return r.Read()?new CurrentUser(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3)):null;
    }
}

static class Db
{
    public static SqliteCommand Cmd(SqliteConnection db,string sql,params (string,object?)[] args){var c=db.CreateCommand();c.CommandText=sql;foreach(var(a,v)in args)c.Parameters.AddWithValue(a,v??DBNull.Value);return c;}
    public static int Exec(SqliteConnection db,string sql,params (string,object?)[] args){using var c=Cmd(db,sql,args);return c.ExecuteNonQuery();}
    public static object? Scalar(SqliteConnection db,string sql,params (string,object?)[] args){using var c=Cmd(db,sql,args);return c.ExecuteScalar();}
    public static List<Dictionary<string,object?>> Query(SqliteConnection db,string sql,params (string,object?)[] args){using var c=Cmd(db,sql,args);using var r=c.ExecuteReader();var list=new List<Dictionary<string,object?>>();while(r.Read()){var row=new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);list.Add(row);}return list;}
    public static Dictionary<string,object?> One(SqliteConnection db,string sql,params (string,object?)[] args)=>Query(db,sql,args).FirstOrDefault()??new(StringComparer.OrdinalIgnoreCase);
    public static void Audit(SqliteConnection db,CurrentUser user,string action,string type,string id,object? oldValue,object? newValue,string? reason)=>Exec(db,"INSERT INTO audit_events(user_id,action,entity_type,entity_id,old_values,new_values,reason,occurred_at) VALUES(@u,@a,@t,@id,@old,@new,@r,@now)",("@u",user.Id),("@a",action),("@t",type),("@id",id),("@old",oldValue is null?null:JsonSerializer.Serialize(oldValue)),("@new",newValue is null?null:JsonSerializer.Serialize(newValue)),("@r",reason),("@now",StoreDb.Now()));
}

public partial class Program { }
