namespace BOAudit.Models;

/// <summary>Référence d'un envoi (publication ou planification) : id de la page de détail et nom affiché.</summary>
public sealed record EnvoiRef(int Id, string Name);
