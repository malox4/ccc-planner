namespace Planner.Services;

public class JiraOptions
{
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    public string ApiToken { get; set; } = "";
    /// <summary>Ключ Jira-проекта: из него строятся запросы по умолчанию.</summary>
    public string ProjectKey { get; set; } = "DEMO";
    /// <summary>Что считаем «доской». Пусто — всё незакрытое в проекте плюс всё из открытого спринта.</summary>
    public string Jql { get; set; } = "";
    /// <summary>Какие из них сейчас в открытом спринте Jira. Пусто — открытые спринты проекта.</summary>
    public string SprintJql { get; set; } = "";
}

public class PlannerOptions
{
    /// <summary>Часов на человека в спринт по умолчанию.</summary>
    public double DefaultCapacityHours { get; set; } = 50;
    /// <summary>Сопровождение аналитика (вопросы, ошибки, уточнения по задачам в работе), % от часов разработки.</summary>
    public double SupportPercent { get; set; } = 15;
    /// <summary>Папка в контейнере, куда кладут файл бизнес-заказчика.</summary>
    public string InboxPath { get; set; } = "/inbox";
    /// <summary>Как эта папка называется на компьютере (только для подсказки в интерфейсе).</summary>
    public string InboxHostPath { get; set; } = "";
}
