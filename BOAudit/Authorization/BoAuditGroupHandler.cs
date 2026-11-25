using Microsoft.AspNetCore.Authorization;
using BOAudit.Models;
using Microsoft.Extensions.Options;

namespace BOAudit.Authorization;

/// <summary>Exige que l'utilisateur authentifié (Windows) appartienne à un groupe défini dans appsettings.json.</summary>
public sealed class BoAuditGroupRequirement : IAuthorizationRequirement;

public sealed class BoAuditGroupHandler(
    IOptions<SecuritySettings> options,
    ILogger<BoAuditGroupHandler> logger) : AuthorizationHandler<BoAuditGroupRequirement>
{
    private readonly SecuritySettings _settings = options.Value;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, BoAuditGroupRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        var groups = (_settings.AllowedGroups ?? Array.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .ToArray();

        // Refus par défaut si aucun groupe n'est configuré.
        if (groups.Length == 0)
        {
            logger.LogWarning("Accès refusé à {User} : aucun groupe autorisé configuré (Security:AllowedGroups).",
                context.User.Identity.Name);
            return Task.CompletedTask;
        }

        foreach (var group in groups)
        {
            if (context.User.IsInRole(group))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        logger.LogWarning("Accès refusé à {User} : n'appartient à aucun des groupes autorisés ({Groups}).",
            context.User.Identity.Name, string.Join(", ", groups));
        return Task.CompletedTask;
    }
}
