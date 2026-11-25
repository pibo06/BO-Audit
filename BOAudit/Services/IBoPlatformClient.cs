using System.Text.Json;

namespace BOAudit.Services;

/// <summary>Client de la plateforme SAP BusinessObjects (API RESTful /biprws et requêtes CMS QueryBuilder).</summary>
public interface IBoPlatformClient
{
    /// <summary>Authentification générique (secEnterprise) ; retourne le jeton X-SAP-LogonToken.</summary>
    Task<string> LogonAsync(CancellationToken ct = default);

    /// <summary>Libère le jeton de session BO.</summary>
    Task LogoffAsync(string token, CancellationToken ct = default);

    /// <summary>Liste les enfants d'un dossier de l'infostore (publications, instances...).</summary>
    Task<List<Models.BoObject>> GetChildrenAsync(string token, int folderId, int page = 1, int? pageSize = null, CancellationToken ct = default);

    /// <summary>Lit un objet de l'infostore (nom du rapport, lien OpenDocument...).</summary>
    Task<Models.BoObject?> GetObjectAsync(string token, int id, CancellationToken ct = default);

    /// <summary>Exécute une requête CMS QueryBuilder et retourne toutes les entrées du résultat (JSON).</summary>
    Task<List<JsonElement>> CmsQueryAsync(string token, string query, CancellationToken ct = default);

    /// <summary>Exécute une requête CMS QueryBuilder et retourne la première entrée du résultat (JSON), sinon null.</summary>
    Task<JsonElement?> CmsQueryFirstAsync(string token, string query, CancellationToken ct = default);
}
