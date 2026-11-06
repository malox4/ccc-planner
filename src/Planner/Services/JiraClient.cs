using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Planner.Services;

public record JiraIssueDto(
    string Key, string Summary, string Type, string Status, string StatusCategory, string? Priority,
    string? Assignee, string? ParentKey, string? ParentSummary, string DescriptionText, int AttachmentCount,
    DateTime? UpdatedUtc);

/// <summary>Только чтение из Jira Cloud (REST v3). Ничего в Jira не пишет.</summary>
public class JiraClient
{
    static readonly string[] Fields =
    [
        "summary", "issuetype", "status", "priority", "assignee", "parent", "description", "attachment", "updated"
    ];

    readonly HttpClient _http;
    readonly JiraOptions _o;

    public JiraClient(HttpClient http, IOptions<JiraOptions> options)
    {
        _http = http;
        _o = options.Value;
        if (IsConfigured)
        {
            _http.BaseAddress = new Uri(_o.BaseUrl.TrimEnd('/') + "/");
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_o.Email}:{_o.ApiToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_o.BaseUrl) && !string.IsNullOrWhiteSpace(_o.Email) && !string.IsNullOrWhiteSpace(_o.ApiToken);

    public async Task<List<JiraIssueDto>> SearchAsync(string jql, CancellationToken ct, bool keysOnly = false)
    {
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

            using var res = await _http.PostAsJsonAsync("rest/api/3/search/jql", body, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Jira ответила {(int)res.StatusCode}: {Trunc(text)}");

            using var doc = JsonDocument.Parse(text);
            foreach (var i in doc.RootElement.GetProperty("issues").EnumerateArray())
                result.Add(Parse(i));

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
        } while (pageToken != null);
        return result;
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
