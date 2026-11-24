using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Planner.Services;

public record JiraIssueDto(
    string Key, string Summary, string Type, string Status, string StatusCategory, string? Priority,
    string? Assignee, string? ParentKey, string? ParentSummary, string DescriptionText, int AttachmentCount,
    DateTime? UpdatedUtc);

public record JiraQueries(string Jql, string SprintJql, string Source);

/// <summary>Только чтение из Jira Cloud (REST v3 и Agile). Ничего в Jira не пишет. Подключение берётся из вкладки «Подключение».</summary>
public class JiraClient(HttpClient http, JiraSettingsService settings)
{
    static readonly string[] Fields =
    [
        "summary", "issuetype", "status", "priority", "assignee", "parent", "description", "attachment", "updated"
    ];

    public async Task<bool> IsConfiguredAsync() => (await settings.GetAsync()).IsComplete;

    async Task<(int Status, string Text)> SendAsync(JiraConfig cfg, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, new Uri(cfg.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/')));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.Email}:{cfg.Token}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) req.Content = JsonContent.Create(body);
        using var res = await http.SendAsync(req, ct);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    static string Explain(int status, string text) => status switch
    {
        401 => "Jira не приняла email или токен (401). Проверьте email и создайте токен заново.",
        403 => "Нет доступа (403): у вашей учётной записи нет прав на этот проект или доску.",
        404 => "Не найдено (404): проверьте ссылку на доску и адрес Jira.",
        _ => $"Jira ответила {status}: {Trunc(text)}",
    };

    public Task<List<JiraIssueDto>> SearchAsync(string jql, CancellationToken ct, bool keysOnly = false) => SearchAsync(null, jql, ct, keysOnly);

    public async Task<List<JiraIssueDto>> SearchAsync(JiraConfig? cfg, string jql, CancellationToken ct, bool keysOnly = false)
    {
        cfg ??= await settings.GetAsync();
        var result = new List<JiraIssueDto>();
        string? pageToken = null;
        do
        {
            var body = new Dictionary<string, object?>
            {
                ["jql"] = jql,
                ["maxResults"] = 100,
                ["fields"] = keysOnly ? new[] { "summary" } : Fields,
            };
            if (pageToken != null) body["nextPageToken"] = pageToken;

            var (status, text) = await SendAsync(cfg, HttpMethod.Post, "rest/api/3/search/jql", body, ct);
            if (status is < 200 or >= 300) throw new InvalidOperationException(Explain(status, text));

            using var doc = JsonDocument.Parse(text);
            foreach (var i in doc.RootElement.GetProperty("issues").EnumerateArray())
                result.Add(Parse(i));

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
        } while (pageToken != null);
        return result;
    }

    /// <summary>Кто вы в Jira (проверка email и токена).</summary>
    public async Task<(string Name, string? Email)> MyselfAsync(JiraConfig cfg, CancellationToken ct)
    {
        var (status, text) = await SendAsync(cfg, HttpMethod.Get, "rest/api/3/myself", null, ct);
        if (status is < 200 or >= 300) throw new InvalidOperationException(Explain(status, text));
        using var doc = JsonDocument.Parse(text);
        return (Str(doc.RootElement, "displayName") ?? "?", Str(doc.RootElement, "emailAddress"));
    }

    /// <summary>Название доски и JQL её фильтра (то, что показывает доска). Null, если доска не указана или недоступна.</summary>
    public async Task<(string Name, string? Jql)?> BoardAsync(JiraConfig cfg, CancellationToken ct)
    {
        if (cfg.BoardId is not { } id) return null;
        var (s1, t1) = await SendAsync(cfg, HttpMethod.Get, $"rest/agile/1.0/board/{id}", null, ct);
        if (s1 is < 200 or >= 300) throw new InvalidOperationException("Доска: " + Explain(s1, t1));
        string name;
        using (var d1 = JsonDocument.Parse(t1)) name = Str(d1.RootElement, "name") ?? $"доска {id}";

        string? jql = null;
        var (s2, t2) = await SendAsync(cfg, HttpMethod.Get, $"rest/agile/1.0/board/{id}/configuration", null, ct);
        if (s2 is >= 200 and < 300)
        {
            using var d2 = JsonDocument.Parse(t2);
            if (d2.RootElement.TryGetProperty("filter", out var f) && f.TryGetProperty("id", out var fid))
            {
                var (s3, t3) = await SendAsync(cfg, HttpMethod.Get, $"rest/api/3/filter/{fid.GetString()}", null, ct);
                if (s3 is >= 200 and < 300)
                {
                    using var d3 = JsonDocument.Parse(t3);
                    jql = Str(d3.RootElement, "jql");
                }
            }
        }
        return (name, jql);
    }

    static string StripOrder(string jql) => Regex.Replace(jql, @"\s+ORDER\s+BY\s+.*$", "", RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();

    /// <summary>Какие запросы выполнять при синхронизации: свои из .env, фильтр доски или весь проект.</summary>
    public async Task<JiraQueries> ResolveQueriesAsync(JiraConfig cfg, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Jql))
            return new JiraQueries(cfg.Jql!, cfg.SprintJql ?? $"({StripOrder(cfg.Jql!)}) AND sprint in openSprints()", "свой JQL из настроек (.env)");

        if (cfg.BoardId != null)
        {
            var board = await BoardAsync(cfg, ct);
            if (board?.Jql is { Length: > 0 } baseJql)
            {
                var b = StripOrder(baseJql);
                return new JiraQueries(
                    $"({b}) AND (statusCategory != Done OR sprint in openSprints()) ORDER BY key DESC",
                    $"({b}) AND sprint in openSprints()",
                    $"фильтр доски «{board.Value.Name}»");
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.ProjectKey))
            throw new InvalidOperationException("Укажите ссылку на доску Jira (во вкладке «Подключение») или ключ проекта.");
        return new JiraQueries(
            $"project = {cfg.ProjectKey} AND (statusCategory != Done OR sprint in openSprints()) ORDER BY key DESC",
            $"project = {cfg.ProjectKey} AND sprint in openSprints()",
            $"весь проект {cfg.ProjectKey}");
    }

    public async Task<int?> CountAsync(JiraConfig cfg, string jql, CancellationToken ct)
    {
        var (status, text) = await SendAsync(cfg, HttpMethod.Post, "rest/api/3/search/approximate-count", new { jql = StripOrder(jql) }, ct);
        if (status is < 200 or >= 300) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : null;
    }

    static string Trunc(string s) => s.Length <= 300 ? s : s[..300] + "…";

    static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    static JiraIssueDto Parse(JsonElement i)
    {
        var f = i.GetProperty("fields");
        var desc = f.TryGetProperty("description", out var d) ? DescriptionToText(d) : "";
        var attach = f.TryGetProperty("attachment", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;
        DateTime? updated = null;
        if (Str(f, "updated") is { } u && DateTimeOffset.TryParse(u, out var dto)) updated = dto.UtcDateTime;
        return new JiraIssueDto(
            i.GetProperty("key").GetString()!,
            Str(f, "summary") ?? "",
            Str(f, "issuetype", "name") ?? "Task",
            Str(f, "status", "name") ?? "",
            Str(f, "status", "statusCategory", "key") ?? "new",
            Str(f, "priority", "name"),
            Str(f, "assignee", "displayName"),
            Str(f, "parent", "key"),
            Str(f, "parent", "fields", "summary"),
            desc, attach, updated);
    }

    // Описание в Jira Cloud приходит в формате ADF (дерево узлов) — разворачиваем в обычный текст.
    static string DescriptionToText(JsonElement d)
    {
        if (d.ValueKind == JsonValueKind.String) return d.GetString() ?? "";
        if (d.ValueKind != JsonValueKind.Object) return "";
        var sb = new StringBuilder();
        Walk(d, sb);
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    static readonly HashSet<string> BlockNodes = ["paragraph", "heading", "listItem", "tableRow", "codeBlock", "blockquote", "rule"];

    static void Walk(JsonElement n, StringBuilder sb)
    {
        var type = Str(n, "type");
        if (type == "text") { sb.Append(Str(n, "text")); return; }
        if (type == "hardBreak") { sb.Append('\n'); return; }
        if (type is "mention" or "emoji" or "inlineCard") { sb.Append(Str(n, "attrs", "text") ?? Str(n, "attrs", "url") ?? ""); return; }
        if (n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var child in c.EnumerateArray()) Walk(child, sb);
        if (type != null && BlockNodes.Contains(type)) sb.Append('\n');
        if (type == "tableCell" || type == "tableHeader") sb.Append(" | ");
    }
}
