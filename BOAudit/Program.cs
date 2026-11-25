using System.Globalization;
using BOAudit.Authorization;
using BOAudit.Data;
using BOAudit.Models;
using BOAudit.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Culture française : dates et nombres affichés au format local.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("fr-FR");
    options.SupportedCultures = [new CultureInfo("fr-FR")];
    options.SupportedUICultures = [new CultureInfo("fr-FR")];
});

builder.Services.AddHttpClient<IBoPlatformClient, BoPlatformClient>()
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<BoSettings>>().Value;
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("BO.Http")
        .LogInformation("Client BO : proxy {ProxyState} (vers {BaseUrl})",
        settings.UseProxy ? "système activé" : "désactivé - connexion directe", settings.BaseUrl);
        return new HttpClientHandler { UseProxy = settings.UseProxy };


    });
// Base SQL des destinataires (Entity Framework)
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ApplicationDbContext")));

// Plateforme BO
builder.Services.Configure<BoSettings>(builder.Configuration.GetSection(BoSettings.Section));
builder.Services.Configure<SecuritySettings>(builder.Configuration.GetSection(SecuritySettings.Section));
builder.Services.AddSingleton<IBoAuditCache, BoAuditCache>();
builder.Services.AddHttpClient<IBoPlatformClient, BoPlatformClient>();

// Authentification Windows (IIS Express ou Kestrel + Negotiate)
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

// Toutes les pages exigent : utilisateur Windows authentifié + membre d'un groupe appconfig.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new BoAuditGroupRequirement())
        .Build();
});
builder.Services.AddSingleton<IAuthorizationHandler, BoAuditGroupHandler>();

builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseRequestLocalization();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// En développement : crée automatiquement la base et la table des destinataires
// (sans bloquer le démarrage si SQL Server est momentanément inaccessible).
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Base des destinataires injoignable au démarrage : créez-la avant d'utiliser l'enrichissement des e-mails.");
    }
}

app.Run();
