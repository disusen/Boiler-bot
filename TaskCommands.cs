using Discord;
using Discord.Commands;
using ProductivityBot.Models;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("task")]
[Alias("t")]
[Summary("Task management commands")]
public class TaskCommands : ModuleBase<SocketCommandContext>
{
    private readonly TaskService _tasks;

    public TaskCommands(TaskService tasks) => _tasks = tasks;

    [Command("add")]
    [Alias("a", "new")]
    [Summary("Add a new task. Usage: !task add <title> [--desc <description>] [--due <date>] [--priority low|high]")]
    public async Task AddTaskAsync([Remainder] string input)
    {
        var parsed = ParseTaskInput(input);

        var task = await _tasks.AddTaskAsync(
            Context.User.Id,
            parsed.title,
            parsed.description,
            parsed.dueDate,
            parsed.priority);

        var embed = new EmbedBuilder()
            .WithColor(PriorityColor(task.Priority))
            .WithTitle("✅ Task Added")
            .AddField("Task", $"`#{task.Id}` {task.Title}", inline: false)
            .WithFooter("Use !task list to see all tasks")
            .Build();

        if (task.DueDate.HasValue)
            embed = embed.ToEmbedBuilder()
                .AddField("Due", task.DueDate.Value.ToString("ddd, MMM d HH:mm UTC"), inline: true)
                .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("list")]
    [Alias("l", "ls")]
    [Summary("List your pending tasks")]
    public async Task ListTasksAsync()
    {
        var tasks = await _tasks.GetPendingTasksAsync(Context.User.Id);

        if (tasks.Count == 0)
        {
            await ReplyAsync("📭 No pending tasks. Add one with `!task add <title>`");
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(Color.Blue)
            .WithTitle($"📋 Your Tasks ({tasks.Count} pending)")
            .WithFooter("!task done <id> | !task del <id>");

        foreach (var task in tasks)
        {
            var priorityIcon = task.Priority switch
            {
                TaskPriority.High => "🔴",
                TaskPriority.Low => "🟢",
                _ => "🟡"
            };

            var dueStr = task.DueDate.HasValue
                ? $"\n📅 Due: {task.DueDate.Value:MMM d}"
                : string.Empty;

            var overdue = task.DueDate.HasValue && task.DueDate < DateTime.UtcNow ? " ⚠️ OVERDUE" : string.Empty;

            embed.AddField(
                $"{priorityIcon} #{task.Id} {task.Title}{overdue}",
				(task.Description != null || dueStr != string.Empty) ? $"{task.Description}{dueStr}" : "\u200b",
				inline: false);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("done")]
    [Alias("complete", "finish")]
    [Summary("Mark a task as done. Usage: !task done <id>")]
    public async Task CompleteTaskAsync(int taskId)
    {
        var success = await _tasks.CompleteTaskAsync(Context.User.Id, taskId);

        if (!success)
            await ReplyAsync("❌ Task not found or already completed.");
        else
            await ReplyAsync($"✅ Task `#{taskId}` marked as complete. Nice work!");
    }

    [Command("del")]
    [Alias("delete", "remove", "rm")]
    [Summary("Delete a task. Usage: !task del <id>")]
    public async Task DeleteTaskAsync(int taskId)
    {
        var success = await _tasks.DeleteTaskAsync(Context.User.Id, taskId);

        if (!success)
            await ReplyAsync("❌ Task not found.");
        else
            await ReplyAsync($"🗑️ Task `#{taskId}` deleted.");
    }

    [Command("stats")]
    [Summary("Show your task stats")]
    public async Task StatsAsync()
    {
        var (total, completed, overdue) = await _tasks.GetStatsAsync(Context.User.Id);
        var pending = total - completed;
        var completionRate = total > 0 ? (int)((double)completed / total * 100) : 0;

        var embed = new EmbedBuilder()
            .WithColor(Color.Gold)
            .WithTitle("📊 Your Task Stats")
            .AddField("Total", total.ToString(), inline: true)
            .AddField("Completed", completed.ToString(), inline: true)
            .AddField("Pending", pending.ToString(), inline: true)
            .AddField("Overdue", overdue.ToString(), inline: true)
            .AddField("Completion Rate", $"{completionRate}%", inline: true)
            .Build();

        await ReplyAsync(embed: embed);
    }

    // --- Helpers ---

    private static Color PriorityColor(TaskPriority p) => p switch
    {
        TaskPriority.High => Color.Red,
        TaskPriority.Low => Color.Green,
        _ => Color.Orange
    };

    private static (string title, string? description, DateTime? dueDate, TaskPriority priority) ParseTaskInput(string input)
    {
        // Simple flag parser: "title text --desc some desc --due 2025-03-10 --priority high"
        string title = input;
        string? description = null;
        DateTime? dueDate = null;
        var priority = TaskPriority.Normal;

        var parts = input.Split("--");
        title = parts[0].Trim();

        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.StartsWith("desc ", StringComparison.OrdinalIgnoreCase))
                description = part[5..].Trim();
            else if (part.StartsWith("due ", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTime.TryParse(part[4..].Trim(), out var dt))
                    dueDate = dt.ToUniversalTime();
            }
            else if (part.StartsWith("priority ", StringComparison.OrdinalIgnoreCase))
            {
                var p = part[9..].Trim().ToLower();
                priority = p switch
                {
                    "high" or "h" => TaskPriority.High,
                    "low" or "l" => TaskPriority.Low,
                    _ => TaskPriority.Normal
                };
            }
        }

        return (title, description, dueDate, priority);
    }
}
