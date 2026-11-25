namespace BOAudit.Models;

/// <summary>Instance d'exécution d'une publication (historique infostore/children).</summary>
public sealed class BoInstance
{
    public int Id { get; set; }
    public string Cuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? Updated { get; set; }
    public string? Status { get; set; }
    public string? OpenDocumentLink { get; set; }
}
