namespace BOAudit.Models;

/// <summary>Ligne affichée dans la liste des destinataires référencés dans les publications et les
/// planifications, enrichie avec les informations RH de la base SQL (EF).</summary>
public sealed class RecipientRow
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Nom issu de la vue [Vue_MailPro], sinon "Non trouvé en base".</summary>
    public string? Nom { get; set; }
    public string? Prenom { get; set; }

    /// <summary>Date de sortie prévue (DateSortie de la vue [Vue_MailPro]), ou null.</summary>
    public DateTime? DateSortie { get; set; }

    /// <summary>True si une fiche Destinataire correspondante a été trouvée en base.</summary>
    public bool FoundInDatabase { get; set; }

    /// <summary>Noms des publications envoyées à ce destinataire.</summary>
    public List<string> Publications { get; set; } = new();

    /// <summary>Noms des planifications envoyées à ce destinataire.</summary>
    public List<string> Planifications { get; set; } = new();
}
