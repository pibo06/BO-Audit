using System.Globalization;

namespace BOAudit.Models;

/// <summary>Helpers d'affichage (dates, badges de statut).</summary>
public static class Display
{
    public static string? Date(DateTime? date)
        => date?.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR"));

    public static string StatusBadgeClass(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "bg-secondary";
        var t = status.Trim().ToLowerInvariant();
        if (t.Contains("échec") || t.Contains("echec") || t.Contains("fail")) return "bg-danger";
        if (t.Contains("termin") || t.Contains("complet") || t.Contains("succ")) return "bg-success";
        if (t.Contains("cours") || t.Contains("run") || t.Contains("execut") || t.Contains("exécut")) return "bg-warning text-dark";
        if (t.Contains("annul") || t.Contains("stop")) return "bg-dark";
        return "bg-secondary";
    }

    public static string StatusLabel(string? status) => string.IsNullOrWhiteSpace(status) ? "Inconnu" : status.Trim();
}
