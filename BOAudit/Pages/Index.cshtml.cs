using BOAudit.Data;
using BOAudit.Models;
using BOAudit.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace BOAudit.Pages;

public class IndexModel(
    IBoAuditCache cache,
    ApplicationDbContext db) : PageModel
{
    public DateTime? LastRefresh { get; private set; }
    public IReadOnlyList<BoPublication> Publications { get; private set; } = Array.Empty<BoPublication>();
    public IReadOnlyList<BoPlanification> Planifications { get; private set; } = Array.Empty<BoPlanification>();
    public List<RecipientRow> Recipients { get; private set; } = new();

    /// <summary>Vue demandée : "cartes" (défaut), "planifications" ou "destinataires".</summary>
    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "cartes";

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync()
    {
        LastRefresh = cache.LastRefresh;
        ErrorMessage = cache.LastError;
        Publications = cache.Publications;
        Planifications = cache.Planifications;

        if (View.Equals("destinataires", StringComparison.OrdinalIgnoreCase))
        {
            Recipients = await BuildRecipientsAsync();
        }
    }

    public async Task<IActionResult> OnPostRefreshAsync()
    {
        var result = await cache.RefreshAsync();
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] = result.Message;
        return RedirectToPage(new { view = View });
    }

    /// <summary>Rassemble tous les e-mails référencés dans les publications et les planifications,
    /// et les complète avec la base SQL (EF).</summary>
    private async Task<List<RecipientRow>> BuildRecipientsAsync()
    {
        var rows = new Dictionary<string, RecipientRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var publication in cache.Publications)
        {
            foreach (var email in publication.StaticEmails)
            {
                if (!rows.TryGetValue(email, out var row))
                    rows[email] = row = new RecipientRow { Email = email };
                row.Publications.Add(publication.Name);
            }
        }

        foreach (var planification in cache.Planifications)
        {
            foreach (var email in planification.StaticEmails)
            {
                if (!rows.TryGetValue(email, out var row))
                    rows[email] = row = new RecipientRow { Email = email };
                row.Planifications.Add(planification.Name);
            }
        }

        if (rows.Count == 0) return new List<RecipientRow>();

        var emails = rows.Keys.ToList();
        var fiches = await db.MailPros
            .Where(m => emails.Contains(m.MailPro))
            .ToListAsync(CancellationToken.None);
        var fichesParEmail = fiches.ToDictionary(m => m.MailPro.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        foreach (var (email, row) in rows)
        {
            if (fichesParEmail.TryGetValue(email.Trim(), out var fiche))
            {
                row.Nom = fiche.Nom;
                row.Prenom = fiche.Prenom;
                row.DateSortie = fiche.DateSortie;
                row.FoundInDatabase = true;
            }
        }

        return rows.Values
            .OrderBy(r => r.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
