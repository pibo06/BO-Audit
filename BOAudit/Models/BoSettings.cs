namespace BOAudit.Models;

/// <summary>Paramètres de connexion à la plateforme SAP BusinessObjects (définis dans appsettings.json).</summary>
public sealed class BoSettings
{
    public const string Section = "BoSettings";

    /// <summary>URL de base du serveur BO RESTful, ex. http://bo.lignesdazur.fr:8080</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Compte générique utilisé pour le logon BO (secEnterprise).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Mot de passe générique.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Type d'authentification BO (secEnterprise, secWindowsAD, ...).</summary>
    public string Auth { get; set; } = "secEnterprise";

    /// <summary>ID du dossier public CMC contenant les publications (257219).</summary>
    public int PublicationsFolderId { get; set; } = 257219;

    /// <summary>Taille de page des requêtes infostore.</summary>
    public int PageSize { get; set; } = 500;
    /// <summary>Taille de page des requêtes infostore.</summary>
    public bool UseProxy { get; set; } = false;
}
