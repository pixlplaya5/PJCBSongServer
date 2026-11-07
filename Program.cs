using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;


var settingsPath = Path.Combine(
    AppContext.BaseDirectory,
    "settings.json");

var settings = await ServerSettings.LoadOrSetupAsync(
    settingsPath);

string dataDirectory = settings.DataDirectory;
Directory.CreateDirectory(dataDirectory);

string dbPath = Path.Combine(dataDirectory, "rhythm.db");
var uploadStorageLock = new SemaphoreSlim(1, 1);

var builder = WebApplication.CreateBuilder(args);

// Use the saved address instead of ASPNETCORE_URLS.
builder.WebHost.UseUrls(settings.ListenUrl);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 55 * 1024 * 1024;
});

builder.Services.Configure<
    Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 52 * 1024 * 1024;
    options.ValueCountLimit = 32;
});

// Continue with the existing service registrations here.

string jwtKey = settings.JwtKey;

var signingKey = new SymmetricSecurityKey(
    Convert.FromBase64String(jwtKey));

builder.WebHost.ConfigureKestrel(options =>
{
    // ZIP upload limit plus multipart overhead.
    options.Limits.MaxRequestBodySize = 55 * 1024 * 1024;
});

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options =>
    {
        options.MultipartBodyLengthLimit = 52 * 1024 * 1024;
        options.ValueCountLimit = 32;
    });

builder.Services.AddDbContext<AppDb>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<PasswordHasher<AppUser>>();

builder.Services.AddAuthentication(
    JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "PJCBSongServer",

                ValidateAudience = true,
                ValidAudience = "PJCB",

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,

                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),

                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = ClaimTypes.Role
            };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;

    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy("uploads", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

// Appropriate for a single-server MVP. Use migrations for
// a managed production database schema.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var jsonOptions = new JsonSerializerOptions(
    JsonSerializerDefaults.Web);


string CreateToken(AppUser user)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role)
    };

    var credentials = new SigningCredentials(
        signingKey, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: "PJCBSongServer",
        audience: "PJCB",
        claims: claims,
        expires: DateTime.UtcNow.AddHours(2),
        signingCredentials: credentials);

    return new JwtSecurityTokenHandler().WriteToken(token);
}

app.MapGet("/", () =>
{
    string indexPath = Path.Combine(AppContext.BaseDirectory, "index.html");

    return File.Exists(indexPath)
        ? Results.File(indexPath, "text/html; charset=utf-8")
        : Results.Content("Nothing to see here", "text/plain");
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "online"
}));

// REGISTER
app.MapPost("/api/auth/register", async (
    RegisterRequest input,
    AppDb db,
    PasswordHasher<AppUser> hasher) =>
{
    string username = (input.Username ?? "").Trim().ToLowerInvariant();
    string password = input.Password ?? "";

    if (username.Length is < 5 or > 254)
    {
        return Results.BadRequest(new
        {
            error = "A valid username is required."
        });
    }

    if (password.Length is < 12 or > 128)
        return Results.BadRequest(new
        {
            error = "Password must be 12-128 characters."
        });

    if (await db.Users.AnyAsync(u => u.Username == username))
        return Results.Conflict(new
        {
            error = "An account with this username already exists."
        });

    bool isAdmin = string.Equals(
            username,
            settings.AdminUsername,
            StringComparison.OrdinalIgnoreCase) &&
        !await db.Users.AnyAsync(u => u.Role == "Admin");

    var user = new AppUser
    {
        Id = Guid.NewGuid().ToString("N"),
        Username = username,
        Role = isAdmin ? "Admin" : "Player"
    };

    user.PasswordHash = hasher.HashPassword(user, password);

    db.Users.Add(user);
    await db.SaveChangesAsync();

    if (isAdmin)
    {
        //settings.AdminBootstrapPending = false;

        //await ServerSettings.SaveAsync(
        //   settingsPath, settings);
    }

    return Results.Created("/api/me", new
    {
        id = user.Id,
        username = user.Username,
        role = user.Role
    });
})
.AllowAnonymous()
.RequireRateLimiting("auth");

// LOGIN
app.MapPost("/api/auth/login", async (
    LoginRequest input,
    AppDb db,
    PasswordHasher<AppUser> hasher) =>
{
    string username = (input.Username ?? "").Trim().ToLowerInvariant();

    var user = await db.Users.FirstOrDefaultAsync(
        u => u.Username == username);

    if (user == null)
        return Results.Unauthorized();

    var result = hasher.VerifyHashedPassword(
        user, user.PasswordHash, input.Password ?? "");

    if (result == PasswordVerificationResult.Failed)
        return Results.Unauthorized();

    if (result == PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = hasher.HashPassword(
            user, input.Password!);
        await db.SaveChangesAsync();
    }

    return Results.Ok(new
    {
        accessToken = CreateToken(user),
        tokenType = "Bearer",
        expiresInSeconds = 7200
    });
})
.AllowAnonymous()
.RequireRateLimiting("auth");

// CURRENT PLAYER
app.MapGet("/api/me", (ClaimsPrincipal principal) =>
    Results.Ok(new
    {
        id = principal.FindFirstValue(
            ClaimTypes.NameIdentifier),
        email = principal.FindFirstValue(ClaimTypes.Email),
        role = principal.FindFirstValue(ClaimTypes.Role)
    }))
.RequireAuthorization();

// LIST PUBLISHED SONGS
app.MapGet("/api/songs", async (
    AppDb db, string? search, int? page, int? pageSize) =>
{
    int p = Math.Clamp(page ?? 1, 1, 1000000);
    int size = Math.Clamp(pageSize ?? 20, 1, 100);

    var query = db.Songs.AsNoTracking()
        .Where(s => s.Status == "Published");

    if (!string.IsNullOrWhiteSpace(search))
    {
        string term = search.Trim()[..Math.Min(
            search.Trim().Length, 100)];

        query = query.Where(s =>
            s.Title.Contains(term) ||
            s.Artist.Contains(term) ||
            s.Charter.Contains(term));
    }

    int total = await query.CountAsync();

    var songs = await query
        .OrderBy(s => s.Title)
        .ThenBy(s => s.Id)
        .Skip((p - 1) * size)
        .Take(size)
        .Select(s => new
        {
            s.Id,
            s.Title,
            s.Artist,
            s.Charter,
            s.Bpm,
            s.Difficulty,
            s.DurationMilliseconds,
            s.CreatedUtc
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = p,
        pageSize = size,
        total,
        songs
    });
})
.AllowAnonymous();

// LIST USER POSTS (BETA)
app.MapGet("/api/posts", async (
    AppDb db, string? search, int? page, int? pageSize) =>
{
    int p = Math.Clamp(page ?? 1, 1, 1000000);
    int size = Math.Clamp(pageSize ?? 20, 1, 100);

    var query = db.TextPosts.AsNoTracking()
        .Where(s => s.Status == "Published");

    if (!string.IsNullOrWhiteSpace(search))
    {
        string term = search.Trim()[..Math.Min(
            search.Trim().Length, 100)];

        query = query.Where(s =>
            s.Value.Contains(term) ||
            s.PostedBy.Contains(term));
    }

    int total = await query.CountAsync();

    var posts = await query
        .OrderBy(s => s.Value)
        .ThenBy(s => s.Id)
        .Skip((p - 1) * size)
        .Take(size)
        .Select(s => new
        {
            s.Id,
            s.Value,
            s.PostedBy,
            s.CreatedUtc
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = p,
        pageSize = size,
        total,
        posts
    });
})
.AllowAnonymous();

// GET POST BY ID
app.MapGet("/api/posts/{id}", async (
    string id, AppDb db) =>
{
    if (!Guid.TryParseExact(id, "N", out _))
        return Results.BadRequest();

    var post = await db.TextPosts.AsNoTracking()
        .Where(s => s.Id == id && s.Status == "Published")
        .Select(s => new
        {
            s.Id,
            s.Value,
            s.PostedBy,
            s.CreatedUtc
        })
        .FirstOrDefaultAsync();

    return post == null
        ? Results.NotFound()
        : Results.Ok(post);
})
.AllowAnonymous();

app.MapGet("/api/lpRand", () => Results.Ok("TODO: get random post value"));

// SONG DETAILS
app.MapGet("/api/songs/{id}", async (
    string id, AppDb db) =>
{
    if (!Guid.TryParseExact(id, "N", out _))
        return Results.BadRequest();

    var song = await db.Songs.AsNoTracking()
        .Where(s => s.Id == id && s.Status == "Published")
        .Select(s => new
        {
            s.Id,
            s.Title,
            s.Artist,
            s.Charter,
            s.Bpm,
            s.Difficulty,
            s.DurationMilliseconds,
            s.CreatedUtc,
            downloadUrl = $"/api/songs/{s.Id}/download"
        })
        .FirstOrDefaultAsync();

    return song == null
        ? Results.NotFound()
        : Results.Ok(song);
})
.AllowAnonymous();

// UPLOAD ZIP
app.MapPost("/api/songs", async (
    HttpRequest request,
    ClaimsPrincipal principal,
    AppDb db) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(
            new { error = "Expected multipart/form-data." });

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");

    if (file == null || file.Length == 0)
        return Results.BadRequest(
            new { error = "Provide a ZIP in the 'file' field." });

    const long maxZipBytes = 50L * 1024 * 1024;

    if (file.Length > maxZipBytes)
        return Results.BadRequest(
            new { error = "ZIP must be 50 MB or smaller." });

    if (!string.Equals(Path.GetExtension(file.FileName),
        ".zip", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(
            new { error = "Only ZIP files are accepted." });
    }

    await uploadStorageLock.WaitAsync();
    using var uploadLock = new SemaphoreReleaser(uploadStorageLock);

    long currentDataSize = GetDirectorySize(dataDirectory);
    if (currentDataSize > settings.AllocatedDataSizeBytes ||
        file.Length > settings.AllocatedDataSizeBytes - currentDataSize)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status507InsufficientStorage,
            title: "The server's allocated data storage is full.");
    }

    string tempPath = Path.Combine(
        dataDirectory, Guid.NewGuid().ToString("N") + ".tmp");

    try
    {
        await using (var output = new FileStream(
            tempPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, useAsync: true))
        {
            await file.CopyToAsync(output);
        }

        // Check the archive WITHOUT extracting it.
        SongManifest manifest;

        try
        {
            using var archive = ZipFile.OpenRead(tempPath);

            if (archive.Entries.Count is < 1 or > 1000)
                return Results.BadRequest(
                    new { error = "Invalid ZIP entry count." });

            long totalExpanded = 0;
            var names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');

                if (name.StartsWith('/') ||
                    name.Contains(':') ||
                    name.Split('/').Any(part => part == ".."))
                {
                    return Results.BadRequest(
                        new { error = "Unsafe ZIP path." });
                }

                if (!names.Add(name))
                    return Results.BadRequest(
                        new { error = "Duplicate ZIP entry." });

                // Reject Unix symbolic links.
                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                    return Results.BadRequest(
                        new { error = "Symbolic links are not allowed." });

                totalExpanded += entry.Length;

                if (totalExpanded > 500L * 1024 * 1024)
                    return Results.BadRequest(
                        new { error = "Expanded ZIP exceeds 500 MB." });

                if (entry.Length > 0 &&
                    (entry.CompressedLength == 0 ||
                     entry.Length > entry.CompressedLength * 200L))
                {
                    return Results.BadRequest(
                        new { error = "Suspicious compression ratio." });
                }
            }

            var metadataEntry = archive.GetEntry("song.json");
            var chartEntry = archive.GetEntry("notes.mid") ?? archive.GetEntry("notes.chart");

            bool hasAudio = archive.Entries.Any(e =>
                new[] { ".ogg", ".mp3", ".wav" }
                .Contains(Path.GetExtension(e.FullName),
                    StringComparer.OrdinalIgnoreCase));

            if (metadataEntry == null ||
                chartEntry == null || !hasAudio)
            {
                return Results.BadRequest(new
                {
                    error = "ZIP must contain song.json, notes.mid/notes.chart, and compatible audio."
                });
            }

            if (metadataEntry.Length > 64 * 1024)
                return Results.BadRequest(
                    new { error = "song.json is too large." });

            await using var metadataStream = metadataEntry.Open();

            manifest = await JsonSerializer.DeserializeAsync<SongManifest>(
                metadataStream, jsonOptions)
                ?? throw new InvalidDataException("Invalid metadata.");
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest(
                new { error = "Invalid ZIP or song metadata." });
        }
        catch (JsonException)
        {
            return Results.BadRequest(
                new { error = "song.json contains invalid JSON." });
        }

        if (string.IsNullOrWhiteSpace(manifest.Title) ||
            manifest.Title.Length > 120 ||
            string.IsNullOrWhiteSpace(manifest.Artist) ||
            manifest.Artist.Length > 120 ||
            (manifest.Charter?.Length ?? 0) > 120 ||
            !double.IsFinite(manifest.Bpm) ||
            manifest.Bpm < 1 || manifest.Bpm > 1000 ||
            manifest.Difficulty < 0 || manifest.Difficulty > 100 ||
            manifest.DurationMilliseconds < 1 ||
            manifest.DurationMilliseconds > 2116800000) // 24.5 days (anything higher would overflow)
        {
            return Results.BadRequest(
                new { error = "Song metadata is outside allowed limits." });
        }

        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(dataDirectory, id + ".zip");

        var song = new Song
        {
            Id = id,
            Title = manifest.Title.Trim(),
            Artist = manifest.Artist.Trim(),
            Charter = (manifest.Charter ?? "").Trim(),
            Bpm = manifest.Bpm,
            Difficulty = manifest.Difficulty,
            DurationMilliseconds = manifest.DurationMilliseconds,
            OwnerId = principal.FindFirstValue(
                ClaimTypes.NameIdentifier)!,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow
        };

        // Save the ZIP first; remove it if the database insert fails.
        File.Move(tempPath, destination);

        try
        {
            db.Songs.Add(song);
            await db.SaveChangesAsync();
        }
        catch
        {
            File.Delete(destination);
            throw;
        }

        return Results.Created($"/api/songs/{id}", new
        {
            id,
            status = song.Status,
            message = "Uploaded. Waiting for moderator approval."
        });
    }
    finally
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
})
.RequireAuthorization()
.RequireRateLimiting("uploads");

// DOWNLOAD PUBLISHED ZIP
app.MapGet("/api/songs/{id}/download", async (
    string id, AppDb db) =>
{
    if (!Guid.TryParseExact(id, "N", out _))
        return Results.BadRequest();

    var song = await db.Songs.AsNoTracking()
        .FirstOrDefaultAsync(s =>
            s.Id == id && s.Status == "Published");

    if (song == null)
        return Results.NotFound();

    string path = Path.Combine(dataDirectory, song.Id + ".zip");

    if (!File.Exists(path))
        return Results.NotFound();

    return Results.File(path, "application/zip",
        song.Id + ".zip", enableRangeProcessing: true);
})
.AllowAnonymous();

// ADMIN: LIST PENDING UPLOADS
app.MapGet("/api/admin/songs/pending", async (AppDb db) =>
{
    var songs = await db.Songs.AsNoTracking()
        .Where(s => s.Status == "Pending")
        .OrderBy(s => s.CreatedUtc)
        .Select(s => new
        {
            s.Id, s.Title, s.Artist, s.Charter,
            s.OwnerId, s.CreatedUtc
        })
        .ToListAsync();

    return Results.Ok(songs);
})
.RequireAuthorization(policy => policy.RequireRole("Admin"));

// ADMIN: PUBLISH SONG
app.MapPost("/api/admin/songs/{id}/publish", async (
    string id, AppDb db) =>
{
    var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);

    if (song == null)
        return Results.NotFound();

    if (!File.Exists(Path.Combine(dataDirectory, id + ".zip")))
        return Results.Conflict(
            new { error = "Song ZIP is missing." });

    song.Status = "Published";
    await db.SaveChangesAsync();

    return Results.Ok(new { id, status = song.Status });
})
.RequireAuthorization(policy => policy.RequireRole("Admin"));

// ADMIN: DELETE SONG
app.MapDelete("/api/admin/songs/{id}", async (
    string id, AppDb db) =>
{
    var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);

    if (song == null)
        return Results.NotFound();

    db.Songs.Remove(song);
    await db.SaveChangesAsync();

    string path = Path.Combine(dataDirectory, id + ".zip");

    if (File.Exists(path))
        File.Delete(path);

    return Results.NoContent();
})
.RequireAuthorization(policy => policy.RequireRole("Admin"));

app.Run();

static long GetDirectorySize(string directory)
{
    long total = 0;

    foreach (string path in Directory.EnumerateFiles(
        directory, "*", SearchOption.AllDirectories))
    {
        long fileSize = new FileInfo(path).Length;
        total = fileSize > long.MaxValue - total
            ? long.MaxValue
            : total + fileSize;
    }

    return total;
}

public sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
{
    public void Dispose() => semaphore.Release();
}

public sealed class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Song> Songs => Set<Song>();
    public DbSet<TextPost> TextPosts => Set<TextPost>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AppUser>().HasKey(u => u.Id);
        model.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();

        model.Entity<Song>().HasKey(s => s.Id);
        model.Entity<Song>().HasIndex(s => s.Status);
        model.Entity<Song>().HasIndex(s => s.Title);

        model.Entity<TextPost>().HasKey(p => p.Id);
        model.Entity<TextPost>().HasIndex(p => p.Status);
        model.Entity<TextPost>().HasIndex(p => p.Value);
    }
}

public sealed class AppUser
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Player";
}

public sealed class Song
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Charter { get; set; } = "";
    public double Bpm { get; set; }
    public int Difficulty { get; set; }
    public int DurationMilliseconds { get; set; }
    public string OwnerId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; }
}

public sealed class SongManifest
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string? Charter { get; set; }
    public double Bpm { get; set; }
    public int Difficulty { get; set; }
    public int DurationMilliseconds { get; set; }
}

public sealed class RegisterRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class TextPost
{
    public string Id { get; set; } = "";
    public string Value { get; set; } = "";
    public string PostedBy { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; }
}


public sealed class ServerSettings
{
    private const long BytesPerGibibyte = 1024L * 1024 * 1024;

    public string DataDirectory { get; set; } = "";
    public long AllocatedDataSizeBytes { get; set; } = 10 * BytesPerGibibyte;
    public string ListenUrl { get; set; } =
        "http://127.0.0.1:5080";

    public string JwtKey { get; set; } = "";
    public string AdminUsername { get; set; } = "";

    // Allows the configured email to claim the first admin account
    // exactly once, rather than on every restart.
    //public bool AdminBootstrapPending { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

    public static async Task<ServerSettings> LoadOrSetupAsync(
        string settingsPath)
    {
        if (File.Exists(settingsPath))
        {
            Console.WriteLine("Loading server settings...");

            try
            {
                string json = await File.ReadAllTextAsync(
                    settingsPath);

                var settings =
                    JsonSerializer.Deserialize<ServerSettings>(
                        json, JsonOptions);

                if (settings == null ||
                    string.IsNullOrWhiteSpace(settings.JwtKey) ||
                    string.IsNullOrWhiteSpace(settings.AdminUsername) ||
                    string.IsNullOrWhiteSpace(settings.DataDirectory) ||
                    settings.AllocatedDataSizeBytes <= 0 ||
                    !Directory.Exists(settings.DataDirectory) ||
                    !IsValidListenUrl(settings.ListenUrl) ||
                    !IsValidKey(settings.JwtKey))
                {
                    throw new InvalidDataException(
                        "One or more settings are missing or invalid.");
                }

                Console.WriteLine("Settings loaded successfully.");
                return settings;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is JsonException ||
                ex is InvalidDataException ||
                ex is ArgumentException ||
                ex is FormatException)
            {
                throw new InvalidOperationException(
                    $"Cannot load '{settingsPath}'. " +
                    "Back up and repair the settings file before " +
                    "restarting. The file was not overwritten.", ex);
            }
        }

        return await RunFirstTimeSetupAsync(settingsPath);
    }

    private static async Task<ServerSettings> RunFirstTimeSetupAsync(
        string settingsPath)
    {
        Console.Clear();

        Console.WriteLine("========================================");
        Console.WriteLine("      Project CloBeats Server Setup");
        Console.WriteLine("========================================");
        Console.WriteLine();
        Console.WriteLine(
            "This is the first launch. Let's configure your server.");
        Console.WriteLine();

        var settings = new ServerSettings();

        // 1. Data directory
        string defaultDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "PJCBSongServer_data");

        Console.WriteLine("Where should song ZIPs and SQLite data be stored?");
        Console.WriteLine($"Default: {defaultDirectory}");
        Console.Write("Data directory: ");

        string? directoryInput = Console.ReadLine();

        string selectedDirectory = string.IsNullOrWhiteSpace(
            directoryInput)
            ? defaultDirectory
            : directoryInput.Trim().Trim('"');

        settings.DataDirectory =
            Path.GetFullPath(selectedDirectory);

        Directory.CreateDirectory(settings.DataDirectory);

        // 2. Allocated data storage
        const long maxAllocatedGibibytes = 1_000_000;
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Allocated data storage limit");
            Console.WriteLine("Default: 10 GiB");
            Console.Write("Allocated size in GiB: ");

            string? sizeInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(sizeInput))
            {
                settings.AllocatedDataSizeBytes = 10 * BytesPerGibibyte;
                break;
            }

            if (long.TryParse(sizeInput, out long allocatedGibibytes) &&
                allocatedGibibytes is >= 1 and <= maxAllocatedGibibytes)
            {
                settings.AllocatedDataSizeBytes =
                    allocatedGibibytes * BytesPerGibibyte;
                break;
            }

            Console.WriteLine(
                $"Enter a whole number from 1 to {maxAllocatedGibibytes}, or press Enter for 10 GiB.");
        }

        // 3. Admin username
        while (true)
        {
            Console.WriteLine();
            Console.Write("Administrator username: ");

            string username = (Console.ReadLine() ?? "")
                .Trim().ToLowerInvariant();

            if (username.Length <= 254)
            {
                settings.AdminUsername = username;
                break;
            }

            Console.WriteLine("Please enter a valid username.");
        }

        // 4. Listening address
        Console.WriteLine();
        Console.WriteLine("Server listening address");
        Console.WriteLine("Default: http://127.0.0.1:5080");
        Console.WriteLine(
            "For internet hosting, prefer a reverse proxy with HTTPS.");
        Console.WriteLine("Example: https://pjcb.example.com:5080");
        Console.WriteLine(
            "Only choose a public-facing bind address if you " +
            "understand the network and firewall configuration.");
        Console.Write("Listen URL: ");

        string? urlInput = Console.ReadLine();

        settings.ListenUrl = string.IsNullOrWhiteSpace(urlInput)
            ? "http://127.0.0.1:5080"
            : urlInput.Trim();

        if (!IsValidListenUrl(settings.ListenUrl))
        {
            throw new InvalidOperationException(
                "Invalid listen URL. Use an HTTP URL such as " +
                "http://127.0.0.1:5080 and run setup again.");
        }

        // 5. Generate a strong signing key automatically.
        byte[] keyBytes = RandomNumberGenerator.GetBytes(48);
        settings.JwtKey = Convert.ToBase64String(keyBytes);

        //settings.AdminBootstrapPending = true;

        // Save only after all setup values have been collected.
        await SaveAsync(settingsPath, settings);

        Console.WriteLine();
        Console.WriteLine("Setup completed!");
        Console.WriteLine($"Settings: {settingsPath}");
        Console.WriteLine($"Data:     {settings.DataDirectory}");
        Console.WriteLine($"Listen:   {settings.ListenUrl}");
        Console.WriteLine();
        Console.WriteLine(
            "Your JWT signing key was generated and saved securely " +
            "as a random value in the settings file.");
        Console.WriteLine(
            "Keep the settings file private and back it up securely.");
        Console.WriteLine();


        return settings;
    }

    public static async Task SaveAsync(
        string settingsPath,
        ServerSettings settings)
    {
        string json = JsonSerializer.Serialize(
            settings, JsonOptions);

        string temporaryPath = settingsPath + ".tmp";

        await File.WriteAllTextAsync(temporaryPath, json);

        // Replace the settings file only after the new file is written.
        if (File.Exists(settingsPath))
            File.Move(temporaryPath, settingsPath, overwrite: true);
        else
            File.Move(temporaryPath, settingsPath);
    }

    private static bool IsValidKey(string key)
    {
        try
        {
            return Convert.FromBase64String(key).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidListenUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && !string.IsNullOrWhiteSpace(uri.Host)
            && uri.Port is >= 1 and <= 65535
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.AbsolutePath == "/";
    }

    private static string SanitizeString(string? input)
    {
        if (input != null)
            return new String(input.Where(Char.IsLetterOrDigit).ToArray());
        return string.Empty;
    }
}

