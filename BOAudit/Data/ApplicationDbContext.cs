using BOAudit.Data;
using Microsoft.EntityFrameworkCore;

namespace BOAudit.Data;

/// <summary>Contexte EF Core : vue SQL [Vue_MailPro] des destinataires (base DEV, lecture seule).</summary>
public sealed class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<VueMailPro> MailPros => Set<VueMailPro>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Vue SQL existante mappée sur une entité avec clé (mailpro est unique) : lecture seule uniquement.
        modelBuilder.Entity<VueMailPro>(entity =>
        {
            entity.HasKey(d => d.MailPro);
            entity.Property(d => d.MailPro).HasMaxLength(200);
            entity.Property(d => d.Nom).HasMaxLength(50);
            entity.Property(d => d.Prenom).HasMaxLength(50);
        });
    }
}
