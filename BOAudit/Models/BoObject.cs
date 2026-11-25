namespace BOAudit.Models;

/// <summary>Objet générique renvoyé par l'API REST infostore BO (publication, rapport, instance...).</summary>
public sealed class BoObject
{
    public int Id { get; set; }
    public string Cuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime? Updated { get; set; }

    /// <summary>Lien OpenDocument (visualisation du document dans BO launchpad).</summary>
    public string? OpenDocumentLink { get; set; }
}
