using System.ComponentModel.DataAnnotations;

namespace Planner.Data;

/// <summary>Зеркало задачи из Jira. Синхронизация перезаписывает только эти поля, планирование хранится отдельно в Plan.</summary>
public class Issue
{
    [Key] public string Key { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Type { get; set; } = "Task";
    public string Status { get; set; } = "";
    /// <summary>new | indeterminate | done — категория статуса из Jira.</summary>
    public string StatusCategory { get; set; } = "new";
    public string? Priority { get; set; }
    public string? Assignee { get; set; }
    public string? ParentKey { get; set; }
    public string? ParentSummary { get; set; }
    public string? Description { get; set; }
    /// <summary>null — данных нет (задача из сида, ещё не синхронизирована).</summary>
    public bool? HasDescription { get; set; }
    public bool? HasAcceptanceCriteria { get; set; }
    public int AttachmentCount { get; set; }
    public bool InJiraSprint { get; set; }
    public DateTime? JiraUpdatedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    /// <summary>Задача пропала из выборки Jira — считаем закрытой, но в сервисе не удаляем.</summary>
    public DateTime? MissingSinceUtc { get; set; }
    public string? LastKnownStatus { get; set; }
}

/// <summary>Наши данные по задаче: оценки, назначения, участие в плане. Sync их не трогает.</summary>
public class Plan
{
    [Key] public string Key { get; set; } = "";
    public int? Bv { get; set; }
    public int? Tc { get; set; }
    public int? Rr { get; set; }
    /// <summary>Часы, которые ввела команда; важнее EstLikely.</summary>
    public double? ManualHours { get; set; }
    /// <summary>Часы анализа, которые ввела команда; важнее модели.</summary>
    public double? ManualAnalysisHours { get; set; }
    /// <summary>Ждём ответа внешней стороны (например, партнёр): часы под работу после ответа остаются зарезервированными.</summary>
    public string? WaitingOn { get; set; }
    /// <summary>Задачу делает ещё и команда интеграторов: аналитик пишет для неё требования (API-контракт, сценарии, скрипты).</summary>
    public bool ForIntegrators { get; set; }
    public double? EstMin { get; set; }
    public double? EstLikely { get; set; }
    public double? EstMax { get; set; }
    /// <summary>ac | desc | title | approx — на чём построена оценка.</summary>
    public string? EstQuality { get; set; }
    public string? EstBasis { get; set; }
    public string? Analyst { get; set; }
    public string? Developer { get; set; }
    /// <summary>null — авто: анализ считается сделанным, если есть вложение или описание с критериями приёмки.</summary>
    public bool? AnalysisDone { get; set; }
    /// <summary>null — авто по ключевым словам заголовка; true/false — решение человека.</summary>
    public bool? AnalysisNotNeeded { get; set; }
    /// <summary>Следующий (пред-) спринт: задачи, которые планируем взять.</summary>
    public bool InSprint { get; set; }
    /// <summary>Переведена в текущий спринт в сервисе (прошла DoR и ёмкость), пока Jira этого не отражает. После синхронизации, когда Jira добавит её в спринт, флаг снимается.</summary>
    public bool InCurrent { get; set; }
    /// <summary>В Jira появились критерии приёмки — оценку часов разработки нужно пересмотреть.</summary>
    public bool NeedsReestimate { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class Person
{
    [Key] public string Name { get; set; } = "";
    /// <summary>dev | analyst</summary>
    public string Role { get; set; } = "dev";
    /// <summary>Часов на спринт.</summary>
    public double CapacityHours { get; set; } = 50;
    /// <summary>Часов на спринт, которые уходят на встречи, инциденты, root cause и бизнес-вопросы вне задач Jira.</summary>
    public double OverheadHours { get; set; }
}

public class StatusChange
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string? From { get; set; }
    public string To { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

public class BusinessPriority
{
    public int Id { get; set; }
    public string Theme { get; set; } = "";
    /// <summary>1 — самый высокий.</summary>
    public int Priority { get; set; }
    public string Keywords { get; set; } = "";
    public string? Source { get; set; }
    public DateTime UploadedUtc { get; set; }
}

public class BusinessLink
{
    public int Id { get; set; }
    public int BusinessPriorityId { get; set; }
    public string IssueKey { get; set; } = "";
    public double Score { get; set; }
    public bool Confirmed { get; set; }
}

public class SyncRun
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int Total { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Disappeared { get; set; }
    public int Returned { get; set; }
    public string? Error { get; set; }
}
