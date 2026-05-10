using Microsoft.Data.Sqlite;
using System.Text;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5000");
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o => { o.IdleTimeout = TimeSpan.FromHours(8); o.Cookie.HttpOnly = true; });
var app = builder.Build();
app.UseSession();

string dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "inventory.db";
string uploadDir = Environment.GetEnvironmentVariable("UPLOAD_DIR") ?? "wwwroot/uploads";
Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
Directory.CreateDirectory(uploadDir);

void InitDb()
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Username TEXT NOT NULL UNIQUE,
            PasswordHash TEXT NOT NULL,
            IsAdmin INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS Groups (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL UNIQUE,
            SortOrder INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS Parts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PartName TEXT NOT NULL DEFAULT '',
            BrandName TEXT NOT NULL,
            Model TEXT NOT NULL DEFAULT '',
            PartNumber TEXT NOT NULL DEFAULT '',
            GroupId INTEGER DEFAULT NULL,
            Quantity INTEGER NOT NULL,
            Place TEXT NOT NULL,
            IsAvailable INTEGER NOT NULL DEFAULT 1,
            IsReserved INTEGER NOT NULL DEFAULT 0,
            IsUsed INTEGER NOT NULL DEFAULT 0,
            ReservedNote TEXT NOT NULL DEFAULT '',
            Notes TEXT NOT NULL DEFAULT '',
            ImageFile TEXT NOT NULL DEFAULT '',
            ParentId INTEGER DEFAULT NULL,
            CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS History (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PartId INTEGER NOT NULL,
            Action TEXT NOT NULL,
            Detail TEXT NOT NULL DEFAULT '',
            Username TEXT NOT NULL DEFAULT '',
            ChangedAt TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS Cart (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PartId INTEGER NOT NULL,
            Username TEXT NOT NULL,
            CartQty INTEGER NOT NULL DEFAULT 1,
            AddedAt TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(PartId, Username)
        );
        CREATE TABLE IF NOT EXISTS DeletedParts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            OriginalPartId INTEGER NOT NULL,
            PartName TEXT NOT NULL DEFAULT '',
            BrandName TEXT NOT NULL,
            Model TEXT NOT NULL DEFAULT '',
            PartNumber TEXT NOT NULL DEFAULT '',
            GroupName TEXT NOT NULL DEFAULT '',
            Quantity INTEGER NOT NULL,
            Place TEXT NOT NULL,
            PurchaseOrder TEXT NOT NULL DEFAULT '',
            DeletedBy TEXT NOT NULL DEFAULT '',
            DeletedAt TEXT NOT NULL DEFAULT (datetime('now'))
        );";
    cmd.ExecuteNonQuery();

    foreach (var col in new[] {
        "ALTER TABLE Parts ADD COLUMN PartName TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN Model TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN GroupId INTEGER DEFAULT NULL",
        "ALTER TABLE Parts ADD COLUMN IsAvailable INTEGER NOT NULL DEFAULT 1",
        "ALTER TABLE Parts ADD COLUMN IsReserved INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Parts ADD COLUMN IsUsed INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE Parts ADD COLUMN ReservedNote TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN Notes TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN PartNumber TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN ImageFile TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Parts ADD COLUMN ParentId INTEGER DEFAULT NULL",
        "ALTER TABLE Parts ADD COLUMN CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))",
        "ALTER TABLE History ADD COLUMN Username TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Cart ADD COLUMN CartQty INTEGER NOT NULL DEFAULT 1",
    })
    { try { cmd.CommandText = col; cmd.ExecuteNonQuery(); } catch { } }

    // Seed default groups
    var gc = conn.CreateCommand();
    gc.CommandText = "SELECT COUNT(*) FROM Groups";
    if ((long)(gc.ExecuteScalar() ?? 0L) == 0)
    {
        var groups = new[] { "Bumper","Bumper bracket","Bumper part","Door","Door part","Interior","Wire harness","Parking sensor","Tire","Mag","Metal body","Metal bracket" };
        for (int i = 0; i < groups.Length; i++)
        {
            var ig = conn.CreateCommand();
            ig.CommandText = "INSERT OR IGNORE INTO Groups (Name,SortOrder) VALUES (@n,@s)";
            ig.Parameters.AddWithValue("@n", groups[i]);
            ig.Parameters.AddWithValue("@s", i);
            ig.ExecuteNonQuery();
        }
    }

    // Default admin
    var chk = conn.CreateCommand();
    chk.CommandText = "SELECT COUNT(*) FROM Users";
    if ((long)(chk.ExecuteScalar() ?? 0L) == 0)
    {
        var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO Users (Username,PasswordHash,IsAdmin) VALUES ('admin',@h,1)";
        ins.Parameters.AddWithValue("@h", HashPassword("admin123"));
        ins.ExecuteNonQuery();
    }
}

InitDb();

string HashPassword(string p)
{
    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(p + "fixauto_salt"))).ToLower();
}

void LogHistory(SqliteConnection conn, int partId, string action, string detail, string username)
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO History (PartId,Action,Detail,Username) VALUES (@pid,@a,@d,@u)";
    cmd.Parameters.AddWithValue("@pid", partId);
    cmd.Parameters.AddWithValue("@a", action);
    cmd.Parameters.AddWithValue("@d", detail);
    cmd.Parameters.AddWithValue("@u", username);
    cmd.ExecuteNonQuery();
}

long GetLastId(SqliteConnection conn) {
    var c = conn.CreateCommand();
    c.CommandText = "SELECT last_insert_rowid()";
    return (long)(c.ExecuteScalar() ?? 0L);
}

string? GetUser(HttpContext ctx) => ctx.Session.GetString("username");
bool IsAdmin(HttpContext ctx) => ctx.Session.GetString("isAdmin") == "1";

List<Part> QueryParts(string? search=null, string? field=null, string? filter=null, string? brand=null, int? groupId=null)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    var cmd = conn.CreateCommand();
    var where = new List<string>();
    if (!string.IsNullOrWhiteSpace(search) && !string.IsNullOrWhiteSpace(field))
    {
        string col = field switch { "PartNumber"=>"p.PartNumber","PartName"=>"p.PartName","Model"=>"p.Model",_=>"p.BrandName" };
        where.Add($"{col} LIKE @s");
        cmd.Parameters.AddWithValue("@s", $"%{search}%");
    }
    if (filter == "available") where.Add("p.IsAvailable=1");
    if (filter == "reserved")  where.Add("p.IsReserved=1");
    if (filter == "used")      where.Add("p.IsUsed=1");
    if (!string.IsNullOrWhiteSpace(brand)) { where.Add("p.BrandName=@brand"); cmd.Parameters.AddWithValue("@brand", brand); }
    if (groupId.HasValue) { where.Add("p.GroupId=@gid"); cmd.Parameters.AddWithValue("@gid", groupId.Value); }
    string w = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    cmd.CommandText = $@"SELECT p.Id,p.PartName,p.BrandName,p.Model,p.PartNumber,p.Quantity,p.Place,
        p.IsAvailable,p.IsReserved,p.IsUsed,p.ReservedNote,p.Notes,p.ParentId,p.CreatedAt,
        p.ImageFile,p.GroupId,COALESCE(g.Name,'') as GroupName
        FROM Parts p LEFT JOIN Groups g ON p.GroupId=g.Id
        {w} ORDER BY p.PartNumber, p.ParentId NULLS FIRST, p.Id";
    var list = new List<Part>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        list.Add(new Part(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),
                          r.GetInt32(5),r.GetString(6),r.GetInt32(7)==1,r.GetInt32(8)==1,r.GetInt32(9)==1,
                          r.GetString(10),r.GetString(11),r.IsDBNull(12)?null:r.GetInt32(12),r.GetString(13),
                          r.GetString(14),r.IsDBNull(15)?null:r.GetInt32(15),r.GetString(16)));
    return list;
}

// ── Auth ──────────────────────────────────────────────────────
app.MapPost("/api/login", async (HttpContext ctx) => {
    var b = await ctx.Request.ReadFromJsonAsync<LoginInput>(); if(b==null) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id,Username,IsAdmin FROM Users WHERE Username=@u AND PasswordHash=@p";
    cmd.Parameters.AddWithValue("@u", b.Username.Trim().ToLower());
    cmd.Parameters.AddWithValue("@p", HashPassword(b.Password));
    using var r = cmd.ExecuteReader(); if(!r.Read()) return Results.Unauthorized();
    ctx.Session.SetString("username", r.GetString(1));
    ctx.Session.SetString("isAdmin", r.GetInt32(2)==1?"1":"0");
    return Results.Ok(new{username=r.GetString(1),isAdmin=r.GetInt32(2)==1});
});
app.MapPost("/api/logout",(HttpContext ctx)=>{ctx.Session.Clear();return Results.Ok();});
app.MapGet("/api/me",(HttpContext ctx)=>{var u=GetUser(ctx);if(u==null)return Results.Unauthorized();return Results.Ok(new{username=u,isAdmin=IsAdmin(ctx)});});

// ── Groups ────────────────────────────────────────────────────
app.MapGet("/api/groups", (HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id,Name,SortOrder FROM Groups ORDER BY SortOrder,Name";
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while(r.Read()) list.Add(new{id=r.GetInt32(0),name=r.GetString(1),sortOrder=r.GetInt32(2)});
    return Results.Ok(list);
});
app.MapPost("/api/groups", async (HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    if(!IsAdmin(ctx)) return Results.Forbid();
    var b = await ctx.Request.ReadFromJsonAsync<GroupInput>(); if(b==null||string.IsNullOrWhiteSpace(b.Name)) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    try {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Groups (Name,SortOrder) VALUES (@n,(SELECT COALESCE(MAX(SortOrder),0)+1 FROM Groups))";
        cmd.Parameters.AddWithValue("@n", b.Name.Trim()); cmd.ExecuteNonQuery();
        return Results.Ok();
    } catch { return Results.Conflict("Group already exists."); }
});
app.MapPut("/api/groups/{id:int}", async (int id, HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    if(!IsAdmin(ctx)) return Results.Forbid();
    var b = await ctx.Request.ReadFromJsonAsync<GroupInput>(); if(b==null||string.IsNullOrWhiteSpace(b.Name)) return Results.BadRequest();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Groups SET Name=@n WHERE Id=@id";
    cmd.Parameters.AddWithValue("@n", b.Name.Trim()); cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
    return Results.Ok();
});
app.MapDelete("/api/groups/{id:int}", (int id, HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    if(!IsAdmin(ctx)) return Results.Forbid();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    // Unlink parts from this group
    var uc = conn.CreateCommand(); uc.CommandText = "UPDATE Parts SET GroupId=NULL WHERE GroupId=@id"; uc.Parameters.AddWithValue("@id",id); uc.ExecuteNonQuery();
    var cmd = conn.CreateCommand(); cmd.CommandText = "DELETE FROM Groups WHERE Id=@id"; cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    return Results.Ok();
});

// ── Brand+Group summary for main page ────────────────────────
app.MapGet("/api/brands/summary", (HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    using var conn = new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT p.BrandName, COALESCE(g.Name,'Uncategorized') as GroupName, g.Id as GroupId, COUNT(*) as Cnt
        FROM Parts p LEFT JOIN Groups g ON p.GroupId=g.Id
        GROUP BY p.BrandName, g.Id ORDER BY p.BrandName, g.Name";
    var result = new Dictionary<string, List<object>>();
    using var r = cmd.ExecuteReader();
    while(r.Read()) {
        string brand = r.GetString(0);
        if(!result.ContainsKey(brand)) result[brand] = new();
        result[brand].Add(new{groupName=r.GetString(1),groupId=r.IsDBNull(2)?null:(int?)r.GetInt32(2),count=r.GetInt32(3)});
    }
    return Results.Ok(result);
});

// ── Users ─────────────────────────────────────────────────────
app.MapGet("/api/users",(HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized(); if(!IsAdmin(ctx)) return Results.Forbid();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand(); cmd.CommandText="SELECT Id,Username,IsAdmin,CreatedAt FROM Users ORDER BY Username";
    var list=new List<object>(); using var r=cmd.ExecuteReader();
    while(r.Read()) list.Add(new{id=r.GetInt32(0),username=r.GetString(1),isAdmin=r.GetInt32(2)==1,createdAt=r.GetString(3)});
    return Results.Ok(list);
});
app.MapPost("/api/users", async (HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized(); if(!IsAdmin(ctx)) return Results.Forbid();
    var b=await ctx.Request.ReadFromJsonAsync<NewUserInput>();
    if(b==null||string.IsNullOrWhiteSpace(b.Username)||string.IsNullOrWhiteSpace(b.Password)) return Results.BadRequest("Username and password required.");
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    try{ var cmd=conn.CreateCommand(); cmd.CommandText="INSERT INTO Users (Username,PasswordHash,IsAdmin) VALUES (@u,@p,@a)";
        cmd.Parameters.AddWithValue("@u",b.Username.Trim().ToLower()); cmd.Parameters.AddWithValue("@p",HashPassword(b.Password)); cmd.Parameters.AddWithValue("@a",b.IsAdmin?1:0);
        cmd.ExecuteNonQuery(); return Results.Ok();
    } catch { return Results.Conflict("Username already exists."); }
});
app.MapDelete("/api/users/{id:int}",(int id,HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized(); if(!IsAdmin(ctx)) return Results.Forbid();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand(); cmd.CommandText="DELETE FROM Users WHERE Id=@id AND IsAdmin=0"; cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    return Results.Ok();
});
app.MapPost("/api/users/{id:int}/password", async (int id,HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized(); if(!IsAdmin(ctx)) return Results.Forbid();
    var b=await ctx.Request.ReadFromJsonAsync<PasswordInput>(); if(b==null||string.IsNullOrWhiteSpace(b.Password)) return Results.BadRequest();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand(); cmd.CommandText="UPDATE Users SET PasswordHash=@p WHERE Id=@id";
    cmd.Parameters.AddWithValue("@p",HashPassword(b.Password)); cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    return Results.Ok();
});

// ── Image upload ──────────────────────────────────────────────
app.MapPost("/api/upload-image", async (HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    if(!ctx.Request.HasFormContentType) return Results.BadRequest();
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("image");
    if(file==null||file.Length==0) return Results.BadRequest("No file.");
    var ext = Path.GetExtension(file.FileName).ToLower();
    if(ext!=".jpg"&&ext!=".jpeg"&&ext!=".png"&&ext!=".webp") return Results.BadRequest("Only jpg/png/webp allowed.");
    var fileName = Guid.NewGuid().ToString("N")+ext;
    var path = Path.Combine(uploadDir, fileName);
    using var stream = File.Create(path);
    await file.CopyToAsync(stream);
    return Results.Ok(new{fileName});
});

// ── Check duplicate spare ─────────────────────────────────────
app.MapGet("/api/parts/check-spare/{spare}", (string spare, HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="SELECT Id,PartName,BrandName,Model,PartNumber,Quantity,Place FROM Parts WHERE LOWER(PartNumber)=LOWER(@s) AND ParentId IS NULL LIMIT 1";
    cmd.Parameters.AddWithValue("@s",spare);
    using var r=cmd.ExecuteReader(); if(!r.Read()) return Results.Ok(new{exists=false});
    return Results.Ok(new{exists=true,id=r.GetInt32(0),partName=r.GetString(1),brandName=r.GetString(2),model=r.GetString(3),partNumber=r.GetString(4),quantity=r.GetInt32(5),place=r.GetString(6)});
});

// ── Add quantity to existing ──────────────────────────────────
app.MapPost("/api/parts/{id:int}/add-quantity", async (int id, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var b=await ctx.Request.ReadFromJsonAsync<AddQtyInput>(); if(b==null||b.Quantity<=0) return Results.BadRequest();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand(); cmd.CommandText="UPDATE Parts SET Quantity=Quantity+@q WHERE Id=@id";
    cmd.Parameters.AddWithValue("@q",b.Quantity); cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    var qc=conn.CreateCommand(); qc.CommandText="SELECT BrandName,Model,PartName,PartNumber,Quantity FROM Parts WHERE Id=@id"; qc.Parameters.AddWithValue("@id",id);
    using var r=qc.ExecuteReader(); if(r.Read()) LogHistory(conn,id,"Added Qty",$"{r.GetString(0)} {r.GetString(1)} — {r.GetString(2)} (Part#:{r.GetString(3)}) +{b.Quantity} → total {r.GetInt32(4)}",user);
    return Results.Ok();
});

// ── Reserve-split ─────────────────────────────────────────────
app.MapPost("/api/parts/{id:int}/reserve-split", async (int id, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var b=await ctx.Request.ReadFromJsonAsync<ReserveSplitInput>(); if(b==null||b.ReserveQty<=0) return Results.BadRequest();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var gc=conn.CreateCommand();
    gc.CommandText="SELECT Id,PartName,BrandName,Model,PartNumber,Quantity,Place,Notes,GroupId,ImageFile FROM Parts WHERE Id=@id";
    gc.Parameters.AddWithValue("@id",id);
    using var r=gc.ExecuteReader(); if(!r.Read()) return Results.NotFound();
    int origQty=r.GetInt32(5);
    if(b.ReserveQty>=origQty) return Results.BadRequest("Reserve qty must be less than total.");
    string partName=r.GetString(1),brand=r.GetString(2),model=r.GetString(3),spare=r.GetString(4),place=r.GetString(6),notes=r.GetString(7),img=r.GetString(9);
    int? gid=r.IsDBNull(8)?null:r.GetInt32(8);
    r.Close();
    var uc=conn.CreateCommand(); uc.CommandText="UPDATE Parts SET Quantity=@q WHERE Id=@id"; uc.Parameters.AddWithValue("@q",origQty-b.ReserveQty); uc.Parameters.AddWithValue("@id",id); uc.ExecuteNonQuery();
    var ic=conn.CreateCommand();
    ic.CommandText="INSERT INTO Parts (PartName,BrandName,Model,PartNumber,GroupId,Quantity,Place,IsAvailable,IsReserved,IsUsed,ReservedNote,Notes,ImageFile,ParentId) VALUES (@pn,@b,@m,@s,@gid,@q,@p,0,1,0,@rn,@n,@img,@pid)";
    ic.Parameters.AddWithValue("@pn",partName); ic.Parameters.AddWithValue("@b",brand); ic.Parameters.AddWithValue("@m",model);
    ic.Parameters.AddWithValue("@s",spare); ic.Parameters.AddWithValue("@gid",(object?)gid??DBNull.Value);
    ic.Parameters.AddWithValue("@q",b.ReserveQty); ic.Parameters.AddWithValue("@p",place);
    ic.Parameters.AddWithValue("@rn",b.ReservedNote?.Trim()??""); ic.Parameters.AddWithValue("@n",notes);
    ic.Parameters.AddWithValue("@img",img); ic.Parameters.AddWithValue("@pid",id);
    ic.ExecuteNonQuery();
    int newId=(int)GetLastId(conn);
    LogHistory(conn,id,"Split & Reserved",$"{brand} {model} — {partName}: reserved {b.ReserveQty} for \"{b.ReservedNote}\", {origQty-b.ReserveQty} remaining",user);
    LogHistory(conn,newId,"Created (Split)",$"Split from #{id} — {brand} {model} — {partName} reserved for \"{b.ReservedNote}\"",user);
    return Results.Ok();
});

// ── Parts CRUD ────────────────────────────────────────────────
app.MapGet("/api/parts",(HttpContext ctx,string? search,string? field,string? filter,string? brand,int? groupId)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized();
    return Results.Ok(QueryParts(search,field,filter,brand,groupId));
});

app.MapPost("/api/parts", async (HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var p=await ctx.Request.ReadFromJsonAsync<PartInput>();
    if(p==null||string.IsNullOrWhiteSpace(p.BrandName)||string.IsNullOrWhiteSpace(p.PartNumber)||string.IsNullOrWhiteSpace(p.Place)) return Results.BadRequest();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="INSERT INTO Parts (PartName,BrandName,Model,PartNumber,GroupId,Quantity,Place,IsAvailable,IsReserved,IsUsed,ReservedNote,Notes,ImageFile) VALUES (@pn,@b,@m,@s,@gid,@q,@p,@a,@r,@u,@rn,@n,@img)";
    cmd.Parameters.AddWithValue("@pn",p.PartName?.Trim()??""); cmd.Parameters.AddWithValue("@b",p.BrandName.Trim());
    cmd.Parameters.AddWithValue("@m",p.Model?.Trim()??""); cmd.Parameters.AddWithValue("@s",p.PartNumber.Trim());
    cmd.Parameters.AddWithValue("@gid",(object?)p.GroupId??DBNull.Value);
    cmd.Parameters.AddWithValue("@q",p.Quantity); cmd.Parameters.AddWithValue("@p",p.Place.Trim());
    cmd.Parameters.AddWithValue("@a",p.IsAvailable?1:0); cmd.Parameters.AddWithValue("@r",p.IsReserved?1:0);
    cmd.Parameters.AddWithValue("@u",p.IsUsed?1:0); cmd.Parameters.AddWithValue("@rn",p.ReservedNote?.Trim()??"");
    cmd.Parameters.AddWithValue("@n",p.Notes?.Trim()??""); cmd.Parameters.AddWithValue("@img",p.ImageFile?.Trim()??"");
    cmd.ExecuteNonQuery();
    LogHistory(conn,(int)GetLastId(conn),"Added",$"{p.BrandName} {p.Model} — {p.PartName} (Part#:{p.PartNumber}, Qty:{p.Quantity})",user);
    return Results.Ok();
});

app.MapPut("/api/parts/{id:int}", async (int id, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var p=await ctx.Request.ReadFromJsonAsync<PartInput>();
    if(p==null||string.IsNullOrWhiteSpace(p.BrandName)||string.IsNullOrWhiteSpace(p.PartNumber)||string.IsNullOrWhiteSpace(p.Place)) return Results.BadRequest();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="UPDATE Parts SET PartName=@pn,BrandName=@b,Model=@m,PartNumber=@s,GroupId=@gid,Quantity=@q,Place=@p,IsAvailable=@a,IsReserved=@r,IsUsed=@u,ReservedNote=@rn,Notes=@n,ImageFile=@img WHERE Id=@id";
    cmd.Parameters.AddWithValue("@pn",p.PartName?.Trim()??""); cmd.Parameters.AddWithValue("@b",p.BrandName.Trim());
    cmd.Parameters.AddWithValue("@m",p.Model?.Trim()??""); cmd.Parameters.AddWithValue("@s",p.PartNumber.Trim());
    cmd.Parameters.AddWithValue("@gid",(object?)p.GroupId??DBNull.Value);
    cmd.Parameters.AddWithValue("@q",p.Quantity); cmd.Parameters.AddWithValue("@p",p.Place.Trim());
    cmd.Parameters.AddWithValue("@a",p.IsAvailable?1:0); cmd.Parameters.AddWithValue("@r",p.IsReserved?1:0);
    cmd.Parameters.AddWithValue("@u",p.IsUsed?1:0); cmd.Parameters.AddWithValue("@rn",p.ReservedNote?.Trim()??"");
    cmd.Parameters.AddWithValue("@n",p.Notes?.Trim()??""); cmd.Parameters.AddWithValue("@img",p.ImageFile?.Trim()??"");
    cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    LogHistory(conn,id,"Edited",$"{p.BrandName} {p.Model} — {p.PartName} | Qty:{p.Quantity}",user);
    return Results.Ok();
});

app.MapDelete("/api/parts/{id:int}", async (int id, HttpContext ctx)=>{
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var body = await ctx.Request.ReadFromJsonAsync<DeleteInput>();
    if(body==null||string.IsNullOrWhiteSpace(body.PurchaseOrder))
        return Results.BadRequest("Purchase Order (PO) is required to delete a part.");
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var qc=conn.CreateCommand();
    qc.CommandText=@"SELECT p.BrandName,p.Model,p.PartName,p.PartNumber,p.ImageFile,p.Quantity,p.Place,COALESCE(g.Name,'') 
        FROM Parts p LEFT JOIN Groups g ON p.GroupId=g.Id WHERE p.Id=@id";
    qc.Parameters.AddWithValue("@id",id);
    string detail="",imgFile="",brandName="",model="",partName="",spareNumber="",place="",groupName="";
    int quantity=0;
    using(var r=qc.ExecuteReader()){
        if(r.Read()){
            brandName=r.GetString(0);model=r.GetString(1);partName=r.GetString(2);
            spareNumber=r.GetString(3);imgFile=r.GetString(4);quantity=r.GetInt32(5);
            place=r.GetString(6);groupName=r.GetString(7);
            detail=$"{brandName} {model} — {partName} (Part#:{spareNumber}, Qty:{quantity}, PO:{body.PurchaseOrder})";
        }
    }
    // Log to DeletedParts
    var dc=conn.CreateCommand();
    dc.CommandText="INSERT INTO DeletedParts (OriginalPartId,PartName,BrandName,Model,PartNumber,GroupName,Quantity,Place,PurchaseOrder,DeletedBy) VALUES (@oid,@pn,@b,@m,@s,@gn,@q,@p,@po,@u)";
    dc.Parameters.AddWithValue("@oid",id); dc.Parameters.AddWithValue("@pn",partName);
    dc.Parameters.AddWithValue("@b",brandName); dc.Parameters.AddWithValue("@m",model);
    dc.Parameters.AddWithValue("@s",spareNumber); dc.Parameters.AddWithValue("@gn",groupName);
    dc.Parameters.AddWithValue("@q",quantity); dc.Parameters.AddWithValue("@p",place);
    dc.Parameters.AddWithValue("@po",body.PurchaseOrder.Trim()); dc.Parameters.AddWithValue("@u",user);
    dc.ExecuteNonQuery();
    // Remove from carts
    var rc=conn.CreateCommand(); rc.CommandText="DELETE FROM Cart WHERE PartId=@id"; rc.Parameters.AddWithValue("@id",id); rc.ExecuteNonQuery();
    if(!string.IsNullOrWhiteSpace(imgFile)){try{File.Delete(Path.Combine(uploadDir,imgFile));}catch{}}
    var cmd=conn.CreateCommand(); cmd.CommandText="DELETE FROM Parts WHERE Id=@id"; cmd.Parameters.AddWithValue("@id",id); cmd.ExecuteNonQuery();
    LogHistory(conn,id,"Deleted",$"{detail}",user);
    return Results.Ok();
});

// ── History ───────────────────────────────────────────────────
app.MapGet("/api/history",(HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand(); cmd.CommandText="SELECT Id,PartId,Action,Detail,Username,ChangedAt FROM History ORDER BY ChangedAt DESC LIMIT 300";
    var list=new List<HistoryEntry>(); using var r=cmd.ExecuteReader();
    while(r.Read()) list.Add(new HistoryEntry(r.GetInt32(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5)));
    return Results.Ok(list);
});

// ── Export CSV ────────────────────────────────────────────────
app.MapGet("/api/export/csv",(HttpContext ctx)=>{
    if(GetUser(ctx)==null) return Results.Unauthorized();
    var parts=QueryParts();
    var sb=new StringBuilder();
    sb.AppendLine("ID,Part Name,Brand,Model,Group,Spare Number,Quantity,Location,Available,Reserved,Reserved Note,Used,Notes,Created At");
    foreach(var p in parts)
        sb.AppendLine($"{p.Id},\"{p.PartName}\",\"{p.BrandName}\",\"{p.Model}\",\"{p.GroupName}\",\"{p.PartNumber}\",{p.Quantity},\"{p.Place}\",{(p.IsAvailable?"Yes":"No")},{(p.IsReserved?"Yes":"No")},\"{p.ReservedNote}\",{(p.IsUsed?"Yes":"No")},\"{p.Notes}\",\"{p.CreatedAt}\"");
    return Results.Content(sb.ToString(),"text/csv",Encoding.UTF8);
});

// ── Remove quantity (partial delete with PO) ─────────────────
app.MapPost("/api/parts/{id:int}/remove-qty", async (int id, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var body=await ctx.Request.ReadFromJsonAsync<RemoveQtyInput>();
    if(body==null||body.Quantity<=0||string.IsNullOrWhiteSpace(body.PurchaseOrder))
        return Results.BadRequest("Quantity and PO are required.");
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    // Get current part info
    var qc=conn.CreateCommand();
    qc.CommandText=@"SELECT p.BrandName,p.Model,p.PartName,p.PartNumber,p.Quantity,p.Place,COALESCE(g.Name,'')
        FROM Parts p LEFT JOIN Groups g ON p.GroupId=g.Id WHERE p.Id=@id";
    qc.Parameters.AddWithValue("@id",id);
    string brandName="",model="",partName="",partNumber="",place="",groupName=""; int currentQty=0;
    using(var r=qc.ExecuteReader()){
        if(!r.Read()) return Results.NotFound();
        brandName=r.GetString(0);model=r.GetString(1);partName=r.GetString(2);
        partNumber=r.GetString(3);currentQty=r.GetInt32(4);place=r.GetString(5);groupName=r.GetString(6);
    }
    if(body.Quantity>currentQty) return Results.BadRequest($"Cannot remove {body.Quantity}, only {currentQty} in stock.");
    // Log to DeletedParts
    var dc=conn.CreateCommand();
    dc.CommandText="INSERT INTO DeletedParts (OriginalPartId,PartName,BrandName,Model,PartNumber,GroupName,Quantity,Place,PurchaseOrder,DeletedBy) VALUES (@oid,@pn,@b,@m,@s,@gn,@q,@p,@po,@u)";
    dc.Parameters.AddWithValue("@oid",id); dc.Parameters.AddWithValue("@pn",partName);
    dc.Parameters.AddWithValue("@b",brandName); dc.Parameters.AddWithValue("@m",model);
    dc.Parameters.AddWithValue("@s",partNumber); dc.Parameters.AddWithValue("@gn",groupName);
    dc.Parameters.AddWithValue("@q",body.Quantity); dc.Parameters.AddWithValue("@p",place);
    dc.Parameters.AddWithValue("@po",body.PurchaseOrder.Trim()); dc.Parameters.AddWithValue("@u",user);
    dc.ExecuteNonQuery();
    // Reduce quantity
    var uc=conn.CreateCommand();
    uc.CommandText="UPDATE Parts SET Quantity=Quantity-@q WHERE Id=@id";
    uc.Parameters.AddWithValue("@q",body.Quantity); uc.Parameters.AddWithValue("@id",id); uc.ExecuteNonQuery();
    LogHistory(conn,id,"Removed Qty",$"{brandName} {model} — {partName} (Part#:{partNumber}): removed {body.Quantity} (PO:{body.PurchaseOrder}), {currentQty-body.Quantity} remaining",user);
    return Results.Ok();
});

// ── Cart routes ───────────────────────────────────────────────
app.MapGet("/api/cart", (HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    try {
        using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
        var cmd=conn.CreateCommand();
        // Ensure CartQty column exists before querying
    try { var mc2=conn.CreateCommand(); mc2.CommandText="ALTER TABLE Cart ADD COLUMN CartQty INTEGER NOT NULL DEFAULT 1"; mc2.ExecuteNonQuery(); } catch {}
    cmd.CommandText=@"SELECT c.Id,c.PartId,
            COALESCE(p.PartName,'') as PartName,
            COALESCE(p.BrandName,'') as BrandName,
            COALESCE(p.Model,'') as Model,
            COALESCE(p.PartNumber,'') as PartNumber,
            COALESCE(p.Quantity,0) as Quantity,
            COALESCE(p.Place,'') as Place,
            COALESCE(g.Name,'') as GroupName,
            COALESCE(p.ImageFile,'') as ImageFile,
            c.AddedAt,
            COALESCE(c.CartQty,1) as CartQty
            FROM Cart c
            JOIN Parts p ON c.PartId=p.Id
            LEFT JOIN Groups g ON p.GroupId=g.Id
            WHERE c.Username=@u ORDER BY c.AddedAt DESC";
        cmd.Parameters.AddWithValue("@u",user);
        var list=new List<object>();
        using var r=cmd.ExecuteReader();
        while(r.Read()) list.Add(new{
            cartId=r.GetInt32(0),partId=r.GetInt32(1),
            partName=r.GetString(2),brandName=r.GetString(3),
            model=r.GetString(4),partNumber=r.GetString(5),
            quantity=r.GetInt32(6),place=r.GetString(7),
            groupName=r.GetString(8),imageFile=r.GetString(9),
            addedAt=r.GetString(10),cartQty=r.GetInt32(11)});
        return Results.Ok(list);
    } catch(Exception ex) {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/cart/{partId:int}", async (int partId, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    try {
        var body=await ctx.Request.ReadFromJsonAsync<CartAddInput>();
        int qty=body?.Quantity>0?body.Quantity:1;
        using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
        // Ensure CartQty column exists (for older DBs)
        try { var mc=conn.CreateCommand(); mc.CommandText="ALTER TABLE Cart ADD COLUMN CartQty INTEGER NOT NULL DEFAULT 1"; mc.ExecuteNonQuery(); } catch {}
        // Check if already in cart — if so update qty
        var chk=conn.CreateCommand();
        chk.CommandText="SELECT Id FROM Cart WHERE PartId=@pid AND Username=@u";
        chk.Parameters.AddWithValue("@pid",partId); chk.Parameters.AddWithValue("@u",user);
        var existing=chk.ExecuteScalar();
        if(existing!=null){
            var upd=conn.CreateCommand();
            upd.CommandText="UPDATE Cart SET CartQty=@q WHERE PartId=@pid AND Username=@u";
            upd.Parameters.AddWithValue("@q",qty); upd.Parameters.AddWithValue("@pid",partId); upd.Parameters.AddWithValue("@u",user);
            upd.ExecuteNonQuery();
            return Results.Ok();
        }
        var cmd=conn.CreateCommand();
        cmd.CommandText="INSERT INTO Cart (PartId,Username,CartQty) VALUES (@pid,@u,@q)";
        cmd.Parameters.AddWithValue("@pid",partId); cmd.Parameters.AddWithValue("@u",user); cmd.Parameters.AddWithValue("@q",qty);
        cmd.ExecuteNonQuery();
        return Results.Ok();
    } catch(Exception ex) {
        return Results.Problem(ex.Message);
    }
});

// Update cart item quantity
app.MapPut("/api/cart/{partId:int}", async (int partId, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    var body=await ctx.Request.ReadFromJsonAsync<CartAddInput>();
    int qty=body?.Quantity>0?body.Quantity:1;
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="UPDATE Cart SET CartQty=@q WHERE PartId=@pid AND Username=@u";
    cmd.Parameters.AddWithValue("@q",qty); cmd.Parameters.AddWithValue("@pid",partId); cmd.Parameters.AddWithValue("@u",user);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

app.MapDelete("/api/cart/{cartId:int}", (int cartId, HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="DELETE FROM Cart WHERE Id=@id AND Username=@u";
    cmd.Parameters.AddWithValue("@id",cartId); cmd.Parameters.AddWithValue("@u",user);
    cmd.ExecuteNonQuery(); return Results.Ok();
});

app.MapDelete("/api/cart", (HttpContext ctx) => {
    var user=GetUser(ctx); if(user==null) return Results.Unauthorized();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="DELETE FROM Cart WHERE Username=@u";
    cmd.Parameters.AddWithValue("@u",user); cmd.ExecuteNonQuery();
    return Results.Ok();
});

// ── Deleted parts history ──────────────────────────────────────
app.MapGet("/api/deleted", (HttpContext ctx) => {
    if(GetUser(ctx)==null) return Results.Unauthorized();
    using var conn=new SqliteConnection($"Data Source={dbPath}"); conn.Open();
    var cmd=conn.CreateCommand();
    cmd.CommandText="SELECT Id,OriginalPartId,PartName,BrandName,Model,PartNumber,GroupName,Quantity,Place,PurchaseOrder,DeletedBy,DeletedAt FROM DeletedParts ORDER BY DeletedAt DESC LIMIT 300";
    var list=new List<object>();
    using var r=cmd.ExecuteReader();
    while(r.Read()) list.Add(new{
        id=r.GetInt32(0),originalPartId=r.GetInt32(1),partName=r.GetString(2),brandName=r.GetString(3),
        model=r.GetString(4),partNumber=r.GetString(5),groupName=r.GetString(6),quantity=r.GetInt32(7),
        place=r.GetString(8),purchaseOrder=r.GetString(9),deletedBy=r.GetString(10),deletedAt=r.GetString(11)});
    return Results.Ok(list);
});

app.MapGet("/",(HttpContext ctx)=>{ ctx.Response.ContentType="text/html"; return ctx.Response.SendFileAsync("wwwroot/index.html"); });
app.UseStaticFiles();

Console.WriteLine("✅  Fixauto Decarie Inventory at http://localhost:5000");
Console.WriteLine("🔑  Default login: admin / admin123");
app.Run();

record Part(int Id,string PartName,string BrandName,string Model,string PartNumber,int Quantity,string Place,bool IsAvailable,bool IsReserved,bool IsUsed,string ReservedNote,string Notes,int? ParentId,string CreatedAt,string ImageFile,int? GroupId,string GroupName);
record PartInput(string? PartName,string BrandName,string? Model,string PartNumber,int? GroupId,int Quantity,string Place,bool IsAvailable,bool IsReserved,bool IsUsed,string? ReservedNote,string? Notes,string? ImageFile);
record HistoryEntry(int Id,int PartId,string Action,string Detail,string Username,string ChangedAt);
record LoginInput(string Username,string Password);
record NewUserInput(string Username,string Password,bool IsAdmin);
record PasswordInput(string Password);
record AddQtyInput(int Quantity);
record ReserveSplitInput(int ReserveQty,string? ReservedNote);
record GroupInput(string Name);
record CartAddInput(int Quantity);
record DeleteInput(string PurchaseOrder,int Quantity=0);
record RemoveQtyInput(int Quantity,string PurchaseOrder);
