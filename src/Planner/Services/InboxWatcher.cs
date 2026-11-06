using Microsoft.Extensions.Options;

namespace Planner.Services;

/// <summary>
/// Следит за папкой inbox: положили туда файл бизнес-заказчика (xlsx/csv) — сервис сам разберёт его, подберёт задачи и
/// переложит файл в inbox/processed. Не удалось разобрать — файл уйдёт в inbox/failed с пояснением. Всё локально.
/// Опрос раз в 10 секунд: наблюдатель файловой системы на Windows-папках внутри Docker ненадёжен.
/// </summary>
public class InboxWatcher(IServiceScopeFactory scopes, IOptions<PlannerOptions> opt, AppState state, ILogger<InboxWatcher> log) : BackgroundService
{
    static readonly string[] Extensions = [".xlsx", ".xlsm", ".csv", ".txt"];
    static readonly SemaphoreSlim Gate = new(1, 1);

    public DateTime? LastScanUtc { get; private set; }
    public string? LastMessage { get; private set; }
    public bool LastFailed { get; private set; }
    public string HostPath => string.IsNullOrWhiteSpace(opt.Value.InboxHostPath) ? opt.Value.InboxPath : opt.Value.InboxHostPath;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ScanAsync(ct); }
            catch (Exception ex) { log.LogWarning(ex, "Сканирование папки inbox"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Проверить папку сейчас. Возвращает число обработанных файлов.</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct)) return 0;
        try
        {
            var dir = opt.Value.InboxPath;
            Directory.CreateDirectory(dir);
            var files = Directory.GetFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !Path.GetFileName(f).StartsWith("~$") && !Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .ToList();
            LastScanUtc = DateTime.UtcNow;

            var done = 0;
            foreach (var file in files)
            {
                // файл ещё копируется или открыт в Excel — подождём следующего прохода
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromSeconds(3)) continue;
                if (!await ProcessAsync(file, dir, ct)) continue;
                done++;
            }
            if (done > 0) state.Notify();
            return done;
        }
        finally { Gate.Release(); }
    }

    async Task<bool> ProcessAsync(string file, string dir, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        try
        {
            using var scope = scopes.CreateScope();
            var biz = scope.ServiceProvider.GetRequiredService<BusinessImport>();

            List<ParsedPriority> parsed;
            await using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var ms = new MemoryStream())
            {
                await fs.CopyToAsync(ms, ct);
                ms.Position = 0;
                parsed = biz.Parse(ms, name);
            }

            if (parsed.Count == 0)
            {
                Fail(file, dir, "В файле не нашлось строк с приоритетом (нужны «N приоритет», «приоритет N», число 1–9 в ячейке или high/medium/low).");
                return true;
            }

            var n = await biz.ImportAsync(parsed, name);
            var processed = Path.Combine(dir, "processed");
            Directory.CreateDirectory(processed);
            File.Move(file, Path.Combine(processed, $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}"), overwrite: true);
            LastMessage = $"{DateTime.Now:dd.MM HH:mm} — {name}: загружено тем {n}";
            LastFailed = false;
            log.LogInformation("Inbox: {File} → тем {N}", name, n);
            return true;
        }
        catch (IOException)
        {
            return false; // файл занят, попробуем позже
        }
        catch (Exception ex)
        {
            Fail(file, dir, "Не удалось прочитать файл: " + ex.Message);
            return true;
        }
    }

    void Fail(string file, string dir, string reason)
    {
        var failed = Path.Combine(dir, "failed");
        Directory.CreateDirectory(failed);
        var name = Path.GetFileName(file);
        var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}";
        File.Move(file, Path.Combine(failed, stamp), overwrite: true);
        File.WriteAllText(Path.Combine(failed, stamp + ".txt"), reason);
        LastMessage = $"{DateTime.Now:dd.MM HH:mm} — {name}: {reason}";
        LastFailed = true;
        log.LogWarning("Inbox: {File}: {Reason}", name, reason);
    }
}
