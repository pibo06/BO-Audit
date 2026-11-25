using System.Globalization;
using BOAudit.Data;
using BOAudit.Models;
using BOAudit.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BOAudit.Pages.Publication;

/// <summary>Détail d'une publication : rapport source, planning, état du dernier envoi et destinataires.</summary>
public class DetailModel(
    IBoAuditCache cache,
    ApplicationDbContext db) : PageModel
{
    public BoPublication? Publication { get; private set; }
    public List<RecipientRow> StaticRecipients { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        Publication = cache.Find(Id);
        if (Publication == null)
        {
            // Après un redémarrage ou un changement d'ID : propose de rafraîchir.
            return RedirectToPage("/Index");
        }

        StaticRecipients = await BuildStaticRecipientsAsync();
        return Page();
    }

    private async Task<List<RecipientRow>> BuildStaticRecipientsAsync()
    {
        if (Publication == null || Publication.StaticEmails.Count == 0)
            return new List<RecipientRow>();

        var emails = Publication.StaticEmails.ToList();
        var fiches = await db.Destinataires
            .Where(d => emails.Contains(d.Email))
            .ToDictionaryAsync(d => d.Email.Trim().ToLower(CultureInfo.InvariantCulture), d => d, CancellationToken.None);

        return emails.Select(email =>
        {
            var row = new RecipientRow { Email = email };
            if (fiches.TryGetValue(email.Trim().ToLower(CultureInfo.InvariantCulture), out var fiche))
            {
                row.Nom = fiche.Nom;
                row.Prenom = fiche.Prenom;
                row.MatriculeRh = fiche.MatriculeRh;
                row.Sorti = fiche.Sorti;
                row.FoundInDatabase = true;
            }
            return row;
        }).ToList();
    }
}
