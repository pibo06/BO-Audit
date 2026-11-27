using System.Text.Json;
using BOAudit.Models;
using Microsoft.Extensions.Options;

namespace BOAudit.Services;

public sealed record RefreshResult(bool Success, string Message);

/// <summary>Cache applicatif des publications BO, rafraîchi depuis la plateforme et persisté sur disque
/// (App_Data/bo-cache.json) afin de conserver la date de dernière actualisation.</summary>
public interface IBoAuditCache
{
    DateTime? LastRefresh { get; }
    IReadOnlyList<BoPublication> Publications { get; }
    IReadOnlyList<BoPlanification> Planifications { get; }
    BoPublication? Find(int id);
    BoPlanification? FindPlanification(int id);
    Task<RefreshResult> RefreshAsync(CancellationToken ct = default);
    string? LastError { get; }
}

public sealed class BoAuditCache : IBoAuditCache
{
    private readonly IBoPlatformClient _bo;
    private readonly BoSettings _settings;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<BoAuditCache> _logger;

    private readonly object _lock = new();
    private List<BoPublication> _publications = new();
    private List<BoPlanification> _planifications = new();
    private DateTime? _lastRefresh;
    private string? _lastError;

    public BoAuditCache(IBoPlatformClient bo, IOptions<BoSettings> settings, IWebHostEnvironment environment, ILogger<BoAuditCache> logger)
    {
        _bo = bo;
        _settings = settings.Value;
        _environment = environment;
        _logger = logger;
        LoadPersisted();
    }

    public DateTime? LastRefresh { get { lock (_lock) return _lastRefresh; } }
    public string? LastError { get { lock (_lock) return _lastError; } }
    public IReadOnlyList<BoPublication> Publications { get { lock (_lock) return _publications; } }
    public IReadOnlyList<BoPlanification> Planifications { get { lock (_lock) return _planifications; } }
    public BoPublication? Find(int id) { lock (_lock) return _publications.FirstOrDefault(p => p.Id == id); }
    public BoPlanification? FindPlanification(int id) { lock (_lock) return _planifications.FirstOrDefault(p => p.Id == id); }

    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await _bo.LogonAsync(ct);
            try
            {
                // Les planifications sont chargées en parallèle de l'enrichissement des publications.
                var planificationsTask = LoadPlanificationsAsync(token, ct);

                var publications = await CollectPublicationsAsync(token, ct);

                if (publications.Count == 0)
                {
                    var planificationsOnly = await planificationsTask;
                    lock (_lock)
                    {
                        _planifications = planificationsOnly;
                        _lastRefresh = DateTime.Now;
                        _lastError = null;
                    }
                    Persist();
                    await _bo.LogoffAsync(token, ct);
                    return new RefreshResult(false,
                        $"Aucune publication trouvée dans les dossiers publics (racine {_settings.PublicFoldersRootId})"
                        + $" ; {planificationsOnly.Count} planification(s) chargée(s).");
                }

                using var throttle = new SemaphoreSlim(4);
                var tasks = publications.Select(p => EnrichAsync(token, p, throttle, ct));
                await Task.WhenAll(tasks);

                var planifications = await planificationsTask;

                var sorted = publications
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (sorted.Count(p => !string.IsNullOrEmpty(p.Error)) == sorted.Count)
                {
                    await _bo.LogoffAsync(token, ct);
                    return new RefreshResult(false, "Aucune publication n'a pu être analysée (voir le détail des erreurs).");
                }

                lock (_lock)
                {
                    _publications = sorted;
                    _planifications = planifications;
                    _lastRefresh = DateTime.Now;
                    _lastError = null;
                }
                Persist();
                var errors = sorted.Count(p => !string.IsNullOrEmpty(p.Error));
                var message = $"{sorted.Count} publication(s) et {planifications.Count} planification(s) actualisée(s) le {DateTime.Now:dd/MM/yyyy HH:mm}."
                              + (errors > 0 ? $" {errors} publication(s) avec erreurs : ouvrez leur détail." : string.Empty);
                await _bo.LogoffAsync(token, ct);
                return new RefreshResult(true, message);
            }
            finally
            {
                await _bo.LogoffAsync(token, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Échec du rafraîchissement des données BO");
            lock (_lock) _lastError = ex.Message;
            return new RefreshResult(false, "Échec du rafraîchissement : " + ex.Message);
        }
    }

    /// <summary>Parcourt les dossiers publics de premier niveau et rassemble toutes les publications,
    /// avec le nom de leur dossier public de premier niveau (recherche récursive, avec pagination).</summary>
    private async Task<List<BoPublication>> CollectPublicationsAsync(string token, CancellationToken ct)
    {
        var publications = new List<BoPublication>();
        var visited = new HashSet<int>();

        var root = await _bo.GetObjectAsync(token, _settings.PublicFoldersRootId, ct)
                   ?? new BoObject { Id = _settings.PublicFoldersRootId, Name = "Dossiers publics" };
        var firstLevel = await GetChildrenAllPagesAsync(token, root.Id, ct);

        foreach (var folder in firstLevel.Where(o => o.Type.Equals("Folder", StringComparison.OrdinalIgnoreCase)))
            await ScanFolderAsync(token, folder.Id, folder.Name, publications, visited, ct);

        return publications;
    }

    private async Task ScanFolderAsync(string token, int folderId, string folderName,
        List<BoPublication> publications, HashSet<int> visited, CancellationToken ct)
    {
        if (!visited.Add(folderId)) return;

        foreach (var child in await GetChildrenAllPagesAsync(token, folderId, ct))
        {
            if (child.Type.Equals("Folder", StringComparison.OrdinalIgnoreCase))
                await ScanFolderAsync(token, child.Id, folderName, publications, visited, ct);
            else if (child.Type.Equals("Publication", StringComparison.OrdinalIgnoreCase))
                publications.Add(new BoPublication
                {
                    Id = child.Id,
                    Cuid = child.Cuid,
                    Name = child.Name,
                    Description = child.Description,
                    Author = child.Author,
                    Updated = child.Updated,
                    OpenDocumentLink = child.OpenDocumentLink,
                    Folder = folderName,
                });
        }
    }

    private async Task<List<BoObject>> GetChildrenAllPagesAsync(string token, int folderId, CancellationToken ct)
    {
        var result = new List<BoObject>();
        for (var page = 1; ; page++)
        {
            var children = await _bo.GetChildrenAsync(token, folderId, page, _settings.PageSize, ct);
            result.AddRange(children);
            if (children.Count < _settings.PageSize) return result;
        }
    }

    /// <summary>Récupère pour une publication : cmsquery (destinataires, rapport, planning) puis l'historique des instances.</summary>
    private async Task EnrichAsync(string token, BoPublication publication, SemaphoreSlim throttle, CancellationToken ct)
    {
        try
        {
            await throttle.WaitAsync(ct);
            try
            {
                var entry = await _bo.CmsQueryFirstAsync(token, $"SELECT * FROM CI_INFOOBJECTS WHERE SI_ID = {publication.Id}", ct);
                if (entry is { } cmsEntry)
                    ApplyCmsEntry(publication, cmsEntry);

                await LoadSourceDocumentsAsync(token, publication, ct);
                await LoadHistoryAsync(token, publication, ct);
            }
            finally
            {
                throttle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur d'enrichissement de la publication {Id}", publication.Id);
            publication.Error = ex.Message;
        }
    }

    /// <summary>Extrait les informations de la réponse CMS QueryBuilder (JSON).</summary>
    private void ApplyCmsEntry(BoPublication publication, JsonElement entry)
    {
        publication.Name = GetString(entry, "SI_NAME") ?? publication.Name;
        publication.Description = GetString(entry, "SI_DESCRIPTION") ?? publication.Description;
        publication.Cuid = GetString(entry, "SI_CUID") ?? publication.Cuid;
        publication.LastRunTime = BoPlatformClient.ParseBoDate(GetString(entry, "SI_LAST_RUN_TIME"));
        publication.LastSuccessfulInstanceId = (int?)GetNumber(entry, "SI_LAST_SUCCESSFUL_INSTANCE_ID");

        // Rapport source utilisé par la publication (SI_PUBLICATION_DOCUMENTS)
        var sourceIds = ReadIndexedIds(entry, "SI_PUBLICATION_DOCUMENTS");
        publication.SourceDocumentId = sourceIds.FirstOrDefault();

        // Rapport fournisseur des destinataires dynamiques (SI_PUBLICATION_EVENT_HANDLERS / SI_ON_READ_RECIPIENTS)
        var handlerPaths = new[]
        {
            new[] { "SI_PUBLICATION_EVENT_HANDLERS", "SI_ON_READ_RECIPIENTS" },
            new[] { "SI_ON_READ_RECIPIENTS" },
        };
        foreach (var handlerPath in handlerPaths)
        {
            foreach (var handler in ReadArray(entry, handlerPath))
            {
                var objId = GetNumber(handler, "SI_OBJID");
                if (objId != null)
                {
                    publication.RecipientsProviderDocumentId = (int)objId;
                    break;
                }
            }
            if (publication.RecipientsProviderDocumentId != null) break;
        }

        // Destinataires dynamiques : colonnes du rapport (email, nom complet, identifiant) et rapports source.
        // Le CMS renvoie ces données dans SI_DYNAMIC_RECIPIENTS_PROFILE_VALUE_MAPPINGS ;
        // SI_DYNAMIC_RECIPIENTS ne contient que SI_TOTAL.
        var dynMappings = entry.TryGetProperty("SI_DYNAMIC_RECIPIENTS_PROFILE_VALUE_MAPPINGS", out var dvm)
                          && dvm.ValueKind == JsonValueKind.Object ? dvm : (JsonElement?)null;
        if (dynMappings == null
            && entry.TryGetProperty("SI_DYNAMIC_RECIPIENTS", out var dyn0)
            && dyn0.ValueKind == JsonValueKind.Object)
            dynMappings = dyn0;

        if (dynMappings != null)
        {
            var dynamic = dynMappings.Value;
            var columns = new List<string>();
            AddIfPresent(columns, "E-mail", GetString(dynamic, "SI_EMAIL_ADDRESS"));
            AddIfPresent(columns, "Nom complet", GetString(dynamic, "SI_USERFULLNAME"));
            AddIfPresent(columns, "Identifiant", GetString(dynamic, "SI_USERNAME"));
            publication.DynamicRecipientColumns = columns;

            // Rapports source des mappings de destinataires dynamiques (SI_SOURCE_DOC_ID)
            foreach (var mapping in ReadArray(dynamic, "SI_PERDOC_DYNAMIC_RECIPIENTS_PROFILE_MAPPINGS"))
            {
                var sourceDocId = (int?)GetNumber(mapping, "SI_SOURCE_DOC_ID");
                if (sourceDocId != null && publication.RecipientsSources.All(s => s.Id != sourceDocId))
                    publication.RecipientsSources.Add(new BoRecipientsSource { Id = sourceDocId.Value });
            }
        }

        // Destinations SMTP : adresses statiques et CC
        foreach (var destination in ReadArray(entry, "SCHEDULING_INFO", "SI_DESTINATIONS"))
        {
            if (!destination.TryGetProperty("SI_DEST_SCHEDULEOPTIONS", out var options)) continue;
            CollectStaticEmails(publication, ReadValues(options, "SI_MAIL_ADDRESSES"));
            CollectStaticEmails(publication, ReadValues(options, "SI_MAIL_CC"));
        }

        // Planification : prochaine exécution et statut, si le CMS les renvoie dans SCHEDULING_INFO
        publication.NextRunTime = BoPlatformClient.ParseBoDate(GetString(entry, "SCHEDULING_INFO", "SI_NEXT_RUN_TIME")) ?? publication.NextRunTime;
        publication.ScheduleStatus = GetNumber(entry, "SCHEDULING_INFO", "SI_SCHEDULE_STATUS") is { } code
            ? BoPlatformClient.DescribeScheduleStatus((int)code)
            : publication.ScheduleStatus;
    }

    private static void CollectStaticEmails(BoPublication publication, IEnumerable<string> addresses)
    {
        foreach (var value in NormalizeAddresses(addresses))
        {
            if (value.StartsWith('%') && value.EndsWith('%'))
            {
                publication.UsesDynamicSmtpRecipients = true;
                continue;
            }
            if (value.Contains('@') && !publication.StaticEmails.Contains(value, StringComparer.OrdinalIgnoreCase))
                publication.StaticEmails.Add(value);
        }
    }

    private static void CollectStaticEmails(BoPlanification planification, IEnumerable<string> addresses)
    {
        foreach (var value in NormalizeAddresses(addresses))
        {
            if (value.StartsWith('%') && value.EndsWith('%'))
            {
                planification.UsesDynamicSmtpRecipients = true;
                continue;
            }
            if (value.Contains('@') && !planification.StaticEmails.Contains(value, StringComparer.OrdinalIgnoreCase))
                planification.StaticEmails.Add(value);
        }
    }

    /// <summary>Nettoie les adresses brutes (trim, ignore les vides) ; logique partagée par les deux collectes.</summary>
    private static IEnumerable<string> NormalizeAddresses(IEnumerable<string> addresses)
        => addresses.Select(a => a.Trim().ToLower()).Where(a => a.Length > 0);

    /// <summary>Résout le nom (et le lien OpenDocument) des rapports liés à la publication, et pour les
    /// destinataires dynamiques le nom et le dossier de chaque rapport source (requêtes infostore).</summary>
    private async Task LoadSourceDocumentsAsync(string token, BoPublication publication, CancellationToken ct)
    {
        var ids = new List<int>();
        if (publication.SourceDocumentId != null) ids.Add(publication.SourceDocumentId.Value);
        if (publication.RecipientsProviderDocumentId != null
            && !ids.Contains(publication.RecipientsProviderDocumentId.Value))
            ids.Add(publication.RecipientsProviderDocumentId.Value);
        ids.AddRange(publication.RecipientsSources.Select(s => s.Id).Where(id => !ids.Contains(id)));

        var objects = new Dictionary<int, BoObject>();
        async Task<BoObject?> LoadObjectAsync(int id)
        {
            if (objects.TryGetValue(id, out var cached)) return cached;
            var obj = await _bo.GetObjectAsync(token, id, ct);
            if (obj != null) objects[id] = obj;
            return obj;
        }

        foreach (var id in ids)
        {
            var obj = await LoadObjectAsync(id);
            if (obj == null) continue;
            if (publication.SourceDocumentId == id)
            {
                publication.SourceDocumentName = obj.Name;
                publication.SourceDocumentOpenLink = obj.OpenDocumentLink;
            }
            if (publication.RecipientsProviderDocumentId == id)
                publication.RecipientsProviderDocumentName = obj.Name;
        }

        // Destinataires dynamiques : nom du rapport source et nom de son dossier parent
        foreach (var source in publication.RecipientsSources)
        {
            var obj = await LoadObjectAsync(source.Id);
            if (obj == null) continue;
            source.Name = obj.Name;
            if (obj.ParentId is { } parentId && await LoadObjectAsync(parentId) is { } folder)
                source.Folder = folder.Name;
        }
    }

    /// <summary>Historique des instances (méthode « Historique des Instances » du document de référence) :
    /// une seule requête cmsquery SI_ANCESTOR renvoie les 20 dernières instances avec SI_STARTTIME, SI_ENDTIME et SI_SCHEDULE_STATUS.</summary>
    private async Task LoadHistoryAsync(string token, BoPublication publication, CancellationToken ct)
    {
        try
        {
            var query = "SELECT TOP 20 SI_ID,SI_NAME,SI_STARTTIME,SI_ENDTIME,SI_SCHEDULE_STATUS FROM CI_INFOOBJECTS "
                        + $"WHERE SI_INSTANCE=1 AND SI_KIND = 'Publication' AND SI_ANCESTOR = {publication.Id}";
            var entries = await _bo.CmsQueryAsync(token, query, ct);

            var instances = entries
                .Select(ParseInstance)
                .Where(i => i.Id != 0)
                .OrderByDescending(i => i.StartTime ?? i.EndTime)
                .ToList();

            publication.History = instances;

            var last = instances.FirstOrDefault();
            if (last != null)
            {
                publication.LastRunTime ??= last.EndTime ?? last.StartTime;
                publication.LastRunStatus ??= last.Status;

                if (publication.LastSuccessfulInstanceId is { } id)
                {
                    var obj = await _bo.GetObjectAsync(token, id, ct);
                    publication.LastInstanceOpenLink ??= obj?.OpenDocumentLink;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historique indisponible pour la publication {Id}", publication.Id);
        }
    }

    /// <summary>Construit une instance depuis une entrée cmsquery (champs SI_*).</summary>
    private static BoInstance ParseInstance(JsonElement entry)
    {
        var start = BoPlatformClient.ParseBoDate(GetString(entry, "SI_STARTTIME"));
        var end = BoPlatformClient.ParseBoDate(GetString(entry, "SI_ENDTIME"));
        var status = GetNumber(entry, "SI_SCHEDULE_STATUS") is { } code
            ? BoPlatformClient.DescribeScheduleStatus((int)code)
            : null;
        return new BoInstance
        {
            Id = (int?)GetNumber(entry, "SI_ID") ?? 0,
            Name = GetString(entry, "SI_NAME") ?? string.Empty,
            StartTime = start,
            EndTime = end,
            Updated = end ?? start,
            Status = status,
        };
    }

    /// <summary>Charge les planifications : documents Webi planifiés (méthode « Planifications » du document de
    /// référence : SI_HIDDEN_OBJECT = 0, SI_IS_SCHEDULABLE = -1, SI_KIND = 'Webi', SI_LAST_SUCCESSFUL_INSTANCE_ID &gt; 0),
    /// avec récurrence, format et destinataires. Les documents dont SCHEDULING_INFO/SI_ENDTIME est
    /// antérieure à 30 jours par rapport à la date du jour sont ignorés.</summary>
    private async Task<List<BoPlanification>> LoadPlanificationsAsync(string token, CancellationToken ct)
    {
        try
        {
            var query = "SELECT * FROM CI_INFOOBJECTS WHERE SI_HIDDEN_OBJECT=0 AND SI_IS_SCHEDULABLE=-1 AND SI_KIND='Webi' AND SI_LAST_SUCCESSFUL_INSTANCE_ID>0";
            var entries = await _bo.CmsQueryAllPagesAsync(token, query, ct);

            var minEndTime = DateTime.Now.AddDays(-30);
            var planifications = new List<BoPlanification>();
            foreach (var entry in entries)
            {
                var planification = ParsePlanification(entry);
                if (planification.Id == 0) continue;

                // Les planifications échues depuis plus de 30 jours ne sont plus actives : on les écarte.
                if (planification.EndTime is { } end && end < minEndTime) continue;

                planifications.Add(planification);
            }

            await ResolveFolderNamesAsync(token, planifications, ct);

            // Historique des instances, chargé en parallèle (limité) comme pour les publications.
            using var throttle = new SemaphoreSlim(4);
            await Task.WhenAll(planifications.Select(p => LoadPlanificationHistoryAsync(token, p, throttle, ct)));

            return planifications.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chargement des planifications en échec");
            return new List<BoPlanification>();
        }
    }

    /// <summary>Historique des instances d'un document planifié : requête cmsquery SI_ANCESTOR
    /// renvoyant les 20 dernières instances avec SI_STARTTIME, SI_ENDTIME et SI_SCHEDULE_STATUS.</summary>
    private async Task LoadPlanificationHistoryAsync(string token, BoPlanification planification, SemaphoreSlim throttle, CancellationToken ct)
    {
        try
        {
            await throttle.WaitAsync(ct);
            try
            {
                var query = "SELECT TOP 20 SI_ID,SI_NAME,SI_STARTTIME,SI_ENDTIME,SI_SCHEDULE_STATUS FROM CI_INFOOBJECTS "
                            + $"WHERE SI_INSTANCE=1 AND SI_KIND = 'Webi' AND SI_ANCESTOR = {planification.Id}";
                var entries = await _bo.CmsQueryAsync(token, query, ct);

                planification.History = entries
                    .Select(ParseInstance)
                    .Where(i => i.Id != 0)
                    .OrderByDescending(i => i.StartTime ?? i.EndTime)
                    .ToList();

                var last = planification.History.FirstOrDefault();
                if (last != null)
                {
                    planification.LastRunTime ??= last.EndTime ?? last.StartTime;
                    planification.LastRunStatus ??= last.Status;
                }
            }
            finally
            {
                throttle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historique indisponible pour la planification {Id}", planification.Id);
        }
    }

    /// <summary>Construit une planification depuis une entrée cmsquery (champs SI_*).</summary>
    private static BoPlanification ParsePlanification(JsonElement entry)
    {
        var planification = new BoPlanification
        {
            Id = (int?)GetNumber(entry, "SI_ID") ?? 0,
            Cuid = GetString(entry, "SI_CUID") ?? string.Empty,
            Name = GetString(entry, "SI_NAME") ?? string.Empty,
            Description = GetString(entry, "SI_DESCRIPTION") ?? string.Empty,
            Author = GetString(entry, "SI_AUTHOR") ?? GetString(entry, "SI_OWNER") ?? string.Empty,
            Updated = BoPlatformClient.ParseBoDate(GetString(entry, "SI_UPDATE_TS")),
            InstanceCount = (int?)GetNumber(entry, "SI_CHILDREN") ?? 0,
            LastRunTime = BoPlatformClient.ParseBoDate(GetString(entry, "SI_LAST_RUN_TIME")),
            EndTime = BoPlatformClient.ParseBoDate(GetString(entry, "SCHEDULING_INFO", "SI_ENDTIME")),
            ParentFolderId = (int?)GetNumber(entry, "SI_PARENT_FOLDER"),
            Recurrence = DescribeRecurrence(entry),
            Format = GetString(entry, "PROCESSING_INFO", "SI_FORMAT_INFO", "SI_FORMAT")
                     ?? FormatFromProgid(GetString(entry, "SCHEDULING_INFO", "SI_PROGID_SCHEDULE")),
            HasPrompts = GetBool(entry, "PROCESSING_INFO", "SI_HAS_PROMPTS"),
        };

        // Destinations SMTP : adresses statiques et CC
        foreach (var destination in ReadArray(entry, "SCHEDULING_INFO", "SI_DESTINATIONS"))
        {
            if (!destination.TryGetProperty("SI_DEST_SCHEDULEOPTIONS", out var options)) continue;
            CollectStaticEmails(planification, ReadValues(options, "SI_MAIL_ADDRESSES"));
            CollectStaticEmails(planification, ReadValues(options, "SI_MAIL_CC"));
        }

        return planification;
    }

    /// <summary>Résout le nom des dossiers parents des planifications par lots (requêtes cmsquery).</summary>
    private async Task ResolveFolderNamesAsync(string token, List<BoPlanification> planifications, CancellationToken ct)
    {
        var parentIds = planifications
            .Select(p => p.ParentFolderId)
            .Where(id => id is { } && id != 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (parentIds.Count == 0) return;

        var names = new Dictionary<int, string>();
        foreach (var chunk in parentIds.Chunk(50))
        {
            try
            {
                var query = "SELECT SI_ID, SI_NAME FROM CI_INFOOBJECTS WHERE SI_ID IN ("
                            + string.Join(",", chunk) + ")";
                foreach (var entry in await _bo.CmsQueryAsync(token, query, ct))
                {
                    var id = (int?)GetNumber(entry, "SI_ID");
                    var name = GetString(entry, "SI_NAME");
                    if (id != null && !string.IsNullOrEmpty(name)) names[id.Value] = name;
                }
            }
            catch (Exception ex)
            {
                var doss = string.Join(",", chunk);
                names[chunk[0]] = $"Dossier(s) {doss} - non trouvé dans la CMC /!\\";
                _logger.LogWarning(ex, "Résolution des noms de dossiers en échec pour {Count} dossier(s)", chunk.Length);
            }
        }

        foreach (var planification in planifications)
        {
            if (planification.ParentFolderId is { } parentId && names.TryGetValue(parentId, out var folderName))
                planification.Folder = folderName;
        }
    }

    /// <summary>Décrit la récurrence de la planification (type, intervalles, jours du modèle).</summary>
    private static string? DescribeRecurrence(JsonElement entry)
    {
        if (!entry.TryGetProperty("SCHEDULING_INFO", out var sched) || sched.ValueKind != JsonValueKind.Object)
            return null;
        if ((int?)GetNumber(sched, "SI_SCHEDULE_TYPE") is not { } type)
            return null;

        var parts = new List<string> { DescribeScheduleType(type) };
        if (GetNumber(sched, "SI_SCHEDULE_INTERVAL_HOURS") is { } hours && hours > 0)
            parts.Add($"toutes les {hours} h");
        if (GetNumber(sched, "SI_SCHEDULE_INTERVAL_MINUTES") is { } minutes && minutes > 0)
            parts.Add($"toutes les {minutes} min");
        if (GetNumber(sched, "SI_SCHEDULE_INTERVAL_MONTHS") is { } months && months > 1)
            parts.Add($"tous les {months} mois");
        if (GetNumber(sched, "SI_SCHEDULE_INTERVAL_NTHDAY") is { } nth && nth > 0)
            parts.Add($"le {nth}-ème jour");

        if (Navigate(sched, new[] { "SI_RUN_ON_TEMPLATE", "SI_TEMPLATE_DAY1", "SI_DAYS_OF_WEEK" }) is { } days
            && days.TryGetInt32(out var mask) && mask > 0)
            parts.Add("jours : " + string.Join(", ", DecodeDaysOfWeek(mask)));

        return string.Join(" — ", parts);
    }

    private static string DescribeScheduleType(int type) => type switch
    {
        0 => "Une fois (immédiat)",
        1 => "Toutes les heures",
        2 => "Tous les jours",
        3 => "Toutes les semaines",
        4 => "Tous les mois",
        5 => "Mensuel (N-ième jour)",
        8 => "Calendrier (jours définis)",
        _ => $"Type {type}",
    };

    /// <summary>Décode le masque SI_DAYS_OF_WEEK : bit 1 = dimanche ... bit 64 = samedi.</summary>
    private static string[] DecodeDaysOfWeek(int mask)
    {
        string[] days = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };
        return days.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
    }

    private static string? FormatFromProgid(string? progid)
        => string.IsNullOrEmpty(progid) || !progid.Contains('.') ? progid : progid.Split('.').Last().ToUpperInvariant();

    // ------------------------------------------------------------------ helpers JSON

    private static string? GetString(JsonElement element, params string[] path)
    {
        var value = Navigate(element, path);
        return value is { } v
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString() : null;
    }

    private static double? GetNumber(JsonElement element, params string[] path)
        => Navigate(element, path) is { } v
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) ? d : null;

    private static bool GetBool(JsonElement element, params string[] path)
        => Navigate(element, path) is { } v
            && (v.ValueKind == JsonValueKind.True
                || (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d != 0));

    private static JsonElement? Navigate(JsonElement element, string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
                return null;
            current = next;
        }
        return current;
    }

    /// <summary>Itère les sous-objets numérotés d'une propriété ("1", "2", ...) ou d'un tableau.</summary>
    private static IEnumerable<JsonElement> ReadArray(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next))
                yield break;
            current = next;
        }

        if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in current.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
            yield break;
        }

        if (current.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in current.EnumerateObject())
        {
            if (property.NameEquals("SI_TOTAL")) continue;
            if (property.Value.ValueKind == JsonValueKind.Object)
                yield return property.Value;
        }
    }

    /// <summary>Itère les valeurs (scalars) numérotées d'une propriété ("1", "2", ...), ex. adresses e-mail.</summary>
    private static IEnumerable<string> ReadValues(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var values))
            yield break;

        if (values.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in values.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    yield return item.GetString() ?? string.Empty;
            yield break;
        }

        if (values.ValueKind == JsonValueKind.String)
        {
            yield return values.GetString() ?? string.Empty;
            yield break;
        }

        if (values.ValueKind != JsonValueKind.Object) yield break;
        foreach (var entryValue in values.EnumerateObject())
        {
            if (entryValue.NameEquals("SI_TOTAL")) continue;
            if (entryValue.Value.ValueKind == JsonValueKind.String)
                yield return entryValue.Value.GetString() ?? string.Empty;
        }
    }

    private static List<int> ReadIndexedIds(JsonElement element, string property)
        => ReadArray(element, property)
            .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null)
            .Where(i => i != null)
            .Select(i => i!.Value)
            .ToList();

    private static void AddIfPresent(List<string> target, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target.Add($"{label} : « {value.Trim()} »");
    }

    // ------------------------------------------------------------------ persistance

    private sealed class Snapshot
    {
        public DateTime? LastRefresh { get; set; }
        public List<BoPublication> Publications { get; set; } = new();
        public List<BoPlanification> Planifications { get; set; } = new();
    }

    private string CachePath => Path.Combine(_environment.ContentRootPath, "App_Data", "bo-cache.json");

    private void Persist()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var snapshot = new Snapshot { LastRefresh = _lastRefresh, Publications = _publications, Planifications = _planifications };
                File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de persister le cache BO ({Path})", CachePath);
        }
    }

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CachePath));
            if (snapshot == null) return;
            lock (_lock)
            {
                _publications = snapshot.Publications;
                _planifications = snapshot.Planifications;
                _lastRefresh = snapshot.LastRefresh;
            }
            _logger.LogInformation("Cache BO initialisé depuis {Path} : {Count} publication(s), {Planifications} planification(s), dernière actualisation {LastRefresh}",
                CachePath, snapshot.Publications.Count, snapshot.Planifications.Count, snapshot.LastRefresh);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache BO illisible ({Path})", CachePath);
        }
    }
}
