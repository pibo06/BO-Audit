namespace BOAudit.Models;

/// <summary>Instance d'exécution d'une publication (historique renvoyé par cmsquery SI_ANCESTOR).</summary>
public sealed class BoInstance
{
    public int Id { get; set; }
    public string Cuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? Updated { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Status { get; set; }
    public string? OpenDocumentLink { get; set; }
}
