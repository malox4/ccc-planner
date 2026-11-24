namespace Planner.Services;

public class JiraOptions
{
    public string BaseUrl { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Токен из .env (запасной вариант); основной способ — ввести токен во вкладке «Подключение».</summary>
    public string ApiToken { get; set; } = "";
    /// <summary>Ссылка на доску Jira по умолчанию. В интерфейсе (вкладка «Подключение») её можно заменить на свою.</summary>
    public string BoardUrl { get; set; } = "";
    /// <summary>Ключ проекта: используется, если ссылка на доску не указана.</summary>
    public string ProjectKey { get; set; } = "";
    /// <summary>Свой JQL вместо фильтра доски (необязательно). Пусто — берём фильтр доски или весь проект.</summary>
    public string Jql { get; set; } = "";
    /// <summary>Какие из них сейчас в открытом спринте Jira. Пусто — открытые спринты доски или проекта.</summary>
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
    /// <summary>Папка с ключами шифрования токена (отдельный том Docker).</summary>
    public string KeysPath { get; set; } = "/keys";
}
