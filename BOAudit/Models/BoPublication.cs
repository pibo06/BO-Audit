namespace BOAudit.Models;

/// <summary>
/// Synthèse d'une publication BO : identité, rapport source, planning,
/// destinataires (statiques et dynamiques) et état de la dernière exécution.
/// </summary>
public sealed class BoPublication
{
    public int Id { get; set; }
    public string Cuid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime? Updated { get; set; }
    public string? OpenDocumentLink { get; set; }

    // --- Rapport utilisé pour la publication ---
    public int? SourceDocumentId { get; set; }
    public string? SourceDocumentName { get; set; }
    public string? SourceDocumentOpenLink { get; set; }

    /// <summary>ID du rapport fournissant les destinataires dynamiques (SI_ON_READ_RECIPIENTS / SI_OBJID).</summary>
    public int? RecipientsProviderDocumentId { get; set; }
    public string? RecipientsProviderDocumentName { get; set; }

    /// <summary>Rapports source des destinataires dynamiques (SI_DYNAMIC_RECIPIENTS / SI_SOURCE_DOC_ID), avec leur dossier.</summary>
    public List<BoRecipientsSource> RecipientsSources { get; set; } = new();

    // --- Destinataires ---
    /// <summary>Colonnes dynamiques définies (email, nom complet, identifiant...).</summary>
    public List<string> DynamicRecipientColumns { get; set; } = new();

    /// <summary>Adresses e-mail statiques configurées dans les destinations SMTP (CC, adresses fixes).</summary>
    public List<string> StaticEmails { get; set; } = new();

    /// <summary>Indique que les destinataires SMTP sont résolus dynamiquement (%SI_EMAIL_ADDRESS%).</summary>
    public bool UsesDynamicSmtpRecipients { get; set; }

    // --- Planning ---
    public string? ScheduleStatus { get; set; }
    public DateTime? NextRunTime { get; set; }

    // --- Dernière exécution ---
    public DateTime? LastRunTime { get; set; }
    public string? LastRunStatus { get; set; }
    public int? LastSuccessfulInstanceId { get; set; }
    public string? LastInstanceOpenLink { get; set; }
    public List<BoInstance> History { get; set; } = new();

    /// <summary>Erreur éventuelle rencontrée lors de l'enrichissement de cette publication.</summary>
    public string? Error { get; set; }
}
