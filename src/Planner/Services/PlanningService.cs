using Microsoft.EntityFrameworkCore;
using Planner.Data;

namespace Planner.Services;

public class Row
{
    public Issue Issue { get; set; } = null!;
    public Plan Plan { get; set; } = null!;
    public Readiness Readiness { get; set; } = null!;
    public double Hours { get; set; }
    public double AnalysisHours { get; set; }
    /// <summary>Исполнитель в Jira — аналитик: задача не для разработки, а на уточнение/анализ.</summary>
    public bool IsAnalytical { get; set; }
    /// <summary>Из чего сложилась оценка анализа по модели.</summary>
    public List<string> AnalysisParts { get; set; } = [];
    /// <summary>Сопровождение аналитика по этой задаче, % от часов разработки (у интеграций больше).</summary>
    public double SupportPct { get; set; } = 15;
    /// <summary>Исполнитель — аналитик, анализ готов, разработчик ещё не назначен: часы разработки ни на ком, у аналитика только сопровождение.</summary>
    public bool WaitsForDeveloper { get; set; }
    /// <summary>У кого часы задачи: аналитика у аналитической задачи, иначе разработчик (Jira-исполнитель-разработчик или назначенный вручную).</summary>
    public string? Owner { get; set; }
    /// <summary>Задача в текущем спринте: по Jira или переведена в сервисе.</summary>
    public bool InCurrent { get; set; }
    /// <summary>Чего не хватает, чтобы перевести из следующего спринта в текущий (DoR, разработчик, оценка).</summary>
    public List<string> PromoteProblems { get; set; } = [];
    public int JobSize { get; set; }
    public double? Wsjf { get; set; }
    public int? BusinessPriority { get; set; }
    public string? BusinessTheme { get; set; }
    public string Key => Issue.Key;
}

public class Load
{
    public Person Person { get; set; } = null!;
    public double Hours { get; set; }
    public int OpenTasks { get; set; }
    public int DoneTasks { get; set; }
    public int NoEstimate { get; set; }
    /// <summary>Из Hours: анализ задач пред-спринта и текущего спринта, ещё не получивших анализ (у аналитика).</summary>
    public double AnalysisHours { get; set; }
    /// <summary>Из Hours: сопровождение задач в работе — вопросы, ошибки, уточнения (у аналитика).</summary>
    public double SupportHours { get; set; }
    /// <summary>Из Hours: часы закрытых в этом спринте задач (в нагрузке остаются).</summary>
    public double DoneHours { get; set; }
    /// <summary>Из Hours: работа вне текущего спринта (анализ в работе, сопровождение задач, взятых разработчиком вне спринта).</summary>
    public double OutsideHours { get; set; }
    public int OutsideTasks { get; set; }
    /// <summary>Из Hours: встречи, инциденты, root cause и бизнес-вопросы вне Jira.</summary>
    public double OverheadHours { get; set; }
    public double Free => Math.Max(0, Person.CapacityHours - Hours);
    public double Ratio => Person.CapacityHours > 0 ? Hours / Person.CapacityHours : 0;
}

public class Snapshot
{
    public List<Row> Rows { get; set; } = [];
    public List<Person> People { get; set; } = [];
    public double SupportPercent { get; set; } = 15;
    public Dictionary<string, List<Row>> Children => Rows.Where(r => r.Issue.ParentKey != null)
        .GroupBy(r => r.Issue.ParentKey!).ToDictionary(g => g.Key, g => g.ToList());

    public record Unowned(string Who, int Open, int Done, double Hours, List<Row> Rows);

    /// <summary>
    /// Работа текущего спринта, которая ни на ком из команды не лежит: задачи без исполнителя и задачи людей, которых нет
    /// в списке «Команда». Открытые и закрытые считаются вместе (нагрузка считается за спринт).
    /// </summary>
    public List<Unowned> CurrentUnowned()
    {
        var names = People.Select(p => p.Name).ToHashSet();
        var sprint = Rows.Where(r => r.InCurrent && r.Issue.MissingSinceUtc == null).ToList();
        var kids = sprint.Where(r => r.Issue.ParentKey != null).GroupBy(r => r.Issue.ParentKey!)
            .ToDictionary(g => g.Key, g => g.ToList());
        double Rest(Row r) => kids.TryGetValue(r.Key, out var ks) ? Math.Max(0, r.Hours - ks.Sum(k => k.Hours)) : r.Hours;
        return sprint.Where(r => r.Owner == null || !names.Contains(r.Owner))
            .GroupBy(r => r.Owner ?? "Без исполнителя")
            .Select(g => new Unowned(g.Key,
                g.Count(r => r.Issue.StatusCategory != "done" && !kids.ContainsKey(r.Key)),
                g.Count(r => r.Issue.StatusCategory == "done"),
                g.Sum(Rest),
                g.OrderByDescending(r => r.Hours).ToList()))
            .OrderByDescending(u => u.Hours).ToList();
    }

    /// <summary>Нагрузка на планируемый (следующий) спринт: по Plan.InSprint.</summary>
    public List<Load> PlannedLoad()
    {
        var planned = Rows.Where(r => r.Plan.InSprint).ToList();
        return People.Select(p =>
        {
            var l = new Load { Person = p, OverheadHours = p.OverheadHours, Hours = p.OverheadHours };
            foreach (var r in planned)
            {
                // анализ пред-спринта аналитик делает ДО спринта — он в нагрузке текущего периода (CurrentLoad);
                // на сам следующий спринт у аналитика остаётся сопровождение
                if (r.IsAnalytical && r.Plan.Analyst == p.Name) { l.Hours += r.Hours; l.OpenTasks++; }
                else if (r.Plan.Analyst == p.Name) { var s = r.Hours * r.SupportPct / 100; l.Hours += s; l.SupportHours += s; }
                if (r.Plan.Developer == p.Name) { l.Hours += r.Hours; l.OpenTasks++; }
            }
            return l;
        }).OrderBy(l => l.Ratio).ToList();
    }

    /// <summary>
    /// Нагрузка «сейчас»: открытые задачи текущего спринта Jira, где человек исполнитель, плюс для аналитика:
    /// сопровождение задач в работе (% от часов разработки) и анализ задач пред-спринта и тех в спринте, что ещё ждут анализа.
    /// </summary>
    public List<Load> CurrentLoad()
    {
        var sprint = Rows.Where(r => r.InCurrent && r.Issue.MissingSinceUtc == null).ToList();
        var kids = sprint.Where(r => r.Issue.ParentKey != null).GroupBy(r => r.Issue.ParentKey!)
            .ToDictionary(g => g.Key, g => g.ToList());
        return People.Select(p =>
        {
            var l = new Load { Person = p };
            // встречи, инциденты, root cause и бизнес-вопросы вне задач Jira — постоянная часть нагрузки спринта
            l.OverheadHours = p.OverheadHours; l.Hours += p.OverheadHours;

            foreach (var r in sprint.Where(r => r.Owner == p.Name))
            {
                // нагрузка считается за спринт целиком: закрытые задачи остаются в ней (часы уже потрачены)
                var done = r.Issue.StatusCategory == "done";
                var isContainer = kids.TryGetValue(r.Key, out var ks);
                // у сторис с подзадачами считаем подзадачи, у сторис — только неразложенный остаток
                var own = r.Hours;
                var rest = isContainer ? Math.Max(0, own - ks!.Sum(k => k.Hours)) : own;
                l.Hours += rest;
                if (done) { l.DoneTasks++; l.DoneHours += rest; }
                else if (!isContainer) { l.OpenTasks++; if (own == 0) l.NoEstimate++; }
            }

            // сопровождение: аналитик ведёт задачу, исполнитель в Jira — другой человек (разработчик)
            // и по задачам вне спринта, которые разработчик уже взял в работу — аналитик сопровождает их так же
            foreach (var r in Rows.Where(r => r.Issue.MissingSinceUtc == null && r.Plan.Analyst == p.Name && r.Owner != p.Name && !kids.ContainsKey(r.Key)
                         && ((r.InCurrent && (r.Issue.StatusCategory == "done" || Rules.DevStarted(r.Issue.Status)))
                             || (!r.InCurrent && r.Issue.StatusCategory != "done" && Rules.DevStarted(r.Issue.Status)))))
            {
                var s = r.Hours * r.SupportPct / 100;
                l.SupportHours += s; l.Hours += s;
                if (!r.InCurrent) l.OutsideHours += s;
            }

            // аналитические задачи, над которыми он работает вне спринта (его собственные уточнения и анализ в работе)
            foreach (var r in Rows.Where(r => r.Issue.MissingSinceUtc == null && !r.InCurrent && !r.Plan.InSprint && r.Issue.StatusCategory != "done"
                         && r.Owner == p.Name && r.IsAnalytical && Rules.ActiveAnalysisStatus(r.Issue.Status)))
            {
                l.AnalysisHours += r.Hours; l.OutsideHours += r.Hours; l.Hours += r.Hours; l.OutsideTasks++;
            }

            // анализ: пред-спринт (Plan.InSprint) и задачи текущего спринта, которые ещё не вышли из статусов анализа
            foreach (var r in Rows.Where(r => r.Plan.Analyst == p.Name && r.AnalysisHours > 0 && r.Issue.MissingSinceUtc == null
                         && r.Issue.StatusCategory != "done"
                         && (r.Plan.InSprint || (r.InCurrent && Rules.AnalysisPendingStatus(r.Issue.Status))
                             || (!r.InCurrent && Rules.ActiveAnalysisStatus(r.Issue.Status)))))
            {
                l.AnalysisHours += r.AnalysisHours; l.Hours += r.AnalysisHours;
                if (!r.InCurrent && !r.Plan.InSprint) { l.OutsideHours += r.AnalysisHours; l.OutsideTasks++; }
            }
            return l;
        }).OrderBy(l => l.Ratio).ToList();
    }
}

public class PlanningService(IDbContextFactory<PlannerDb> dbf, Microsoft.Extensions.Options.IOptions<PlannerOptions> opt)
{
    public async Task<Snapshot> LoadAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var issues = await db.Issues.AsNoTracking().ToListAsync();
        var plans = await db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Key);
        var people = await db.People.AsNoTracking().OrderBy(p => p.Role).ThenBy(p => p.Name).ToListAsync();
        var prios = await db.BusinessPriorities.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var links = await db.BusinessLinks.AsNoTracking().Where(l => l.Confirmed).ToListAsync();
        var bestByKey = links.GroupBy(l => l.IssueKey)
            .ToDictionary(g => g.Key, g => g.Where(l => prios.ContainsKey(l.BusinessPriorityId))
                .Select(l => prios[l.BusinessPriorityId]).OrderBy(p => p.Priority).FirstOrDefault());

        var roles = people.ToDictionary(x => x.Name, x => x.Role);
        var rows = issues.Select(i =>
        {
            plans.TryGetValue(i.Key, out var p);
            p ??= new Plan { Key = i.Key };
            bestByKey.TryGetValue(i.Key, out var bp);

            // исполнитель в Jira по умолчанию = разработчик; если исполнитель аналитик, задача аналитическая (уточнить), не для разработки
            var role = i.Assignee != null && roles.TryGetValue(i.Assignee, out var rl) ? rl : null;
            if (string.IsNullOrEmpty(p.Developer) && role == "dev") p.Developer = i.Assignee;
            if (string.IsNullOrEmpty(p.Analyst) && role == "analyst") p.Analyst = i.Assignee;
            // аналитик в исполнителях: если анализ ещё не сделан — это уточнение (аналитическая задача);
            // если анализ уже готов (документ, критерии) — аналитик ведёт задачу, а разработчика ещё нужно назначить
            var noDev = role == "analyst" && string.IsNullOrEmpty(p.Developer);
            var complete = Rules.IsAnalysisComplete(i, p);
            var analytical = noDev && !complete;
            var waits = noDev && complete;

            var est = Rules.EstimateAnalysis(i, p.WaitingOn, p.ForIntegrators);
            // у аналитической задачи (исполнитель аналитик, анализ не готов) часы — это работа аналитика, а не разработка
            var hours = analytical ? (p.ManualHours ?? Rules.AnalysisEffort(i, p)) : Rules.DevHours(p);
            var readiness = Rules.Evaluate(i, p, analytical);
            var promote = new List<string>();
            if (!readiness.CanTake) promote.AddRange(readiness.Problems);
            if (!analytical && string.IsNullOrEmpty(p.Developer)) promote.Add("не назначен разработчик");
            if (hours <= 0) promote.Add("нет оценки в часах");
            return new Row
            {
                InCurrent = i.InJiraSprint || p.InCurrent,
                PromoteProblems = promote,
                AnalysisParts = est.Parts,
                // интеграции требуют больше сопровождения: вопросы по контракту, ответы внешних сторон
                SupportPct = opt.Value.SupportPercent + (est.Systems > 0 ? 10 : 0),
                Issue = i,
                Plan = p,
                IsAnalytical = analytical,
                WaitsForDeveloper = waits,
                Owner = analytical ? i.Assignee : (string.IsNullOrEmpty(p.Developer) ? (role == null ? i.Assignee : null) : p.Developer),
                Readiness = readiness,
                Hours = hours,
                AnalysisHours = analytical ? 0 : Rules.AnalysisHours(i, p),
                JobSize = Rules.JobSize(hours),
                BusinessPriority = bp?.Priority,
                BusinessTheme = bp?.Theme,
                Wsjf = Rules.Wsjf(p, Rules.BusinessBoost(bp?.Priority)),
            };
        }).ToList();
        return new Snapshot { Rows = rows, People = people, SupportPercent = opt.Value.SupportPercent };
    }

    /// <summary>Изменить наши данные по задаче (оценки, назначения). Jira не трогаем.</summary>
    public async Task UpdatePlanAsync(string key, Action<Plan> change)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.Plans.FindAsync(key);
        if (p == null) { p = new Plan { Key = key }; db.Plans.Add(p); }
        change(p);
        p.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Положить задачи в следующий спринт (пред-спринт) или вернуть в беклог. Без блокировок: в следующий спринт
    /// можно класть что угодно, а DoR и ёмкость проверяются при переводе в текущий (PromoteAsync).
    /// Возвращает информационное сообщение (что ещё не готово) или null.
    /// </summary>
    public async Task<string?> SetManyInSprintAsync(IEnumerable<string> keys, bool on)
    {
        var list = keys.Distinct().ToList();
        if (list.Count == 0) return null;
        if (!on)
        {
            await using var db0 = await dbf.CreateDbContextAsync();
            foreach (var p in await db0.Plans.Where(p => list.Contains(p.Key)).ToListAsync())
            { p.InSprint = false; p.UpdatedUtc = DateTime.UtcNow; }
            await db0.SaveChangesAsync();
            return null;
        }

        var snap = await LoadAsync();
        var rows = snap.Rows.Where(r => list.Contains(r.Key)).ToList();
        var skipped = rows.Where(r => r.Issue.MissingSinceUtc != null || r.InCurrent).Select(r => r.Key).ToHashSet();
        var ok = rows.Where(r => !skipped.Contains(r.Key)).ToList();

        await using var db = await dbf.CreateDbContextAsync();
        foreach (var r in ok)
        {
            var p = await db.Plans.FindAsync(r.Key);
            if (p == null) { p = new Plan { Key = r.Key }; db.Plans.Add(p); }
            p.InSprint = true; p.UpdatedUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        var notes = new List<string>();
        if (skipped.Count > 0) notes.Add($"Пропущено {skipped.Count} (уже в текущем спринте или закрыты в Jira): {string.Join(", ", skipped)}.");
        var notReady = ok.Where(r => r.PromoteProblems.Count > 0).ToList();
        if (notReady.Count > 0)
            notes.Add("В следующем спринте, но пока не готовы к переводу в текущий: " +
                      string.Join("; ", notReady.Take(6).Select(r => $"{r.Key} ({string.Join(", ", r.PromoteProblems)})")) +
                      (notReady.Count > 6 ? $" и ещё {notReady.Count - 6}" : "") + ".");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    public Task<string?> SetInSprintAsync(string key, bool on) => SetManyInSprintAsync([key], on);

    public record PromoteReport(List<string> Moved, List<(string Key, string Why)> Stayed);

    /// <summary>
    /// Перевести задачи из следующего спринта в текущий. Переводятся только те, где выполнен DoR (описание или вложение,
    /// аналитик, разработчик, оценка) и хватает ёмкости разработчика и аналитика с учётом уже взятого. Остальные остаются
    /// в следующем спринте. Порядок: по WSJF.
    /// </summary>
    public async Task<PromoteReport> PromoteAsync(IEnumerable<string>? keys = null)
    {
        var only = keys?.ToHashSet();
        var snap = await LoadAsync();
        var planned = snap.Rows.Where(r => r.Plan.InSprint && r.Issue.MissingSinceUtc == null && (only == null || only.Contains(r.Key)))
            .OrderByDescending(r => r.Wsjf ?? -1).ThenBy(r => r.Key).ToList();

        var moved = new List<string>();
        var stayed = new List<(string, string)>();
        foreach (var r in planned)
        {
            var why = new List<string>(r.PromoteProblems);
            if (why.Count == 0)
            {
                // пробуем перевести и смотрим нагрузку: если кто-то превысит ёмкость, откатываем
                var before = snap.CurrentLoad().ToDictionary(x => x.Person.Name, x => x.Hours);
                r.Plan.InSprint = false; r.InCurrent = true;
                var loads = snap.CurrentLoad();
                foreach (var name in new[] { r.Owner, r.Plan.Analyst }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
                {
                    var l = loads.FirstOrDefault(x => x.Person.Name == name);
                    // блокируем только тех, кому перевод добавляет часы и выводит за ёмкость
                    if (l != null && l.Hours > l.Person.CapacityHours && l.Hours > before.GetValueOrDefault(name!) + 0.01)
                        why.Add($"перегруз у {name}: стало бы {l.Hours:0} из {l.Person.CapacityHours:0} ч");
                }
                if (why.Count > 0) { r.Plan.InSprint = true; r.InCurrent = false; }
            }
            if (why.Count == 0) moved.Add(r.Key); else stayed.Add((r.Key, string.Join(", ", why)));
        }

        if (moved.Count > 0)
        {
            await using var db = await dbf.CreateDbContextAsync();
            foreach (var p in await db.Plans.Where(p => moved.Contains(p.Key)).ToListAsync())
            { p.InSprint = false; p.InCurrent = true; p.UpdatedUtc = DateTime.UtcNow; }
            await db.SaveChangesAsync();
        }
        return new PromoteReport(moved, stayed);
    }

    /// <summary>Вернуть из текущего спринта (переведённого в сервисе) обратно в следующий.</summary>
    public async Task ReturnToNextAsync(string key)
    {
        await UpdatePlanAsync(key, p => { p.InCurrent = false; p.InSprint = true; });
    }

    public async Task SetOverheadAsync(string name, double hours)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.People.FindAsync(name);
        if (p == null) return;
        p.OverheadHours = Math.Max(0, hours);
        await db.SaveChangesAsync();
    }

    public async Task SetCapacityAsync(string name, double hours)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.People.FindAsync(name);
        if (p == null) return;
        p.CapacityHours = Math.Max(0, hours);
        await db.SaveChangesAsync();
    }

    public async Task AddPersonAsync(string name, string role, double hours)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        await using var db = await dbf.CreateDbContextAsync();
        if (await db.People.FindAsync(name) != null) return;
        db.People.Add(new Person { Name = name, Role = role, CapacityHours = hours });
        await db.SaveChangesAsync();
    }

    public async Task RemovePersonAsync(string name)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var p = await db.People.FindAsync(name);
        if (p == null) return;
        db.People.Remove(p);
        await db.SaveChangesAsync();
    }
}
