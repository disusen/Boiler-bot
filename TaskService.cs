using Microsoft.EntityFrameworkCore;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

public class TaskService
{
    private readonly BotDbContext _db;

    public TaskService(BotDbContext db) => _db = db;

    public async Task<TaskItem> AddTaskAsync(ulong userId, string title, string? description = null,
        DateTime? dueDate = null, TaskPriority priority = TaskPriority.Normal)
    {
        var task = new TaskItem
        {
            UserId = userId,
            Title = title,
            Description = description,
            DueDate = dueDate,
            Priority = priority
        };
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    public async Task<List<TaskItem>> GetPendingTasksAsync(ulong userId)
        => await _db.Tasks
            .Where(t => t.UserId == userId && !t.IsCompleted)
            .OrderBy(t => t.Priority == TaskPriority.High ? 0 : t.Priority == TaskPriority.Normal ? 1 : 2)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync();

    public async Task<List<TaskItem>> GetCompletedTasksAsync(ulong userId, int limit = 10)
        => await _db.Tasks
            .Where(t => t.UserId == userId && t.IsCompleted)
            .OrderByDescending(t => t.CompletedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<TaskItem?> GetTaskAsync(ulong userId, int taskId)
        => await _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == userId);

    public async Task<bool> CompleteTaskAsync(ulong userId, int taskId)
    {
        var task = await GetTaskAsync(userId, taskId);
        if (task is null || task.IsCompleted) return false;

        task.IsCompleted = true;
        task.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteTaskAsync(ulong userId, int taskId)
    {
        var task = await GetTaskAsync(userId, taskId);
        if (task is null) return false;

        _db.Tasks.Remove(task);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<(int total, int completed, int overdue)> GetStatsAsync(ulong userId)
    {
        var tasks = await _db.Tasks.Where(t => t.UserId == userId).ToListAsync();
        var now = DateTime.UtcNow;
        return (
            tasks.Count,
            tasks.Count(t => t.IsCompleted),
            tasks.Count(t => !t.IsCompleted && t.DueDate.HasValue && t.DueDate < now)
        );
    }
}
