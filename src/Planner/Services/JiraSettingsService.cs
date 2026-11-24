using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Planner.Data;

namespace Planner.Services;

public record ParsedBoard(string BaseUrl, string? ProjectKey, int? BoardId);

/// <summary>Подключение к Jira, с которым работает сервис сейчас.</summary>
public class JiraConfig
{
    public string BoardUrl { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string? ProjectKey { get; init; }
    public int? BoardId { get; init; }
    public string Email { get; init; } = "";
    public string Token { get; init; } = "";
    /// <summary>ui — токен введён на вкладке «Подключение»; env — из .env; broken — не расшифровался; none — нет.</summary>
    public string TokenSource { get; init; } = "none";
    /// <summary>Свои запросы из .env (необязательно): перекрывают выборку по доске.</summary>
    public string? Jql { get; init; }
    public string? SprintJql { get; init; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Настройки Jira для каждого пользователя: ссылка на доску, email и API-токен вводятся во вкладке «Подключение».
/// Токен хранится в базе в зашифрованном виде (ASP.NET Data Protection, ключи на отдельном томе) и в интерфейс никогда не возвращается.
/// Значения из .env работают как умолчания.
/// </summary>
public class JiraSettingsService(IDbContextFactory<PlannerDb> dbf, IOptions<JiraOptions> env, IDataProtectionProvider dp)
{
    const string KBoard = "Jira.BoardUrl", KEmail = "Jira.Email", KToken = "Jira.TokenEnc";
    readonly IDataProtector _p = dp.CreateProtector("ccc-planner.jira-token.v1");

    static readonly Regex ProjectsRx = new(@"/projects/([A-Za-z][A-Za-z0-9_]*)", RegexOptions.Compiled);
    static readonly Regex BoardsRx = new(@"/boards/(\d+)", RegexOptions.Compiled);
    static readonly Regex RapidRx = new(@"[?&]rapidView=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ProjectKeyRx = new(@"[?&]projectKey=([A-Za-z][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Разбирает ссылку на доску: https://компания.atlassian.net/jira/software/c/projects/KEY/boards/123</summary>
    public static ParsedBoard? ParseBoardUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return null;
        var path = u.AbsolutePath;
        var proj = ProjectsRx.Match(path) is { Success: true } m1 ? m1.Groups[1].Value : ProjectKeyRx.Match(u.Query) is { Success: true } m2 ? m2.Groups[1].Value : null;
        var board = BoardsRx.Match(path) is { Success: true } b1 ? b1.Groups[1].Value : RapidRx.Match(u.Query) is { Success: true } b2 ? b2.Groups[1].Value : null;
        return new ParsedBoard($"{u.Scheme}://{u.Host}", proj, board != null ? int.Parse(board) : null);
    }

    public async Task<JiraConfig> GetAsync()
    {
        await using var db = await dbf.CreateDbContextAsync();
        var s = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value);

        var token = ""; var source = "none";
        if (s.TryGetValue(KToken, out var enc) && !string.IsNullOrEmpty(enc))
        {
            try { token = _p.Unprotect(enc); source = "ui"; }
            catch { source = "broken"; }
        }
        if (token == "" && !string.IsNullOrWhiteSpace(env.Value.ApiToken)) { token = env.Value.ApiToken; source = "env"; }

        return Compose(s.GetValueOrDefault(KBoard), s.GetValueOrDefault(KEmail), token, source);
    }

    JiraConfig Compose(string? boardUrl, string? email, string token, string source)
    {
        var o = env.Value;
        var board = !string.IsNullOrWhiteSpace(boardUrl) ? boardUrl.Trim() : o.BoardUrl;
        var pb = ParseBoardUrl(board);
        return new JiraConfig
        {
            BoardUrl = board ?? "",
            BaseUrl = pb?.BaseUrl ?? o.BaseUrl ?? "",
            ProjectKey = pb?.ProjectKey ?? (string.IsNullOrWhiteSpace(o.ProjectKey) ? null : o.ProjectKey),
            BoardId = pb?.BoardId,
            Email = !string.IsNullOrWhiteSpace(email) ? email.Trim() : o.Email ?? "",
            Token = token,
            TokenSource = source,
            Jql = string.IsNullOrWhiteSpace(o.Jql) ? null : o.Jql,
            SprintJql = string.IsNullOrWhiteSpace(o.SprintJql) ? null : o.SprintJql,
        };
    }

    /// <summary>Конфигурация из введённых на форме значений (ещё не сохранённых); пустой токен означает «взять сохранённый».</summary>
    public async Task<JiraConfig> BuildAsync(string boardUrl, string email, string? tokenInput)
    {
        var stored = await GetAsync();
        var token = !string.IsNullOrWhiteSpace(tokenInput) ? tokenInput.Trim() : stored.Token;
        var source = !string.IsNullOrWhiteSpace(tokenInput) ? "ui" : stored.TokenSource;
        return Compose(boardUrl, email, token, source);
    }

    public async Task SaveAsync(string boardUrl, string email, string? newToken, bool clearToken)
    {
        await using var db = await dbf.CreateDbContextAsync();
        async Task Put(string k, string? v)
        {
            var row = await db.AppSettings.FindAsync(k);
            if (string.IsNullOrEmpty(v)) { if (row != null) db.AppSettings.Remove(row); return; }
            if (row == null) db.AppSettings.Add(new AppSetting { Key = k, Value = v }); else row.Value = v;
        }
        await Put(KBoard, boardUrl.Trim());
        await Put(KEmail, email.Trim());
        if (clearToken) await Put(KToken, null);
        else if (!string.IsNullOrWhiteSpace(newToken)) await Put(KToken, _p.Protect(newToken.Trim()));
        await db.SaveChangesAsync();
    }
}
