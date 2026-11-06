namespace Planner.Services;

public static class Fmt
{
    // Ташкент, UTC+5 без перехода на летнее время — не зависим от tzdata в контейнере
    public static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(5);
    public static string Stamp(DateTime utc) => Local(utc).ToString("dd.MM HH:mm");
    public static string Day(DateTime utc) => Local(utc).ToString("dd.MM.yyyy");

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var p = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 1 ? p[0][..1].ToUpperInvariant() : (p[0][..1] + p[^1][..1]).ToUpperInvariant();
    }

    public static string TypeClass(string type)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("эпик") || t.Contains("epic")) return "epic";
        if (t.Contains("иници") || t.Contains("initiative")) return "init";
        if (t.Contains("story") || t.Contains("истор")) return "story";
        if (t.Contains("подзадач") || t.Contains("sub")) return "sub";
        if (t.Contains("problem") || t.Contains("bug") || t.Contains("ошибк")) return "bug";
        return "task";
    }

    public static bool IsContainerType(string type) => TypeClass(type) is "epic" or "init";

    public static string Hours(double h) => h == 0 ? "—" : (h % 1 == 0 ? $"{h:0} ч" : $"{h:0.#} ч");
}
