using BOAudit.Data;
using Microsoft.EntityFrameworkCore;

namespace BOAudit.Data;

/// <summary>Contexte EF Core : base SQL des destinataires (issus des RH).</summary>
public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Destinataire> Destinataires => Set<Destinataire>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Destinataire>(entity =>
        {
            entity.HasIndex(d => d.Email).IsUnique();
            entity.Property(d => d.Email).HasMaxLength(50).IsRequired();
            entity.Property(d => d.MatriculeRh).HasMaxLength(6);
            entity.Property(d => d.Nom).HasMaxLength(50);
            entity.Property(d => d.Prenom).HasMaxLength(50);
        });
    }
}
