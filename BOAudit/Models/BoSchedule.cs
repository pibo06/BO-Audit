namespace BOAudit.Models;

/// <summary>Planification associée à une publication (renseignée par .../schedules).</summary>
public sealed class BoSchedule
{
    public int Id { get; set; }
    public string ScheduleName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? UiStatus { get; set; }
    public string? Path { get; set; }
    public DateTime? NextRunTime { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime? Expiry { get; set; }
    public DateTime? Created { get; set; }
}
