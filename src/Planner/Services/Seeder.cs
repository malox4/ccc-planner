using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planner.Data;

namespace Planner.Services;

/// <summary>Первый запуск: создаёт схему, людей и грузит демонстрационные данные, пока не настроена и не нажата «Синхронизировать».</summary>
public static class Seeder
{
    class SeedFile
    {
        public List<SeedIssue> Issues { get; set; } = [];
        public List<SeedPlan> Plans { get; set; } = [];
    }

    class SeedIssue
    {
        public string Key { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Type { get; set; } = "Task";
        public string Status { get; set; } = "";
        public string StatusCategory { get; set; } = "new";
        public string? Priority { get; set; }
        public string? Assignee { get; set; }
        public string? ParentKey { get; set; }
        public bool InJiraSprint { get; set; }
        public bool? HasDescription { get; set; }
        public bool? HasAcceptanceCriteria { get; set; }
        public int AttachmentCount { get; set; }
    }

    class SeedPlan
    {
        public string Key { get; set; } = "";
        public int? Bv { get; set; }
        public int? Tc { get; set; }
        public int? Rr { get; set; }
        public double? ManualHours { get; set; }
        public double? EstMin { get; set; }
        public double? EstLikely { get; set; }
        public double? EstMax { get; set; }
        public string? EstQuality { get; set; }
        public string? EstBasis { get; set; }
        public string? Analyst { get; set; }
        public string? Developer { get; set; }
        public bool? AnalysisDone { get; set; }
    }

    static readonly string[] Developers =
        ["Dev One", "Dev Two", "Dev Three", "Dev Four", "Dev Five"];
    static readonly string[] Analysts = ["Analyst Anna", "Product Owner Pavel", "Analyst Alex"];

    public static async Task InitAsync(IServiceProvider sp)
    {
        var dbf = sp.GetRequiredService<IDbContextFactory<PlannerDb>>();
        var opt = sp.GetRequiredService<IOptions<PlannerOptions>>().Value;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await dbf.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                // EnsureCreated не добавляет колонки в уже созданные таблицы
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Plans\" ADD COLUMN IF NOT EXISTS \"NeedsReestimate\" boolean NOT NULL DEFAULT false");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Plans\" ADD COLUMN IF NOT EXISTS \"ManualAnalysisHours\" double precision NULL");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Plans\" ADD COLUMN IF NOT EXISTS \"WaitingOn\" text NULL");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Plans\" ADD COLUMN IF NOT EXISTS \"ForIntegrators\" boolean NOT NULL DEFAULT false");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Plans\" ADD COLUMN IF NOT EXISTS \"InCurrent\" boolean NOT NULL DEFAULT false");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"People\" ADD COLUMN IF NOT EXISTS \"OverheadHours\" double precision NOT NULL DEFAULT 0");

                if (!await db.People.AnyAsync())
                {
                    // Product Owner занят в проекте на 50% — ёмкость вдвое меньше стандартной (имена демонстрационные: поменяйте на вкладке «Команда»)
                    foreach (var n in Developers)
                        db.People.Add(new Person { Name = n, Role = "dev", CapacityHours = opt.DefaultCapacityHours });
                    foreach (var n in Analysts)
                        db.People.Add(new Person
                        {
                            Name = n, Role = "analyst",
                            CapacityHours = n == "Product Owner Pavel" ? opt.DefaultCapacityHours / 2 : opt.DefaultCapacityHours
                        });
                }

                if (!await db.Issues.AnyAsync())
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "Seed", "seed.json");
                    if (File.Exists(path))
                    {
                        var seed = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(path),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        var now = DateTime.UtcNow;
                        foreach (var s in seed.Issues)
                            db.Issues.Add(new Issue
                            {
                                Key = s.Key, Summary = s.Summary, Type = s.Type, Status = s.Status, StatusCategory = s.StatusCategory,
                                Priority = s.Priority, Assignee = s.Assignee, ParentKey = s.ParentKey, InJiraSprint = s.InJiraSprint,
                                HasDescription = s.HasDescription, HasAcceptanceCriteria = s.HasAcceptanceCriteria,
                                AttachmentCount = s.AttachmentCount, FirstSeenUtc = now, LastSeenUtc = now,
                            });
                        foreach (var s in seed.Plans)
                            db.Plans.Add(new Plan
                            {
                                Key = s.Key, Bv = s.Bv, Tc = s.Tc, Rr = s.Rr, ManualHours = s.ManualHours,
                                EstMin = s.EstMin, EstLikely = s.EstLikely, EstMax = s.EstMax, EstQuality = s.EstQuality,
                                EstBasis = s.EstBasis, Analyst = s.Analyst, Developer = s.Developer, AnalysisDone = s.AnalysisDone,
                                UpdatedUtc = now,
                            });
                        log.LogInformation("Seed: {I} задач, {P} планов", seed.Issues.Count, seed.Plans.Count);
                    }
                }
                await db.SaveChangesAsync();
                return;
            }
            catch (Exception ex) when (attempt < 30)
            {
                log.LogWarning("База ещё не готова ({Msg}), попытка {N}/30", ex.Message, attempt);
                await Task.Delay(2000);
            }
        }
    }
}
