namespace BOAudit.Models;

/// <summary>
/// Planification BO : document Webi planifié (SI_INSTANCE = 0, SI_KIND = 'Webi', SI_CHILDREN &gt; 0),
/// avec sa récurrence, son format de sortie, ses destinataires SMTP et son état de dernière exécution.
/// </summary>
public sealed class BoPlanification
{
    public int Id { get; set; }
    public string Cuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime? Updated { get; set; }

    /// <summary>Nom du dossier contenant le document planifié.</summary>
    public string? Folder { get; set; }

    /// <summary>ID du dossier parent (résolu en nom lors de la collecte).</summary>
    public int? ParentFolderId { get; set; }

    /// <summary>Nombre d'instances planifiées (SI_CHILDREN).</summary>
    public int InstanceCount { get; set; }

    public DateTime? LastRunTime { get; set; }

    /// <summary>Description de la récurrence (type de planification + jours du modèle éventuel).</summary>
    public string? Recurrence { get; set; }

    /// <summary>Format de sortie planifié (PDF, Excel...).</summary>
    public string? Format { get; set; }

    /// <summary>Le document comporte des invites (SI_HAS_PROMPTS).</summary>
    public bool HasPrompts { get; set; }

    /// <summary>Adresses e-mail statiques des destinations SMTP (À et CC).</summary>
    public List<string> StaticEmails { get; set; } = new();

    /// <summary>Indique que les destinataires SMTP sont résolus dynamiquement (%SI_EMAIL_ADDRESS%).</summary>
    public bool UsesDynamicSmtpRecipients { get; set; }

    /// <summary>Erreur éventuelle rencontrée lors de l'enrichissement de cette planification.</summary>
    public string? Error { get; set; }
}
