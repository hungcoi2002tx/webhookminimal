using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

var app = builder.Build();

// Swagger always available (useful for testing)
app.UseSwagger();
app.UseSwaggerUI();

// Log directory for script output
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

// Track running scripts (store process + start time so we can kill if needed)
var runningScripts = new ConcurrentDictionary<string, (Process Process, DateTime StartedAt)>();

// ---------------------------------------------------------------------------
// Health check (no auth required)
// ---------------------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck")
   .WithTags("Health")
   .WithOpenApi();

// ---------------------------------------------------------------------------
// API Key middleware – skips /health and /swagger paths
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var config = context.RequestServices.GetRequiredService<IConfiguration>();
    var expectedKey = config["ApiKey"];

    if (string.IsNullOrEmpty(expectedKey))
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "ApiKey is not configured on server." });
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
        providedKey != expectedKey)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API Key." });
        return;
    }

    await next();
});

// ---------------------------------------------------------------------------
// GET /api/scripts – List all configured scripts
// ---------------------------------------------------------------------------
app.MapGet("/api/scripts", (IConfiguration config) =>
{
    var scripts = config.GetSection("Scripts").GetChildren()
        .Select(s => new
        {
            name = s.Key,
            path = s.Value,
            exists = File.Exists(s.Value),
            running = runningScripts.ContainsKey(s.Key),
            startedAt = runningScripts.TryGetValue(s.Key, out var info) ? info.StartedAt : (DateTime?)null
        })
        .ToList();

    return Results.Ok(scripts);
})
.WithName("ListScripts")
.WithTags("Scripts")
.WithOpenApi();

// ---------------------------------------------------------------------------
// POST /api/scripts/{name}/execute – Fire-and-forget script execution
// ---------------------------------------------------------------------------
app.MapPost("/api/scripts/{name}/execute", (string name, IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    var scriptPath = config[$"Scripts:{name}"];

    if (string.IsNullOrEmpty(scriptPath))
        return Results.NotFound(new { error = $"Script '{name}' is not configured." });

    if (!File.Exists(scriptPath))
        return Results.BadRequest(new { error = $"Script file not found: {scriptPath}" });

    if (runningScripts.TryGetValue(name, out var running))
        return Results.Conflict(new { error = $"Script '{name}' is already running.", startedAt = running.StartedAt });

    var logFile = Path.Combine(logDir, $"{name}.log");

    // Fire-and-forget: start process, return 202 immediately
    _ = Task.Run(async () =>
    {
        Process? process = null;
        try
        {
            // Write header to log
            await File.WriteAllTextAsync(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === Starting: {scriptPath} ===\n");

            // Let cmd.exe redirect output to log file directly
            // "< NUL" auto-skips any `pause` commands in the bat file
            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\" >> \"{logFile}\" 2>&1 < NUL\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ""
            };

            process.Start();
            runningScripts[name] = (process, DateTime.UtcNow);
            await process.WaitForExitAsync();

            await File.AppendAllTextAsync(logFile, $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === Finished with exit code: {process.ExitCode} ===\n");
            logger.LogInformation("Script '{Name}' finished with exit code {ExitCode}", name, process.ExitCode);

            // --- Auto Health Check after script completes ---
            var healthCheckUrl = config[$"HealthCheckUrls:{name}"];
            var discordWebhookUrl = config["DiscordWebhookUrl"];
            if (!string.IsNullOrEmpty(healthCheckUrl) && !string.IsNullOrEmpty(discordWebhookUrl))
            {
                logger.LogInformation("Script '{Name}' finished → starting health check for {Url}", name, healthCheckUrl);
                await HealthCheckWithRetryAsync(name, healthCheckUrl, discordWebhookUrl, httpClientFactory, config, logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script '{Name}' failed", name);
            try { await File.AppendAllTextAsync(logFile, $"\n[EXCEPTION] {ex.Message}\n"); } catch { }
        }
        finally
        {
            runningScripts.TryRemove(name, out _);
            try { process?.Dispose(); } catch { }
        }
    });

    logger.LogInformation("Script '{Name}' triggered (fire-and-forget)", name);

    return Results.Accepted($"/api/scripts/{name}/log", new
    {
        message = $"Script '{name}' has been triggered.",
        logUrl = $"/api/scripts/{name}/log",
        triggeredAt = DateTime.UtcNow
    });
})
.WithName("ExecuteScript")
.WithTags("Scripts")
.WithOpenApi();

// ---------------------------------------------------------------------------
// DELETE /api/scripts/{name}/kill – Force-kill a stuck script
// ---------------------------------------------------------------------------
app.MapDelete("/api/scripts/{name}/kill", (string name, ILogger<Program> logger) =>
{
    if (!runningScripts.TryRemove(name, out var info))
        return Results.NotFound(new { error = $"Script '{name}' is not currently running." });

    try
    {
        info.Process.Kill(entireProcessTree: true);
        logger.LogWarning("Script '{Name}' was force-killed", name);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to kill script '{Name}'", name);
    }
    finally
    {
        try { info.Process.Dispose(); } catch { }
    }

    return Results.Ok(new { message = $"Script '{name}' has been killed.", startedAt = info.StartedAt });
})
.WithName("KillScript")
.WithTags("Scripts")
.WithOpenApi();

// ---------------------------------------------------------------------------
// GET /api/scripts/{name}/log – View latest log output
// ---------------------------------------------------------------------------
app.MapGet("/api/scripts/{name}/log", (string name, IConfiguration config) =>
{
    var scriptPath = config[$"Scripts:{name}"];
    if (string.IsNullOrEmpty(scriptPath))
        return Results.NotFound(new { error = $"Script '{name}' is not configured." });

    var logFile = Path.Combine(logDir, $"{name}.log");
    if (!File.Exists(logFile))
        return Results.NotFound(new { error = $"No log found for '{name}'. Script has not been executed yet." });

    using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fs);
    var content = reader.ReadToEnd();

    return Results.Ok(new
    {
        script = name,
        running = runningScripts.ContainsKey(name),
        log = content,
        logFile
    });
})
.WithName("GetScriptLog")
.WithTags("Scripts")
.WithOpenApi();

// ---------------------------------------------------------------------------
// GET /api/healthcheck/urls – List all configured health-check URLs
// ---------------------------------------------------------------------------
app.MapGet("/api/healthcheck/urls", (IConfiguration config) =>
{
    var urls = config.GetSection("HealthCheckUrls").GetChildren()
        .Select(s => new { name = s.Key, url = s.Value })
        .ToList();

    return Results.Ok(urls);
})
.WithName("ListHealthCheckUrls")
.WithTags("HealthCheck")
.WithOpenApi();

// ---------------------------------------------------------------------------
// POST /api/healthcheck/{name} – Check a single URL (fire-and-forget with retry)
// ---------------------------------------------------------------------------
app.MapPost("/api/healthcheck/{name}", (string name, IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    var url = config[$"HealthCheckUrls:{name}"];
    if (string.IsNullOrEmpty(url))
        return Results.NotFound(new { error = $"Health check URL '{name}' is not configured." });

    var discordWebhookUrl = config["DiscordWebhookUrl"];
    if (string.IsNullOrEmpty(discordWebhookUrl))
        return Results.BadRequest(new { error = "DiscordWebhookUrl is not configured." });

    _ = Task.Run(async () =>
    {
        await HealthCheckWithRetryAsync(name, url, discordWebhookUrl, httpClientFactory, config, logger);
    });

    logger.LogInformation("Health check for '{Name}' triggered (fire-and-forget, will start checking after 3 minutes)", name);

    return Results.Accepted(value: new
    {
        message = $"Health check for '{name}' has been triggered. Will check after 3 minutes.",
        url,
        triggeredAt = DateTime.UtcNow
    });
})
.WithName("CheckHealthUrl")
.WithTags("HealthCheck")
.WithOpenApi();

// ---------------------------------------------------------------------------
// POST /api/healthcheck/all – Check all configured URLs
// ---------------------------------------------------------------------------
app.MapPost("/api/healthcheck/all", (IConfiguration config, IHttpClientFactory httpClientFactory, ILogger<Program> logger) =>
{
    var discordWebhookUrl = config["DiscordWebhookUrl"];
    if (string.IsNullOrEmpty(discordWebhookUrl))
        return Results.BadRequest(new { error = "DiscordWebhookUrl is not configured." });

    var urls = config.GetSection("HealthCheckUrls").GetChildren().ToList();
    if (!urls.Any())
        return Results.BadRequest(new { error = "No HealthCheckUrls configured." });

    foreach (var entry in urls)
    {
        var entryName = entry.Key;
        var entryUrl = entry.Value;
        if (string.IsNullOrEmpty(entryUrl)) continue;

        _ = Task.Run(async () =>
        {
            await HealthCheckWithRetryAsync(entryName, entryUrl, discordWebhookUrl, httpClientFactory, config, logger);
        });
    }

    logger.LogInformation("Health check for ALL URLs triggered (fire-and-forget)");

    return Results.Accepted(value: new
    {
        message = "Health check for all URLs has been triggered. Will start checking after 3 minutes.",
        urls = urls.Select(u => new { name = u.Key, url = u.Value }),
        triggeredAt = DateTime.UtcNow
    });
})
.WithName("CheckAllHealthUrls")
.WithTags("HealthCheck")
.WithOpenApi();

app.Run();

// =============================================================================
// Helper: Health check with retry logic
// =============================================================================
static async Task HealthCheckWithRetryAsync(
    string name,
    string url,
    string discordWebhookUrl,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger logger)
{
    var initialDelayMinutes = config.GetValue("HealthCheckSettings:InitialDelayMinutes", 3);
    var retryIntervalMinutes = config.GetValue("HealthCheckSettings:RetryIntervalMinutes", 1);
    var maxTotalMinutes = config.GetValue("HealthCheckSettings:MaxTotalMinutes", 10);

    var client = httpClientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15);

    // Check if this URL needs HTTP/2
    var http2Urls = config.GetSection("HealthCheckSettings:Http2Urls").Get<string[]>() ?? [];
    var useHttp2 = http2Urls.Contains(name, StringComparer.OrdinalIgnoreCase);

    logger.LogInformation("Health check '{Name}': waiting {Delay} minutes before first check...", name, initialDelayMinutes);
    await Task.Delay(TimeSpan.FromMinutes(initialDelayMinutes));

    var stopwatch = Stopwatch.StartNew();
    var attemptCount = 0;

    while (stopwatch.Elapsed.TotalMinutes < maxTotalMinutes)
    {
        attemptCount++;
        try
        {
            logger.LogInformation("Health check '{Name}': attempt #{Attempt} → {Url} (HTTP/{Version})", name, attemptCount, url, useHttp2 ? "2" : "1.1");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (useHttp2)
            {
                request.Version = System.Net.HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            }
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Health check '{Name}': ✅ SUCCESS (HTTP {StatusCode})", name, (int)response.StatusCode);
                await SendDiscordNotificationAsync(client, discordWebhookUrl, name, url, true, (int)response.StatusCode, attemptCount, logger);
                return;
            }

            logger.LogWarning("Health check '{Name}': attempt #{Attempt} failed with HTTP {StatusCode}", name, attemptCount, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Health check '{Name}': attempt #{Attempt} threw exception", name, attemptCount);
        }

        // Wait before retrying (unless we'd exceed the total time)
        if (stopwatch.Elapsed.TotalMinutes + retryIntervalMinutes < maxTotalMinutes)
        {
            await Task.Delay(TimeSpan.FromMinutes(retryIntervalMinutes));
        }
        else
        {
            break;
        }
    }

    // All retries exhausted
    logger.LogError("Health check '{Name}': ❌ FAILED after {Minutes} minutes ({Attempts} attempts)", name, maxTotalMinutes, attemptCount);
    await SendDiscordNotificationAsync(client, discordWebhookUrl, name, url, false, 0, attemptCount, logger);
}

// =============================================================================
// Helper: Send Discord webhook notification
// =============================================================================
static async Task SendDiscordNotificationAsync(
    HttpClient client,
    string webhookUrl,
    string name,
    string url,
    bool isSuccess,
    int statusCode,
    int attempts,
    ILogger logger)
{
    try
    {
        var color = isSuccess ? 3066993 : 15158332; // Green : Red
        var statusEmoji = isSuccess ? "✅" : "❌";
        var statusText = isSuccess ? "ACTIVE" : "DOWN";
        var description = isSuccess
            ? $"URL trả về HTTP **{statusCode}** sau **{attempts}** lần kiểm tra."
            : $"URL không phản hồi thành công sau **{attempts}** lần kiểm tra trong 10 phút.";

        var payload = new
        {
            embeds = new[]
            {
                new
                {
                    title = $"{statusEmoji} {name} is {statusText}",
                    description,
                    color,
                    fields = new[]
                    {
                        new { name = "URL", value = url, inline = true },
                        new { name = "Attempts", value = attempts.ToString(), inline = true },
                        new { name = "Checked At", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), inline = false }
                    },
                    footer = new { text = "WebhookMinimalApi Health Check" }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(webhookUrl, content);

        if (response.IsSuccessStatusCode)
            logger.LogInformation("Discord notification sent for '{Name}'", name);
        else
            logger.LogWarning("Discord notification failed for '{Name}': HTTP {StatusCode}", name, (int)response.StatusCode);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send Discord notification for '{Name}'", name);
    }
}
