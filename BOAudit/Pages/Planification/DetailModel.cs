using BOAudit.Data;
using BOAudit.Models;
using BOAudit.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BOAudit.Pages.Planification;

/// <summary>Détail d'une planification : échéance, récurrence, destinataires et historique des instances.</summary>
public class DetailModel(
    IBoAuditCache cache,
    ApplicationDbContext db) : PageModel
{
    public BoPlanification? Planification { get; private set; }
    public List<RecipientRow> StaticRecipients { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Planification = cache.FindPlanification(Id);
        if (Planification == null)
        {
            // Après un redémarrage ou un changement d'ID : propose de rafraîchir.
            return RedirectToPage("/Index");
        }

        StaticRecipients = await BuildStaticRecipientsAsync();
        return Page();
    }

    private async Task<List<RecipientRow>> BuildStaticRecipientsAsync()
    {
        if (Planification == null || Planification.StaticEmails.Count == 0)
            return new List<RecipientRow>();

        var emails = Planification.StaticEmails.ToList();
        var fiches = await db.MailPros
            .Where(m => emails.Contains(m.MailPro))
            .ToListAsync(CancellationToken.None);
        var fichesParEmail = fiches.ToDictionary(m => m.MailPro.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        return emails.Select(email =>
        {
            var row = new RecipientRow { Email = email };
            if (fichesParEmail.TryGetValue(email.Trim(), out var fiche))
            {
                row.Nom = fiche.Nom;
                row.Prenom = fiche.Prenom;
                row.DateSortie = fiche.DateSortie;
                row.FoundInDatabase = true;
            }
            return row;
        }).ToList();
    }
}
