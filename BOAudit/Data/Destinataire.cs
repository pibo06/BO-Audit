using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BOAudit.Data;

/// <summary>Destinataire référencé par les publications, complété depuis la base SQL RH.</summary>
[Table("Destinataires")]
public sealed class Destinataire
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Email { get; set; } = string.Empty;

    [Column("MatriculeRH")]
    [MaxLength(6)]
    public string? MatriculeRh { get; set; }

    [MaxLength(50)]
    public string? Nom { get; set; }

    [MaxLength(50)]
    public string? Prenom { get; set; }

    public bool Sorti { get; set; }
}
