using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Planner.Data;

namespace Planner.Services;

/// <summary>Выгрузка всего планирования в один Excel-файл: задачи по блокам, нагрузка людей, команда, приоритеты бизнеса.</summary>
public class ExportService(PlanningService svc, IDbContextFactory<PlannerDb> dbf)
{
    public async Task<byte[]> BuildAsync()
    {
        var snap = await svc.LoadAsync();
        using var wb = new XLWorkbook();

        Tasks(wb, snap);
        Load(wb, snap);
        Team(wb, snap);
        await BusinessAsync(wb);
        Info(wb, snap);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    static string Block(Row r) =>
        r.Issue.MissingSinceUtc != null ? "Закрыто (нет в Jira)"
        : r.InCurrent ? "Текущий спринт"
        : r.Plan.InSprint ? "Следующий спринт"
        : "Беклог";

    static int BlockOrder(string b) => b switch { "Текущий спринт" => 0, "Следующий спринт" => 1, "Беклог" => 2, _ => 3 };

    static void Tasks(XLWorkbook wb, Snapshot snap)
    {
        var ws = wb.AddWorksheet("Задачи");
        string[] head =
        [
            "Блок", "Ключ", "Название", "Тип", "Статус Jira", "Родитель", "Исполнитель в Jira", "Разработчик", "Аналитик", "Роль задачи",
            "Часы разработки", "Часы анализа", "Сопровождение, %", "Job Size", "WSJF", "Business Value", "Time Criticality", "Risk Reduction",
            "Приоритет бизнеса", "Описание", "Критерии приёмки", "Вложений", "Анализ готов", "Ждём ответа от", "Для интеграторов",
            "Готово к текущему спринту", "Чего не хватает", "Основание оценки",
        ];
        for (var c = 0; c < head.Length; c++) ws.Cell(1, c + 1).Value = head[c];

        var rows = snap.Rows
            .Where(r => r.Issue.MissingSinceUtc != null || r.InCurrent || r.Plan.InSprint || r.Issue.StatusCategory != "done")
            .Select(r => (Row: r, Block: Block(r)))
            .OrderBy(x => BlockOrder(x.Block))
            .ThenBy(x => x.Row.Issue.StatusCategory == "done" ? 1 : 0)
            .ThenByDescending(x => x.Row.Wsjf ?? -1)
            .ThenBy(x => x.Row.Key)
            .ToList();

        var line = 2;
        foreach (var (r, block) in rows)
        {
            var i = r.Issue;
            var role = r.IsAnalytical ? "аналитическая" : r.WaitsForDeveloper ? "нужен разработчик" : "разработка";
            var problems = r.PromoteProblems.Count > 0 ? string.Join("; ", r.PromoteProblems) : "";
            object?[] vals =
            [
                block, i.Key, i.Summary, i.Type, i.MissingSinceUtc != null ? i.LastKnownStatus : i.Status, i.ParentKey, i.Assignee,
                r.Plan.Developer, r.Plan.Analyst, role,
                r.Hours > 0 ? r.Hours : null, r.AnalysisHours > 0 ? r.AnalysisHours : null, r.SupportPct, r.JobSize > 0 ? r.JobSize : null, r.Wsjf,
                r.Plan.Bv, r.Plan.Tc, r.Plan.Rr, r.BusinessPriority, Yn(i.HasDescription), Yn(i.HasAcceptanceCriteria), i.AttachmentCount,
                Yn(r.Readiness.AnalysisComplete), r.Plan.WaitingOn, r.Plan.ForIntegrators ? "да" : "",
                r.PromoteProblems.Count == 0 ? "да" : "нет", problems, r.Plan.EstBasis,
            ];
            for (var c = 0; c < vals.Length; c++)
            {
                var cell = ws.Cell(line, c + 1);
                switch (vals[c])
                {
                    case null: break;
                    case double d: cell.Value = d; break;
                    case int n: cell.Value = n; break;
                    default: cell.Value = vals[c]!.ToString(); break;
                }
            }
            if (block == "Закрыто (нет в Jira)" || i.StatusCategory == "done") ws.Row(line).Style.Font.FontColor = XLColor.Gray;
            line++;
        }

        Style(ws, head.Length, line - 1, [10, 10, 48, 11, 18, 10, 22, 22, 22, 16, 11, 11, 12, 9, 8, 10, 10, 10, 11, 10, 10, 9, 11, 16, 12, 14, 44, 50]);
        ws.Column(15).Style.NumberFormat.Format = "0.0";
        ws.Column(13).Style.NumberFormat.Format = "0";
    }

    static string Yn(bool? v) => v == null ? "" : v == true ? "да" : "нет";

    static void Load(XLWorkbook wb, Snapshot snap)
    {
        var ws = wb.AddWorksheet("Нагрузка");
        string[] head =
        [
            "Роль", "Человек", "Ёмкость, ч", "Нагрузка сейчас, ч", "Свободно, ч", "Загрузка, %", "Открытых", "Закрытых", "Закрыто, ч",
            "Встречи и инциденты, ч", "Анализ, ч", "Из них вне спринта, ч", "Сопровождение, ч",
        ];
        for (var c = 0; c < head.Length; c++) ws.Cell(1, c + 1).Value = head[c];
        var line = 2;
        foreach (var l in snap.CurrentLoad().OrderBy(l => l.Person.Role).ThenByDescending(l => l.Ratio))
        {
            object[] vals =
            [
                l.Person.Role == "analyst" ? "аналитик" : "разработчик", l.Person.Name, l.Person.CapacityHours, Math.Round(l.Hours, 1),
                Math.Round(l.Person.CapacityHours - l.Hours, 1), Math.Round(l.Ratio * 100), l.OpenTasks, l.DoneTasks, Math.Round(l.DoneHours, 1),
                l.OverheadHours, Math.Round(l.AnalysisHours, 1), Math.Round(l.OutsideHours, 1), Math.Round(l.SupportHours, 1),
            ];
            for (var c = 0; c < vals.Length; c++)
            {
                if (vals[c] is double d) ws.Cell(line, c + 1).Value = d;
                else if (vals[c] is int n) ws.Cell(line, c + 1).Value = n;
                else ws.Cell(line, c + 1).Value = vals[c].ToString();
            }
            if (l.Ratio >= 1) ws.Cell(line, 6).Style.Font.FontColor = XLColor.Red;
            line++;
        }
        Style(ws, head.Length, line - 1, [12, 26, 11, 14, 12, 11, 10, 10, 11, 14, 11, 14, 14]);

        // следующий спринт и работа вне команды ниже на том же листе
        line += 2;
        ws.Cell(line, 1).Value = "Следующий спринт (план)"; ws.Cell(line, 1).Style.Font.Bold = true;
        line++;
        foreach (var (h, c) in new[] { "Человек", "Ёмкость, ч", "Нагрузка, ч", "Свободно, ч", "Задач" }.Select((h, c) => (h, c)))
        { ws.Cell(line, c + 2).Value = h; ws.Cell(line, c + 2).Style.Font.Bold = true; }
        line++;
        foreach (var l in snap.PlannedLoad().OrderBy(l => l.Person.Role).ThenBy(l => l.Person.Name))
        {
            ws.Cell(line, 2).Value = l.Person.Name;
            ws.Cell(line, 3).Value = l.Person.CapacityHours;
            ws.Cell(line, 4).Value = Math.Round(l.Hours, 1);
            ws.Cell(line, 5).Value = Math.Round(l.Person.CapacityHours - l.Hours, 1);
            ws.Cell(line, 6).Value = l.OpenTasks;
            line++;
        }

        var un = snap.CurrentUnowned();
        if (un.Count > 0)
        {
            line += 2;
            ws.Cell(line, 1).Value = "Работа вне команды (без исполнителя или не из списка команды)"; ws.Cell(line, 1).Style.Font.Bold = true;
            line++;
            foreach (var (h, c) in new[] { "Кто", "Открытых", "Закрытых", "Часов" }.Select((h, c) => (h, c)))
            { ws.Cell(line, c + 2).Value = h; ws.Cell(line, c + 2).Style.Font.Bold = true; }
            line++;
            foreach (var u in un)
            {
                ws.Cell(line, 2).Value = u.Who; ws.Cell(line, 3).Value = u.Open; ws.Cell(line, 4).Value = u.Done; ws.Cell(line, 5).Value = Math.Round(u.Hours, 1);
                line++;
            }
        }
    }

    static void Team(XLWorkbook wb, Snapshot snap)
    {
        var ws = wb.AddWorksheet("Команда");
        string[] head = ["Имя", "Роль", "Ёмкость на спринт, ч", "Встречи и инциденты, ч"];
        for (var c = 0; c < head.Length; c++) ws.Cell(1, c + 1).Value = head[c];
        var line = 2;
        foreach (var p in snap.People)
        {
            ws.Cell(line, 1).Value = p.Name;
            ws.Cell(line, 2).Value = p.Role == "analyst" ? "аналитик" : "разработчик";
            ws.Cell(line, 3).Value = p.CapacityHours;
            ws.Cell(line, 4).Value = p.OverheadHours;
            line++;
        }
        Style(ws, head.Length, line - 1, [28, 14, 20, 22]);
    }

    async Task BusinessAsync(XLWorkbook wb)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var prios = await db.BusinessPriorities.AsNoTracking().OrderBy(p => p.Priority).ThenBy(p => p.Theme).ToListAsync();
        if (prios.Count == 0) return;
        var links = await db.BusinessLinks.AsNoTracking().ToListAsync();
        var issues = await db.Issues.AsNoTracking().ToDictionaryAsync(i => i.Key);

        var ws = wb.AddWorksheet("Приоритеты бизнеса");
        string[] head = ["Приоритет", "Тема", "Источник", "Ключ задачи", "Название задачи", "Совпадение, %", "Подтверждено"];
        for (var c = 0; c < head.Length; c++) ws.Cell(1, c + 1).Value = head[c];
        var line = 2;
        foreach (var p in prios)
        {
            var pl = links.Where(l => l.BusinessPriorityId == p.Id).OrderByDescending(l => l.Score).ToList();
            if (pl.Count == 0)
            {
                ws.Cell(line, 1).Value = p.Priority; ws.Cell(line, 2).Value = p.Theme; ws.Cell(line, 3).Value = p.Source ?? "";
                line++;
                continue;
            }
            foreach (var l in pl)
            {
                ws.Cell(line, 1).Value = p.Priority; ws.Cell(line, 2).Value = p.Theme; ws.Cell(line, 3).Value = p.Source ?? "";
                ws.Cell(line, 4).Value = l.IssueKey;
                ws.Cell(line, 5).Value = issues.TryGetValue(l.IssueKey, out var i) ? i.Summary : "";
                ws.Cell(line, 6).Value = Math.Round(l.Score * 100);
                ws.Cell(line, 7).Value = l.Confirmed ? "да" : "нет";
                line++;
            }
        }
        Style(ws, head.Length, line - 1, [10, 40, 24, 12, 52, 14, 14]);
    }

    static void Info(XLWorkbook wb, Snapshot snap)
    {
        var ws = wb.AddWorksheet("О выгрузке");
        ws.Cell(1, 1).Value = "CCC Planner: выгрузка планирования"; ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = "Выгружено"; ws.Cell(2, 2).Value = Fmt.Local(DateTime.UtcNow).ToString("dd.MM.yyyy HH:mm");
        ws.Cell(3, 1).Value = "Сопровождение аналитика, % от часов разработки"; ws.Cell(3, 2).Value = snap.SupportPercent;
        ws.Cell(5, 1).Value = "Листы"; ws.Cell(5, 1).Style.Font.Bold = true;
        ws.Cell(6, 1).Value = "Задачи"; ws.Cell(6, 2).Value = "все задачи по блокам: текущий спринт, следующий спринт, беклог; закрытые в спринте остаются";
        ws.Cell(7, 1).Value = "Нагрузка"; ws.Cell(7, 2).Value = "нагрузка людей сейчас, план следующего спринта и работа вне команды";
        ws.Cell(8, 1).Value = "Команда"; ws.Cell(8, 2).Value = "ёмкость и часы на встречи и инциденты";
        ws.Cell(9, 1).Value = "Приоритеты бизнеса"; ws.Cell(9, 2).Value = "темы из файла заказчика и подобранные задачи (если файл загружали)";
        ws.Cell(11, 1).Value = "Часы приблизительные: введённые командой вручную главнее оценок сервиса.";
        ws.Column(1).Width = 44; ws.Column(2).Width = 90;
    }

    static void Style(IXLWorksheet ws, int cols, int lastRow, int[] widths)
    {
        var head = ws.Range(1, 1, 1, cols);
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = XLColor.FromHtml("#DEEBFF");
        head.Style.Alignment.WrapText = true;
        head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        for (var c = 0; c < cols && c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        ws.SheetView.FreezeRows(1);
        if (lastRow >= 2) ws.Range(1, 1, lastRow, cols).SetAutoFilter();
    }
}
