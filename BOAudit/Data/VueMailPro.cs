using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BOAudit.Data;

/// <summary>Destinataire depuis la vue SQL [Vue_MailPro] (base DEV, lecture seule) :
/// e-mail professionnel complété du nom, prénom et de la date de sortie RH.</summary>
[Table("Vue_MailPro")]
public sealed class VueMailPro
{
    /// <summary>E-mail professionnel, unique dans la vue.</summary>
    [Key]
    [Column("mailpro")]
    public string MailPro { get; set; } = string.Empty;

    [Column("Nom")]
    public string? Nom { get; set; }

    [Column("Prenom")]
    public string? Prenom { get; set; }

    /// <summary>Date de sortie RH ; une valeur non nulle indique que la personne est sortie.</summary>
    [Column("DateSortie")]
    public DateTime? DateSortie { get; set; }
}
