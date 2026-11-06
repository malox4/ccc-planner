using System.Text.RegularExpressions;
using Planner.Data;

namespace Planner.Services;

/// <param name="HasDocs">Есть вложение или описание — задачу можно брать.</param>
/// <param name="AnalysisComplete">Есть вложение или описание с критериями приёмки — анализ уже сделан.</param>
public record Readiness(bool CanTake, bool NoAnalysisNeeded, bool HasDocs, bool AnalysisComplete, List<string> Problems);

/// <summary>Правила: что можно брать в спринт, как считаем часы и WSJF.</summary>
public static class Rules
{
    // Задачи, где аналитик не нужен: получить доступ, узнать причину, получить креды и т.п.
    static readonly string[] NoAnalysisHints =
    [
        "доступ", "креды", "кредо", "credential", "access", "узнать", "выяснить", "причин", "account",
        "поднять", "уточнить", "получить методы", "получить данные"
    ];

    public static bool SuggestsNoAnalysis(string summary)
    {
        var s = summary.ToLowerInvariant();
        return NoAnalysisHints.Any(s.Contains);
    }

    public static bool IsNoAnalysisNeeded(Issue i, Plan? p) => p?.AnalysisNotNeeded ?? SuggestsNoAnalysis(i.Summary);

    // --- Определение описания и критериев приёмки по тексту ---
    static readonly Regex AcRegex = new(
        @"acceptance\s+criteria|критери\w*\s+приём\w*|критери\w*\s+прием\w*|\bдано\b[\s\S]{0,400}\bкогда\b[\s\S]{0,400}\bтогда\b|\bgiven\b[\s\S]{0,400}\bwhen\b[\s\S]{0,400}\bthen\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (bool hasDescription, bool hasAc) AnalyzeText(string summary, string text)
    {
        var t = (text ?? "").Trim();
        var hasDesc = t.Length >= 40 && !string.Equals(t, summary.Trim(), StringComparison.OrdinalIgnoreCase);
        return (hasDesc, hasDesc && AcRegex.IsMatch(t));
    }

    public static bool IsAnalysisComplete(Issue i, Plan? p) => p?.AnalysisDone ?? AnalysisComplete(i);

    // Статусы Jira, в которых задача ещё не вышла из анализа (аналитик ещё нужен для самого анализа).
    static readonly HashSet<string> AnalysisStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "Backlog", "К выполнению", "To Do", "Ready for Tech Analysis", "Analysis - Hold" };
    public static bool AnalysisPendingStatus(string status) => AnalysisStatuses.Contains(status);

    // Анализ идёт прямо сейчас (в отличие от Ready For Refinement / Hold / Backlog, где аналитика ещё не загружают).
    static readonly HashSet<string> ActiveAnalysisStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "Ready for Tech Analysis", "To Be Analyzed", "В работе", "In Progress" };
    public static bool ActiveAnalysisStatus(string status) => ActiveAnalysisStatuses.Contains(status);

    // Разработчик уже взял задачу: только с этого момента аналитик тратит ресурс на сопровождение.
    // Ready For Refinement / Ready for Development / К выполнению и т.п. — ещё не в работе, аналитика они не загружают.
    static readonly HashSet<string> DevStartedStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "В работе", "In Progress", "Ready for Devbox", "Тестирование", "Ready For Testing", "В процессе проверки" };
    public static bool DevStarted(string status) => DevStartedStatuses.Contains(status);

    // Взять в спринт можно с вложением или описанием; критерии приёмки не обязательны.
    static bool HasDocs(Issue i) => i.AttachmentCount > 0 || i.HasDescription == true;

    // Анализ считается сделанным, только если есть вложение или описание с критериями приёмки.
    // Описание без критериев: задачу взять можно, но аналитик нужен — написать критерии (его часы считаются).
    static bool AnalysisComplete(Issue i) => i.AttachmentCount > 0 || (i.HasDescription == true && i.HasAcceptanceCriteria == true);

    // --- Готовность к спринту ---
    /// <param name="analytical">Исполнитель в Jira — аналитик: задача не для разработки, а на уточнение/анализ, отдельный анализ не нужен.</param>
    public static Readiness Evaluate(Issue i, Plan? p, bool analytical = false)
    {
        var problems = new List<string>();
        var exempt = analytical || IsNoAnalysisNeeded(i, p);
        var hasDocs = HasDocs(i);

        var complete = AnalysisComplete(i);

        if (exempt) return new Readiness(true, true, hasDocs, complete, problems);

        if (i.HasDescription == null && i.AttachmentCount == 0)
            problems.Add("Нет данных об описании — нажмите «Синхронизировать».");
        else if (!hasDocs)
            problems.Add("Нет вложения и нет описания — анализа, скорее всего, нет.");
        if (string.IsNullOrEmpty(p?.Analyst))
            problems.Add("Не назначен аналитик (обязателен, если анализ нужен).");
        return new Readiness(problems.Count == 0, false, hasDocs, complete, problems);
    }

    // --- Часы ---
    public static double DevHours(Plan? p) => p?.ManualHours ?? p?.EstLikely ?? 0;

    /// <summary>Часы аналитика: нужны, только если анализ нужен и ещё не сделан. Введённые вручную важнее модели.</summary>
    public static double AnalysisHours(Issue i, Plan? p)
    {
        if (IsNoAnalysisNeeded(i, p)) return 0;
        var done = p?.AnalysisDone ?? AnalysisComplete(i);
        if (done) return 0;
        return AnalysisEffort(i, p);
    }

    /// <summary>
    /// Трудозатраты аналитика на анализ. Ожидание ответа внешней стороны часы не снижает: когда ответ придёт,
    /// начнётся основная работа (маппинг, новые методы, логика, созвоны), поэтому ёмкость под неё резервируется.
    /// </summary>
    public static double AnalysisEffort(Issue i, Plan? p) =>
        p?.ManualAnalysisHours ?? EstimateAnalysis(i, p?.WaitingOn, p?.ForIntegrators ?? false).Hours;

    // --- Модель часов анализа: не доля от разработки, а работа аналитика по содержанию задачи ---
    // Интеграции, уточнения у внешних сторон, маппинг данных и примеры запросов/ответов часто занимают больше времени, чем сама разработка.
    static Regex Rx(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Внешние системы, из-за которых анализ дороже. Список меняйте под свой домен: имя системы и регулярное выражение по тексту задачи.
    static readonly (string Name, Regex Rx)[] Systems =
    [
        ("платёжная система", Rx(@"payment|платёж|платеж|процессинг|эквайринг")),
        ("внешний партнёр", Rx(@"внешн|партн[её]р|вендор|vendor|third[- ]party")),
        ("интеграция", Rx(@"интеграц|integration|webhook|вебхук")),
    ];
    static readonly Regex ContractRx = Rx(@"маппинг|mapping|\bполя\b|поле|payload|request|response|запрос|ответ|json|endpoint|контракт|\bapi\b|пример");
    static readonly Regex ClarifyRx = Rx(@"ожида|жд[её]м|ответ от|уточн|запрос в|согласов|вопрос|эскалац");
    static readonly Regex DocsRx = Rx(@"confluence|документац|brd|frd|bpmn|to-?be|as-?is|\bтз\b|спецификац");

    public record AnalysisEstimate(double Hours, List<string> Parts, int Systems);

    // Задачи «документация TO-BE», «закрыть открытые вопросы», «BRD»: ~20 ч, число систем в тексте часы не растит.
    // Плавающая оценка: может закрыться предыдущим анализом или вырасти, если подниматься выше по стейкхолдерам.
    static readonly Regex DocTaskRx = Rx(@"документац|to-?be|as-?is|открыт\w*\s+вопрос|вопрос\w*\s+по\s+brd|\bbrd\b");
    public const double DocTaskHours = 20;

    static readonly Regex IntegratorsRx = Rx(@"интегратор|integrator");
    public const double IntegratorsHours = 12;

    public static AnalysisEstimate EstimateAnalysis(Issue i, string? waitingOn = null, bool forIntegrators = false)
    {
        var text = $"{i.Summary} {i.Description} {i.ParentSummary}";
        var parts = new List<string>();
        if (DocTaskRx.IsMatch(i.Summary))
        {
            parts.Add($"документация / закрытие вопросов ≈ {DocTaskHours:0} ч (плавающая: может закрыться прошлым анализом)");
            return new AnalysisEstimate(DocTaskHours, parts, 0);
        }
        double h = i.HasDescription == true ? 6 : 10;
        parts.Add($"база {h:0} ч ({(i.HasDescription == true ? "описание есть" : "описания нет")})");

        var sys = Systems.Where(s => s.Rx.IsMatch(text)).Select(s => s.Name).ToList();
        if (sys.Count > 0)
        {
            var add = 8 + Math.Min(sys.Count - 1, 3) * 3;
            h += add; parts.Add($"интеграция: {string.Join(", ", sys.Take(4))} +{add} ч");
        }
        var waiting = !string.IsNullOrWhiteSpace(waitingOn);
        // ждём внешнюю сторону: после ответа придут маппинг, новые методы и логика — закладываем их, даже если в тексте ещё не описаны
        if (waiting || ContractRx.IsMatch(text)) { h += 8; parts.Add("маппинг данных, новые методы, примеры запросов и ответов +8 ч"); }
        if (waiting) { h += 4; parts.Add($"созвоны и согласование с {waitingOn!.Trim()} после ответа +4 ч"); }
        if (ClarifyRx.IsMatch(text)) { h += 4; parts.Add("уточнения / ответы внешних сторон +4 ч"); }
        if (DocsRx.IsMatch(text)) { h += 4; parts.Add("документация (TO-BE, BRD, Confluence) +4 ч"); }
        if (forIntegrators || IntegratorsRx.IsMatch(text))
        {
            h += IntegratorsHours;
            parts.Add($"требования для команды интеграторов (API-контракт, сценарии, скрипты) +{IntegratorsHours:0} ч");
        }
        if (i.HasAcceptanceCriteria != true) { h += 4; parts.Add("критериев приёмки нет +4 ч"); }
        return new AnalysisEstimate(Math.Min(60, Math.Round(h)), parts, sys.Count);
    }

    public static int JobSize(double hours) =>
        hours <= 0 ? 0 : hours <= 4 ? 1 : hours <= 8 ? 2 : hours <= 16 ? 3 : hours <= 40 ? 5 : hours <= 80 ? 8 : 13;

    /// <summary>Сколько баллов Business Value добавляет приоритет бизнеса (1 — самый высокий).</summary>
    public static int BusinessBoost(int? priority) => priority switch
    {
        1 => 13, 2 => 8, 3 => 5, 4 => 3, 5 => 1, _ => 0
    };

    public static double? Wsjf(Plan? p, int boost)
    {
        if (p?.Bv == null || p.Tc == null || p.Rr == null) return null;
        var js = JobSize(DevHours(p));
        return js == 0 ? null : (p.Bv.Value + p.Tc.Value + p.Rr.Value + boost) / (double)js;
    }
}
