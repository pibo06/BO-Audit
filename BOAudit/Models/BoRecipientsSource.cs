namespace BOAudit.Models;

/// <summary>Rapport source des destinataires dynamiques d'une publication, résolu par l'infostore.</summary>
public sealed class BoRecipientsSource
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Folder { get; set; }
}
