namespace TodoApp.Models;

/// <summary>旧版扁平子任务（v2.0 及以前的数据格式，加载时自动迁移为 Children，仅保留用于反序列化）</summary>
public class SubTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool IsDone { get; set; }
}

/// <summary>
/// 一条待办事项（树形结构，子任务即完整的 TodoItem，可无限嵌套）。
/// Priority: 0=无 1=低 2=中 3=高；Details 支持 Markdown-lite 与 ![图片](附件名) 引用。
/// </summary>
public class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string Details { get; set; } = "";
    public bool IsDone { get; set; }
    public int Priority { get; set; }
    public DateTime? DueAt { get; set; }
    public bool Remind { get; set; }
    public List<TodoItem> Children { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    /// <summary>旧版数据，加载迁移后置 null（序列化时忽略）</summary>
    public List<SubTask>? SubTasks { get; set; }
}
