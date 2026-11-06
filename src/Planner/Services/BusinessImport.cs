using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Planner.Data;

namespace Planner.Services;

public record ParsedPriority(string Theme, int Priority);

/// <summary>
/// Файл бизнес-владельца: в нём нет ключей задач — только стратегия/тема и приоритет (например «Платежи — 1 приоритет»).
/// Импортируем темы, затем подбираем подходящие задачи из беклога по словам; совпадения можно подтвердить или снять.
/// </summary>
public class BusinessImport(IDbContextFactory<PlannerDb> dbf)
{
    static readonly Regex[] PriorityPatterns =
    [
        new(@"(\d)\s*[-–]?\s*(?:й|ый|ой|-й)?\s*приоритет", RegexOptions.IgnoreCase),
        new(@"приоритет\s*[:№#=\-–]?\s*(\d)", RegexOptions.IgnoreCase),
        new(@"priority\s*[:#=\-–]?\s*(\d)", RegexOptions.IgnoreCase),
        new(@"\bP(\d)\b"),
    ];

    static readonly string[] Stop =
    [
        "для", "при", "над", "под", "что", "как", "это", "его", "или", "the", "and", "for", "with",
        "приоритет", "priority", "задача", "задачи", "стратегия"
    ];

    // --- Разбор файла ---
    public List<ParsedPriority> Parse(Stream stream, string fileName)
    {
        var rows = fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)
            ? ReadXlsx(stream)
            : ReadText(stream);

        var result = new List<ParsedPriority>();
        foreach (var cells in rows)
        {
            var nonEmpty = cells.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            if (nonEmpty.Count == 0) continue;

            int? prio = null;
            string? prioCell = null;
            foreach (var c in nonEmpty)
            {
                prio = FindPriority(c);
                if (prio != null) { prioCell = c; break; }
            }
            // колонка вида «Приоритет | 1»: число в отдельной ячейке
            if (prio == null)
            {
                foreach (var c in nonEmpty)
                    if (Regex.IsMatch(c, @"^\d$") && int.Parse(c) is >= 1 and <= 9) { prio = int.Parse(c); prioCell = c; break; }
            }
            if (prio == null)
            {
                foreach (var c in nonEmpty)
                    if (WordPriority(c) is { } w) { prio = w; prioCell = c; break; }
            }
            if (prio == null) continue;

            var theme = nonEmpty.Where(c => c != prioCell && !Regex.IsMatch(c, @"^\d+$")).OrderByDescending(c => c.Length).FirstOrDefault();
            if (theme == null && prioCell != null)
                theme = PriorityPatterns.Aggregate(prioCell, (acc, r) => r.Replace(acc, " ")).Trim(' ', '-', '–', ':', ',', '.');
            if (string.IsNullOrWhiteSpace(theme) || Regex.IsMatch(theme, @"^(тема|стратегия|направление|priority|приоритет)$", RegexOptions.IgnoreCase)) continue;
            result.Add(new ParsedPriority(theme.Trim(), prio.Value));
        }
        return result;
    }

    static int? FindPriority(string cell)
    {
        foreach (var r in PriorityPatterns)
        {
            var m = r.Match(cell);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 9) return n;
        }
        return null;
    }

    static int? WordPriority(string cell)
    {
        var s = cell.Trim().ToLowerInvariant();
        return s switch
        {
            "высокий" or "high" or "must" or "critical" or "критичный" or "критический" => 1,
            "should" => 2,
            "средний" or "medium" => 3,
            "could" => 4,
            "низкий" or "low" => 5,
            _ => null
        };
    }

    static List<List<string>> ReadXlsx(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var rows = new List<List<string>>();
        foreach (var ws in wb.Worksheets)
        {
            foreach (var r in ws.RowsUsed())
                rows.Add(r.CellsUsed().Select(c => c.GetFormattedString()).ToList());
        }
        return rows;
    }

    static List<List<string>> ReadText(Stream stream)
    {
        using var sr = new StreamReader(stream);
        var rows = new List<List<string>>();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var delim = line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : ',';
            rows.Add(line.Split(delim).Select(c => c.Trim().Trim('"')).ToList());
        }
        return rows;
    }

    // --- Слова и совпадения ---
    public static List<string> Tokens(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_]+")
            .Select(m => m.Value)
            .Where(t => t.Length >= 3 && !Stop.Contains(t))
            .Select(Stem)
            .Distinct()
            .ToList();
    }

    // Грубое усечение окончаний: для русских слов хватает пяти первых букв
    static string Stem(string t) => t.Length > 5 ? t[..5] : t;

    static string Norm(string theme) => string.Join(' ', Regex.Matches(theme.ToLowerInvariant(), @"[\p{L}\p{N}_]+").Select(m => m.Value));

    /// <summary>
    /// Загрузка файла. Новая версия файла с тем же именем заменяет прежнюю, а решения человека
    /// (галочки у совпадений) сохраняются для тех же тем и задач.
    /// </summary>
    public async Task<int> ImportAsync(IEnumerable<ParsedPriority> parsed, string source)
    {
        await using var db = await dbf.CreateDbContextAsync();

        var old = await db.BusinessPriorities.Where(p => p.Source == source).ToListAsync();
        var oldIds = old.Select(o => o.Id).ToList();
        var oldLinks = await db.BusinessLinks.Where(l => oldIds.Contains(l.BusinessPriorityId)).ToListAsync();
        var decisions = new Dictionary<(string Theme, string Issue), bool>();
        foreach (var l in oldLinks)
        {
            var theme = old.First(o => o.Id == l.BusinessPriorityId).Theme;
            decisions[(Norm(theme), l.IssueKey)] = l.Confirmed;
        }
        db.BusinessLinks.RemoveRange(oldLinks);
        db.BusinessPriorities.RemoveRange(old);

        var added = new List<BusinessPriority>();
        foreach (var p in parsed)
        {
            var bp = new BusinessPriority
            {
                Theme = p.Theme, Priority = p.Priority, Source = source, UploadedUtc = DateTime.UtcNow,
                Keywords = string.Join(' ', Tokens(p.Theme)),
            };
            db.BusinessPriorities.Add(bp);
            added.Add(bp);
        }
        await db.SaveChangesAsync();
        await RematchAsync(db);

        if (decisions.Count > 0)
        {
            var ids = added.Select(a => a.Id).ToList();
            var links = await db.BusinessLinks.Where(l => ids.Contains(l.BusinessPriorityId)).ToListAsync();
            foreach (var l in links)
            {
                var theme = added.First(a => a.Id == l.BusinessPriorityId).Theme;
                if (decisions.TryGetValue((Norm(theme), l.IssueKey), out var was)) l.Confirmed = was;
            }
            await db.SaveChangesAsync();
        }
        return added.Count;
    }

    /// <summary>Подбирает задачи под темы заново. Решения человека (подтверждено/снято) сохраняются.</summary>
    public async Task RematchAsync(PlannerDb? existing = null)
    {
        var db = existing ?? await dbf.CreateDbContextAsync();
        try
        {
            var prios = await db.BusinessPriorities.ToListAsync();
            var issues = await db.Issues.Where(i => i.StatusCategory != "done" && i.MissingSinceUtc == null).ToListAsync();
            var current = await db.BusinessLinks.ToListAsync();
            var have = current.ToDictionary(l => (l.BusinessPriorityId, l.IssueKey));

            var issueTokens = issues.ToDictionary(i => i.Key, i => Tokens(i.Summary + " " + i.ParentSummary).ToHashSet());
            foreach (var p in prios)
            {
                var theme = p.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (theme.Count == 0) continue;
                foreach (var i in issues)
                {
                    var matched = theme.Count(issueTokens[i.Key].Contains);
                    var score = matched / (double)theme.Count;
                    var ok = matched >= 1 && (theme.Count <= 2 ? score >= 0.5 : matched >= 2 && score >= 0.34);
                    if (!ok) continue;
                    if (have.TryGetValue((p.Id, i.Key), out var l)) { l.Score = score; continue; }
                    db.BusinessLinks.Add(new BusinessLink
                    {
                        BusinessPriorityId = p.Id, IssueKey = i.Key, Score = score, Confirmed = score >= 0.5
                    });
                }
            }
            await db.SaveChangesAsync();
        }
        finally
        {
            if (existing == null) await db.DisposeAsync();
        }
    }

    public async Task ClearAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        db.BusinessLinks.RemoveRange(db.BusinessLinks);
        db.BusinessPriorities.RemoveRange(db.BusinessPriorities);
        await db.SaveChangesAsync();
    }

    public async Task SetConfirmedAsync(int linkId, bool confirmed)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var l = await db.BusinessLinks.FindAsync(linkId);
        if (l == null) return;
        l.Confirmed = confirmed;
        await db.SaveChangesAsync();
    }

    public async Task DeletePriorityAsync(int id)
    {
        await using var db = await dbf.CreateDbContextAsync();
        db.BusinessLinks.RemoveRange(db.BusinessLinks.Where(l => l.BusinessPriorityId == id));
        var p = await db.BusinessPriorities.FindAsync(id);
        if (p != null) db.BusinessPriorities.Remove(p);
        await db.SaveChangesAsync();
    }
}
