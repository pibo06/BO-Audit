using Azure;
using BOAudit.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace BOAudit.Services;

/// <summary>Implémentation du client REST BO Platform (d'après « Requetes REST et QUERYBUILDER pour Audit.txt »).</summary>
public sealed class BoPlatformClient : IBoPlatformClient
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Bip = "http://www.sap.com/rws/bip";

    /// <summary>Correspondance des codes SI_SCHEDULE_STATUS des instances (statuts du planificateur BO).</summary>
    private static readonly Dictionary<int, string> ScheduleStatusTexts = new()
    {
        [0] = "Sans statut",
        [1] = "Terminé",
        [2] = "Échec",
        [3] = "En cours",
        [4] = "Interrompu",
        [5] = "En suspens",
        [8] = "Stoppé",
    };

    private static readonly CultureInfo FrCulture = new("fr-FR");

    private readonly HttpClient _http;
    private readonly BoSettings _settings;
    private readonly ILogger<BoPlatformClient> _logger;

    public BoPlatformClient(HttpClient http, IOptions<BoSettings> settings, ILogger<BoPlatformClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(1);
    }

    public async Task<string> LogonAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "biprws/logon/long")
        {
            Content = JsonContent.Create(new
            {
                userName = _settings.UserName,
                password = _settings.Password,
                auth = _settings.Auth,
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage? response = null;
        string token = "";
        try
        {
            response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            var t = ex.Message;
            _logger.LogError("Send POST Logon request : {t}", t);

        }

        if (response != null)
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            token = doc.RootElement.GetProperty("logonToken").GetString();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Le serveur BO n'a pas renvoyé de jeton de session (logonToken).");
        }
        return token;
    }

    public async Task LogoffAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "biprws/logoff");
            request.Headers.TryAddWithoutValidation("X-SAP-LogonToken", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogError("Logoff BO en échec : {StatusCode}", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors du logoff BO (session abandonnée)");
        }
    }

    public async Task<List<BoObject>> GetChildrenAsync(string token, int folderId, int page = 1, int? pageSize = null, CancellationToken ct = default)
    {
        var url = $"biprws/v1/infostore/{folderId}/children?page={page}&pagesize={pageSize ?? _settings.PageSize}";
        return await GetFeedAsync(token, url, ct);
    }

    public async Task<BoObject?> GetObjectAsync(string token, int id, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"biprws/v1/infostore/{id}");
        request.Headers.TryAddWithoutValidation("X-SAP-LogonToken", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var xml = await response.Content.ReadAsStringAsync(ct);
        return ParseEntry(XDocument.Parse(xml).Root);
    }

    public async Task<List<JsonElement>> CmsQueryAsync(string token, string query, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "biprws/v1/cmsquery?page=1&pagesize=150")
        {
            Content = JsonContent.Create(new { query })
        };
        request.Headers.TryAddWithoutValidation("X-SAP-LogonToken", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var entries = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var entry in array.EnumerateArray())
                entries.Add(entry.Clone());
        return entries;
    }

    public async Task<JsonElement?> CmsQueryFirstAsync(string token, string query, CancellationToken ct = default)
    {
        var entries = await CmsQueryAsync(token, query, ct);
        return entries.Count > 0 ? entries[0] : null;
    }

    public async Task<List<JsonElement>> CmsQueryAllPagesAsync(string token, string query, CancellationToken ct = default)
    {
        var entries = new List<JsonElement>();
        var next = await RunCmsQueryAsync(token, HttpMethod.Post, "biprws/v1/cmsquery?page=1&pagesize=200", query, entries, ct);

        // Suit les pages suivantes renvoyées par le QueryBuilder (URI « next.__deferred »).
        for (var page = 2; next != null && page <= 100; page++)
        {
            next = await RunCmsQueryAsync(token, HttpMethod.Get, next, query, entries, ct);
        }
        return entries;
    }

    /// <summary>Exécute une requête cmsquery (POST initial ou GET de page suivante), ajoute les entrées
    /// et retourne l'URI de la page suivante, sinon null.</summary>
    private async Task<string?> RunCmsQueryAsync(string token, HttpMethod method, string path, string? query,
        List<JsonElement> entries, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-SAP-LogonToken", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (query != null)
            request.Content = JsonContent.Create(new { query });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var entry in array.EnumerateArray())
                entries.Add(entry.Clone());

        return root.TryGetProperty("next", out var next)
               && next.TryGetProperty("__deferred", out var deferred)
               && deferred.TryGetProperty("uri", out var uri)
               && uri.GetString() is { } nextUri
            ? _http.BaseAddress != null && nextUri.StartsWith(_http.BaseAddress.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                ? nextUri[_http.BaseAddress.AbsoluteUri.Length..]
                : nextUri
            : null;
    }

    // ------------------------------------------------------------------ parsing

    private async Task<List<BoObject>> GetFeedAsync(string token, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-SAP-LogonToken", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct);
        var feed = XDocument.Parse(xml).Root;
        var result = new List<BoObject>();
        if (feed == null) return result;

        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            var obj = ParseEntry(entry) ?? new BoObject();
            result.Add(obj);
        }
        return result;
    }

    /// <summary>Construit un BoObject depuis une entrée Atom (publication, instance, rapport...).</summary>
    private static BoObject? ParseEntry(XElement? entry)
    {
        if (entry == null) return null;
        var attrs = ParseAttrs(entry.Element(Atom + "content"));
        var id = TryParseInt(attrs.TryGetValue("id", out var idAttr) ? idAttr : null);
        if (id == null)
            id = TryParseInt(GetLink(entry, "alternate")?.Split('/').LastOrDefault());
        if (id == null) return null;

        return new BoObject
        {
            Id = id.Value,
            ParentId = TryParseInt(GetLink(entry, "up")?.Split('/').LastOrDefault()),
            Cuid = Attr(attrs, "cuid"),
            Name = TryGet(attrs, "name") ?? entry.Element(Atom + "title")?.Value ?? string.Empty,
            Description = Attr(attrs, "description"),
            Type = TryGet(attrs, "type") ?? string.Empty,
            Author = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? string.Empty,
            Updated = TryParseIsoDate(entry.Element(Atom + "updated")?.Value),
            OpenDocumentLink = Attr(attrs, "opendoclink") ?? GetLink(entry, "opendocument"),
        };
    }

    /// <summary>Extrait les attributs <c>attr name="..."</c> du contenu XML d'une entrée.</summary>
    private static Dictionary<string, string> ParseAttrs(XElement? content)
    {
        var attrs = content?.Element(Bip + "attrs");
        if (attrs == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return attrs.Elements()
            .ToDictionary(
                a => (string?)a.Attribute("name") ?? string.Empty,
                a => a.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetLink(XElement entry, string relSuffix)
        => entry.Elements(Atom + "link")
            .Where(l => ((string?)l.Attribute("rel"))?.EndsWith(relSuffix, StringComparison.OrdinalIgnoreCase) == true
                        || string.Equals(((string?)l.Attribute("rel")), relSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(l => (string?)l.Attribute("href"))
            .FirstOrDefault();

    private static string Attr(IReadOnlyDictionary<string, string> attrs, string name)
        => attrs.TryGetValue(name, out var value) ? value : string.Empty;

    private static string? TryGet(IReadOnlyDictionary<string, string> attrs, string name)
        => attrs.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static int? TryParseInt(string? value)
        => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>Les dates du CMS QueryBuilder sont au format français « 30 nov. 2026 06:00 » ; celles d'infostore en ISO-8601.</summary>
    public static DateTime? ParseBoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string format = "d MMM yyyy HH:mm";
        if (DateTime.TryParseExact(value.Trim(), format, FrCulture, DateTimeStyles.None, out var dt))
            return dt;
        if (DateTimeOffset.TryParse(value, FrCulture, DateTimeStyles.None, out var dto))
            return dto.LocalDateTime;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso.LocalDateTime;
        return null;
    }

    private static DateTime? TryParseIsoDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.LocalDateTime
            : null;

    public static string DescribeScheduleStatus(int code)
        => ScheduleStatusTexts.TryGetValue(code, out var text) ? text : $"Statut BO {code}";

    /// <summary>Statuts connus des instances, reconnus à partir des libellés renvoyés par BO.</summary>
    public static string DescribeStatusLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Inconnu";
        var t = text.Trim().ToLowerInvariant();
        if (t.Contains("échec") || t.Contains("echec") || t.Contains("fail")) return "Échec";
        if (t.Contains("termin") || t.Contains("complet") || t.Contains("succ")) return "Terminé";
        if (t.Contains("cours") || t.Contains("run") || t.Contains("execut") || t.Contains("exécut")) return "En cours";
        if (t.Contains("suspens") || t.Contains("pending") || t.Contains("attente")) return "En suspens";
        if (t.Contains("pause")) return "En pause";
        if (t.Contains("annul") || t.Contains("stop")) return "Annulé";
        return text.Trim();
    }
}
