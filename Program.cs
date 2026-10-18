using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AlmacenPrimavera;
using AlmacenPrimavera.Data;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StoreDb>();
builder.Services.AddSingleton<StoreService>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 12 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 24 * 1024 * 1024);
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var database = app.Services.GetRequiredService<StoreDb>();
database.Initialize();
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self' 'wasm-unsafe-eval'; worker-src 'self'; connect-src 'self'; img-src 'self' data: blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    try {
        if (ctx.Request.Path.StartsWithSegments("/api")) {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!HttpMethods.IsGet(ctx.Request.Method) && origin.Length > 0 && origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}")
                throw new DomainException("Origen de la solicitud no permitido.", 403);
            if (ctx.Request.Path != "/api/auth/login")
                ctx.Items["user"] = Auth.CurrentUser(ctx, database) ?? throw new DomainException("Inicia sesión para continuar.", 401);
        }
        await next();
    }
    catch (DomainException e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (SqliteException e) when (e.SqliteErrorCode == 19) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "El código ya existe o el registro está en uso. Revisa los datos." }); }
    catch (BadHttpRequestException e) { ctx.Response.StatusCode = e.StatusCode; await ctx.Response.WriteAsJsonAsync(new { error = "La solicitud no es válida o supera el tamaño permitido." }); }
    catch (Exception e) { app.Logger.LogError(e, "Error en la solicitud"); ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "No se pudo completar la operación. No se guardaron cambios parciales." }); }
});
app.UseDefaultFiles();
var staticTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticTypes.Mappings[".gz"] = "application/gzip";
staticTypes.Mappings[".wasm"] = "application/wasm";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticTypes });
app.UseRateLimiter();

app.MapPost("/api/auth/login", (LoginRequest r, HttpContext ctx) => {
    using var db = database.Open();
    var row = Db.One(db, "SELECT * FROM users WHERE username=@u AND active=1", ("@u", r.Username?.Trim() ?? ""));
    if (row.Count == 0 || string.IsNullOrEmpty(r.Password) || !CryptographicOperations.FixedTimeEquals(
        Convert.FromBase64String((string)row["password_hash"]!), Convert.FromBase64String(StoreDb.HashPassword(r.Password, (string)row["password_salt"]!))))
        throw new DomainException("Usuario o contraseña incorrectos.", 401);
    if (ctx.Request.Cookies.TryGetValue("primavera_session", out var previous)) Db.Exec(db, "DELETE FROM sessions WHERE token_hash=@t", ("@t", Auth.TokenHash(previous)));
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    Db.Exec(db, "DELETE FROM sessions WHERE expires_at<@now; INSERT INTO sessions(token_hash,user_id,expires_at,created_at) VALUES(@t,@u,@exp,@now)",
        ("@t", Auth.TokenHash(token)), ("@u", row["id"]), ("@exp", DateTimeOffset.UtcNow.AddHours(12).ToString("O")), ("@now", StoreDb.Now()));
    ctx.Response.Cookies.Append("primavera_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromHours(12), Path = "/" });
    return Results.Ok(new CurrentUser((long)row["id"]!, (string)row["display_name"]!, (string)row["role"]!));
}).RequireRateLimiting("login");
app.MapGet("/api/auth/me", (HttpContext ctx) => Results.Ok(ctx.UserRecord()));
app.MapPost("/api/auth/logout", (HttpContext ctx) => {
    using var db = database.Open();
    Db.Exec(db, "DELETE FROM sessions WHERE token_hash=@t", ("@t", Auth.TokenHash(ctx.Request.Cookies["primavera_session"] ?? "")));
    ctx.Response.Cookies.Delete("primavera_session"); return Results.NoContent();
});
app.MapGet("/api/lookups", (StoreService s) => s.Lookups());
app.MapGet("/api/products", (string? q, long? categoryId, bool? pending, int? page, StoreService s) => s.Products(q, categoryId, pending ?? false, page ?? 1));
app.MapGet("/api/products/{id:long}", (long id, StoreService s) => s.Product(id));
app.MapPost("/api/products", (ProductEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveProduct(null, r); });
app.MapPut("/api/products/{id:long}", (long id, ProductEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveProduct(id, r); });
app.MapPost("/api/products/classify", (Classification r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.Classify(r); });
app.MapPost("/api/categories", (CategoryEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveCategory(null, r); });
app.MapPut("/api/categories/{id:long}", (long id, CategoryEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveCategory(id, r); });
app.MapPost("/api/subcategories", (SubcategoryEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveSubcategory(null, r); });
app.MapPut("/api/subcategories/{id:long}", (long id, SubcategoryEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveSubcategory(id, r); });
app.MapPost("/api/importers", (ImporterEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveImporter(null, r); });
app.MapPut("/api/importers/{id:long}", (long id, ImporterEdit r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.SaveImporter(id, r); });
app.MapPost("/api/excel/read", async (HttpRequest request) => {
    if (!request.HasFormContentType) throw new DomainException("Selecciona un archivo Excel (.xlsx) o CSV.");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new DomainException("Selecciona un archivo.");
    if (file.Length > 10 * 1024 * 1024 || file.Length == 0) throw new DomainException("El archivo debe tener contenido y pesar como máximo 10 MB.");
    using var stream = file.OpenReadStream();
    return ExcelFiles.Read(stream, file.FileName);
});
app.MapPost("/api/excel/merge", (MergeRequest r) => ExcelFiles.Merge(r));
app.MapPost("/api/excel/export", (ExportRequest r) => { ExcelFiles.ValidateRows(r.Rows); return Results.File(ExcelFiles.Export(r.Rows), ExcelFiles.Mime, "productos-fusionados.xlsx"); });
app.MapPost("/api/ocr/export", (OcrExportRequest r) => Results.File(OcrExport.Create(r), ExcelFiles.Mime, "productos-desde-imagen.xlsx"));
app.MapPost("/api/excel/apply", (ImportRequest r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.Import(r); });
app.MapGet("/api/products/export", (StoreService s) => Results.File(ExcelFiles.Export(s.ExportRows()), ExcelFiles.Mime, "catalogo-primavera.xlsx"));
app.MapPost("/api/pricing/recommend", (PriceRequest r, StoreService s) => s.Recommend(r));
app.MapPost("/api/pricing/apply", (PriceApply r, HttpContext c, StoreService s) => { c.RequireEdit(); return s.ApplyPrice(r); });
app.Map("/api/{**path}", () => Results.NotFound(new { error = "Esta función ya no forma parte de la app." }));
app.MapFallbackToFile("index.html");
app.Run();

public record LoginRequest(string Username, string Password);
public record CurrentUser(long Id, string DisplayName, string Role) { public bool CanEdit => Role is "administrator" or "manager"; }
public sealed class DomainException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
static class HttpExtensions {
    public static CurrentUser UserRecord(this HttpContext c) => (CurrentUser)c.Items["user"]!;
    public static void RequireEdit(this HttpContext c) { if (!c.UserRecord().CanEdit) throw new DomainException("Solo administradores y gerentes pueden guardar cambios.", 403); }
}
static class Auth {
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static CurrentUser? CurrentUser(HttpContext ctx, StoreDb store) {
        if (!ctx.Request.Cookies.TryGetValue("primavera_session", out var token) || token.Length != 64) return null;
        using var db = store.Open();
        var r = Db.One(db, "SELECT u.id,u.display_name,u.role FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@t AND s.expires_at>@now AND u.active=1", ("@t", TokenHash(token)), ("@now", StoreDb.Now()));
        return r.Count == 0 ? null : new((long)r["id"]!, (string)r["display_name"]!, (string)r["role"]!);
    }
}
public static class Db {
    public static SqliteCommand Cmd(SqliteConnection db, string sql, params (string, object?)[] args) { var c = db.CreateCommand(); c.CommandText = sql; foreach (var (a,v) in args) c.Parameters.AddWithValue(a,v ?? DBNull.Value); return c; }
    public static int Exec(SqliteConnection db, string sql, params (string, object?)[] args) { using var c = Cmd(db,sql,args); return c.ExecuteNonQuery(); }
    public static object? Scalar(SqliteConnection db, string sql, params (string, object?)[] args) { using var c = Cmd(db,sql,args); return c.ExecuteScalar(); }
    public static List<Dictionary<string,object?>> Query(SqliteConnection db, string sql, params (string,object?)[] args) { using var c = Cmd(db,sql,args); using var r = c.ExecuteReader(); var list = new List<Dictionary<string,object?>>(); while(r.Read()) { var row = new Dictionary<string,object?>(); for(var i=0;i<r.FieldCount;i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); list.Add(row); } return list; }
    public static Dictionary<string,object?> One(SqliteConnection db, string sql, params (string,object?)[] args) => Query(db,sql,args).FirstOrDefault() ?? new();
}
public partial class Program { }
