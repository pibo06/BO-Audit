namespace BOAudit.Models;

/// <summary>Paramètres de sécurité Windows (définis dans appsettings.json).</summary>
public sealed class SecuritySettings
{
    public const string Section = "Security";

    /// <summary>
    /// Groupes Windows autorisés à accéder à l'application, ex. "MONDOMAINE\\Groupe_BOAudit".
    /// L'accès est refusé si aucun groupe n'est configuré (échec par défaut).
    /// </summary>
    public string[] AllowedGroups { get; set; } = Array.Empty<string>();
}
