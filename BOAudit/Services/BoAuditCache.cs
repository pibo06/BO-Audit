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
    BoPublication? Find(int id);
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
    public BoPublication? Find(int id) { lock (_lock) return _publications.FirstOrDefault(p => p.Id == id); }

    public async Task<RefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await _bo.LogonAsync(ct);
            try
            {
                var children = await _bo.GetChildrenAsync(token, _settings.PublicationsFolderId, pageSize: _settings.PageSize, ct: ct);
                var publications = children
                    .Where(o => o.Type.Equals("Publication", StringComparison.OrdinalIgnoreCase))
                    .Select(o => new BoPublication
                    {
                        Id = o.Id,
                        Cuid = o.Cuid,
                        Name = o.Name,
                        Description = o.Description,
                        Author = o.Author,
                        Updated = o.Updated,
                        OpenDocumentLink = o.OpenDocumentLink,
                    })
                    .ToList();

                if (publications.Count == 0)
                {
                    await _bo.LogoffAsync(token, ct);
                    return new RefreshResult(false, $"Aucune publication trouvée dans le dossier {_settings.PublicationsFolderId} ({children.Count} objets).");
                }

                using var throttle = new SemaphoreSlim(4);
                var tasks = publications.Select(p => EnrichAsync(token, p, throttle, ct));
                await Task.WhenAll(tasks);

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
                    _lastRefresh = DateTime.Now;
                    _lastError = null;
                }
                Persist();
                var errors = sorted.Count(p => !string.IsNullOrEmpty(p.Error));
                var message = $"{sorted.Count} publication(s) actualisée(s) le {DateTime.Now:dd/MM/yyyy HH:mm}."
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

    /// <summary>Récupère pour une publication : cmsquery (destinataires, rapport, dernière exécution), planning et historique.</summary>
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

                var schedules = await _bo.GetSchedulesAsync(token, publication.Id, ct);
                publication.Schedules = schedules;
                publication.ActiveSchedule = PickSchedule(schedules);
                if (publication.NextRunTime == null && publication.ActiveSchedule != null)
                    publication.NextRunTime = publication.ActiveSchedule.NextRunTime ?? publication.ActiveSchedule.StartTime;
                if (publication.ScheduleStatus == null && publication.ActiveSchedule?.Status != null)
                    publication.ScheduleStatus = publication.ActiveSchedule.Status;

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

        // Rapport fournisseur des destinataires dynamiques (SI_ON_READ_RECIPIENTS)
        foreach (var handler in ReadArray(entry, "SI_ON_READ_RECIPIENTS"))
        {
            var objId = GetNumber(handler, "SI_OBJID");
            if (objId != null)
            {
                publication.RecipientsProviderDocumentId = (int)objId;
                break;
            }
        }

        // Destinataires dynamiques : colonnes du rapport (email, nom complet, identifiant)
        var dynamic = entry.TryGetProperty("SI_DYNAMIC_RECIPIENTS", out var dyn) ? dyn : (JsonElement?)null;
        if (dynamic != null && dynamic.Value.ValueKind == JsonValueKind.Object)
        {
            var columns = new List<string>();
            AddIfPresent(columns, "E-mail", GetString(dynamic.Value, "SI_EMAIL_ADDRESS"));
            AddIfPresent(columns, "Nom complet", GetString(dynamic.Value, "SI_USERFULLNAME"));
            AddIfPresent(columns, "Identifiant", GetString(dynamic.Value, "SI_USERNAME"));
            publication.DynamicRecipientColumns = columns;
        }

        // Destinations SMTP : adresses statiques et CC
        foreach (var destination in ReadArray(entry, "SCHEDULING_INFO", "SI_DESTINATIONS"))
        {
            if (!destination.TryGetProperty("SI_DEST_SCHEDULEOPTIONS", out var options)) continue;
            CollectStaticEmails(publication, ReadValues(options, "SI_MAIL_ADDRESSES"));
            CollectStaticEmails(publication, ReadValues(options, "SI_MAIL_CC"));
        }
    }

    private void CollectStaticEmails(BoPublication publication, IEnumerable<string> addresses)
    {
        foreach (var raw in addresses)
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (value.StartsWith('%') && value.EndsWith('%'))
            {
                publication.UsesDynamicSmtpRecipients = true;
                continue;
            }
            if (value.Contains('@') && !publication.StaticEmails.Contains(value, StringComparer.OrdinalIgnoreCase))
                publication.StaticEmails.Add(value);
        }
    }

    /// <summary>Résout le nom (et le lien OpenDocument) des rapports liés à la publication.</summary>
    private async Task LoadSourceDocumentsAsync(string token, BoPublication publication, CancellationToken ct)
    {
        var ids = new List<int>();
        if (publication.SourceDocumentId != null) ids.Add(publication.SourceDocumentId.Value);
        if (publication.RecipientsProviderDocumentId != null
            && !ids.Contains(publication.RecipientsProviderDocumentId.Value))
            ids.Add(publication.RecipientsProviderDocumentId.Value);

        foreach (var id in ids)
        {
            var obj = await _bo.GetObjectAsync(token, id, ct);
            if (obj == null) continue;
            if (publication.SourceDocumentId == id)
            {
                publication.SourceDocumentName = obj.Name;
                publication.SourceDocumentOpenLink = obj.OpenDocumentLink;
            }
            if (publication.RecipientsProviderDocumentId == id)
                publication.RecipientsProviderDocumentName = obj.Name;
        }
    }

    /// <summary>Historique des instances : la plus récente donne l'état du dernier envoi.</summary>
    private async Task LoadHistoryAsync(string token, BoPublication publication, CancellationToken ct)
    {
        try
        {
            var instances = await _bo.GetChildrenAsync(token, publication.Id, pageSize: 50, ct: ct);
            publication.History = instances
                .OrderByDescending(i => i.Updated)
                .Take(10)
                .Select(i => new BoInstance
                {
                    Id = i.Id,
                    Cuid = i.Cuid,
                    Name = i.Name,
                    Updated = i.Updated,
                    OpenDocumentLink = i.OpenDocumentLink,
                })
                .ToList();

            var last = publication.History.FirstOrDefault();
            if (last != null)
            {
                publication.LastRunTime ??= last.Updated;
                if (last.OpenDocumentLink != null) publication.LastInstanceOpenLink = last.OpenDocumentLink;
                var status = await GetInstanceStatusAsync(token, last.Id, ct);
                last.Status = status;
                publication.LastRunStatus ??= status;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Historique indisponible pour la publication {Id}", publication.Id);
        }
    }

    /// <summary>Lit le SI_SCHEDULE_STATUS de l'instance (requête CMS) pour connaître l'issue du dernier envoi.</summary>
    private async Task<string?> GetInstanceStatusAsync(string token, int instanceId, CancellationToken ct)
    {
        var entry = await _bo.CmsQueryFirstAsync(
            token,
            $"SELECT SI_ID, SI_NAME, SI_UPDATE_TS, SI_START_TIME, SI_END_TIME FROM CI_INFOOBJECTS WHERE SI_ID = {instanceId}",
            ct);
        if (entry == null) return null;

        if (entry.Value.TryGetProperty("SCHEDULING_INFO", out var scheduling)
            && scheduling.ValueKind == JsonValueKind.Object
            && scheduling.TryGetProperty("SI_SCHEDULE_STATUS", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.Number)
            return BoPlatformClient.DescribeScheduleStatus(statusEl.GetInt32());

        return null;
    }

    /// <summary>Choisit la planification dont la prochaine exécution est la plus proche.</summary>
    private static BoSchedule? PickSchedule(List<BoSchedule> schedules)
    {
        if (schedules.Count == 0) return null;
        var now = DateTime.Now;
        return schedules.Where(s => (s.NextRunTime ?? s.StartTime) >= now)
            .OrderBy(s => s.NextRunTime ?? s.StartTime)
            .FirstOrDefault()
            ?? schedules.OrderBy(s => s.NextRunTime ?? s.StartTime).First();
    }

    // ------------------------------------------------------------------ helpers JSON

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString() : null;

    private static double? GetNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var d) ? d : null;

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
    }

    private string CachePath => Path.Combine(_environment.ContentRootPath, "App_Data", "bo-cache.json");

    private void Persist()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var snapshot = new Snapshot { LastRefresh = _lastRefresh, Publications = _publications };
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
                _lastRefresh = snapshot.LastRefresh;
            }
            _logger.LogInformation("Cache BO initialisé depuis {Path} : {Count} publication(s), dernière actualisation {LastRefresh}",
                CachePath, snapshot.Publications.Count, snapshot.LastRefresh);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache BO illisible ({Path})", CachePath);
        }
    }
}
