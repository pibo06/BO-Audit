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

    /// <summary>Classe du badge d'échéance d'une planification selon SI_ENDTIME :
    /// rouge si dépassée, orange si dans le mois qui suit, vert sinon ; neutre si absente.</summary>
    public static string ScheduleEndBadgeClass(DateTime? endTime)
    {
        if (endTime is null) return "bg-secondary";
        if (endTime.Value < DateTime.Now) return "bg-danger";
        if (endTime.Value <= DateTime.Now.AddDays(30)) return "bg-warning text-dark";
        return "bg-success";
    }

    /// <summary>Libellé du badge d'échéance d'une planification (SI_ENDTIME).</summary>
    public static string ScheduleEndLabel(DateTime? endTime)
    {
        if (endTime is null) return "Pas d'échéance";
        return endTime.Value < DateTime.Now ? "Planification dépassée" : "Échéance";
    }
}
