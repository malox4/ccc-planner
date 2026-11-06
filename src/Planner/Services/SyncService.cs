using Microsoft.EntityFrameworkCore;
using Planner.Data;

namespace Planner.Services;

/// <summary>
/// Кнопка «Синхронизировать»: тянет новые задачи и статусы текущих. Задача, которая пропала из выборки Jira,
/// считается закрытой — в сервисе она остаётся (с датой исчезновения и последним известным статусом).
/// </summary>
public class SyncService(IDbContextFactory<PlannerDb> dbf, JiraClient jira, Microsoft.Extensions.Options.IOptions<JiraOptions> opt, AppState state)
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<SyncRun> RunAsync(CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
            return new SyncRun { StartedUtc = DateTime.UtcNow, Error = "Синхронизация уже идёт." };

        state.SetSyncing(true);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var run = new SyncRun { StartedUtc = DateTime.UtcNow };
        db.SyncRuns.Add(run);
        try
        {
            if (!jira.IsConfigured)
                throw new InvalidOperationException("Jira не настроена: задайте Jira__BaseUrl, Jira__Email и Jira__ApiToken в .env и перезапустите контейнер.");

            var jql = string.IsNullOrWhiteSpace(opt.Value.Jql) ? $"project = {opt.Value.ProjectKey} AND (statusCategory != Done OR sprint in openSprints()) ORDER BY key DESC" : opt.Value.Jql;
            var sprintJql = string.IsNullOrWhiteSpace(opt.Value.SprintJql) ? $"project = {opt.Value.ProjectKey} AND sprint in openSprints()" : opt.Value.SprintJql;

            // клонированные задачи («CLONE …» в заголовке) в планирование не берём
            static bool IsClone(string s) => s.TrimStart().StartsWith("CLONE", StringComparison.OrdinalIgnoreCase);
            var fetched = (await jira.SearchAsync(jql, ct)).Where(x => !IsClone(x.Summary)).ToList();
            var inSprint = (await jira.SearchAsync(sprintJql, ct, keysOnly: true)).Select(x => x.Key).ToHashSet();

            var existing = await db.Issues.ToDictionaryAsync(i => i.Key, ct);
            // защита: пустой ответ при непустой базе — это, скорее всего, сбой, а не «все закрыты»
            if (fetched.Count == 0 && existing.Values.Any(i => i.MissingSinceUtc == null))
                throw new InvalidOperationException("Jira вернула 0 задач — пропавшими ничего не помечаю. Проверьте JQL и права токена.");

            var plans = await db.Plans.ToDictionaryAsync(p => p.Key, ct);
            var now = DateTime.UtcNow;
            var seen = new HashSet<string>();
            foreach (var d in fetched)
            {
                seen.Add(d.Key);
                var (hasDesc, hasAc) = Rules.AnalyzeText(d.Summary, d.DescriptionText);
                var isNew = !existing.TryGetValue(d.Key, out var i);
                if (isNew)
                {
                    i = new Issue();
                    i.Key = d.Key; i.FirstSeenUtc = now;
                    db.Issues.Add(i);
                    existing[d.Key] = i;
                    run.Added++;
                }
                else
                {
                    if (i.Status != d.Status)
                        db.StatusChanges.Add(new StatusChange { Key = d.Key, From = i.Status, To = d.Status, AtUtc = now });
                    if (i.MissingSinceUtc != null)
                    {
                        run.Returned++;
                        i.MissingSinceUtc = null;
                        i.LastKnownStatus = null;
                    }
                    run.Updated++;
                }

                // критерии приёмки появились после оценки — оценку часов разработки надо пересмотреть
                if (!isNew && i.HasAcceptanceCriteria != true && hasAc)
                {
                    if (!plans.TryGetValue(d.Key, out var pl)) { pl = new Plan { Key = d.Key, UpdatedUtc = now }; db.Plans.Add(pl); plans[d.Key] = pl; }
                    if (pl.EstQuality != "ac") pl.NeedsReestimate = true;
                }

                i.Summary = d.Summary;
                i.Type = d.Type;
                i.Status = d.Status;
                i.StatusCategory = d.StatusCategory;
                i.Priority = d.Priority;
                i.Assignee = d.Assignee;
                i.ParentKey = d.ParentKey;
                i.ParentSummary = d.ParentSummary;
                i.Description = d.DescriptionText;
                i.HasDescription = hasDesc;
                i.HasAcceptanceCriteria = hasAc;
                i.AttachmentCount = d.AttachmentCount;
                i.InJiraSprint = inSprint.Contains(d.Key);
                // Jira уже отражает перевод в текущий спринт — локальная пометка больше не нужна
                if (i.InJiraSprint && plans.TryGetValue(d.Key, out var local) && (local.InCurrent || local.InSprint))
                { local.InCurrent = false; local.InSprint = false; }
                i.JiraUpdatedUtc = d.UpdatedUtc;
                i.LastSeenUtc = now;
            }

            // Вне доски: клоны и подзадачи/дефекты/тест-кейсы вне спринта (так обычно фильтрует доска) — это не «закрытые»,
            // поэтому убираем их из сервиса, если они не были в статусе «Готово».
            static bool OffBoardType(string t) => t is "Подзадача" or "Sub-task" or "Дефект" or "Defect" or "Test-Cases";
            foreach (var c in existing.Values.Where(i => !seen.Contains(i.Key) &&
                         (IsClone(i.Summary) || (OffBoardType(i.Type) && i.StatusCategory != "done"))).ToList())
            {
                db.Plans.RemoveRange(db.Plans.Where(p => p.Key == c.Key));
                db.BusinessLinks.RemoveRange(db.BusinessLinks.Where(l => l.IssueKey == c.Key));
                db.Issues.Remove(c);
                existing.Remove(c.Key);
            }

            foreach (var i in existing.Values.Where(i => !seen.Contains(i.Key) && i.MissingSinceUtc == null))
            {
                i.MissingSinceUtc = now;
                i.LastKnownStatus = i.Status;
                run.Disappeared++;
            }

            run.Total = fetched.Count;
            run.FinishedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
            run.FinishedUtc = DateTime.UtcNow;
            db.ChangeTracker.Clear();
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            Gate.Release();
            state.SetSyncing(false);
        }
        state.SetLastRun(run);
        return run;
    }
}
