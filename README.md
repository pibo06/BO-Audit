# BOAudit — Suivi des publications SAP BusinessObjects

Application web ASP.NET Core 8 (Razor Pages, C#) pour centraliser le suivi des publications BO :
pour chaque publication active, elle affiche les destinataires définis, le rapport utilisé pour la
publication dynamique, la prochaine date de publication et l'état de la dernière publication.

> Voir `Documents/Projet.txt` (cahier des charges) et `Documents/Requetes REST et QUERYBUILDER pour Audit.txt`.

## Arborescence

```
├── BOAudit.sln
└── BOAudit/
    ├── Program.cs                     # configuration (EF, auth Windows, BO, Razor)
    ├── appsettings.json               # paramètres : chaîne SQL, serveur BO, groupes autorisés
    ├── Models/                        # BoSettings, BoPublication, BoSchedule, BoInstance, RecipientRow...
    ├── Services/
    │   ├── BoPlatformClient.cs        # client REST /biprws : logon/long, infostore, schedules, cmsquery
    │   └── BoAuditCache.cs            # rafraîchissement + cache persisté (App_Data/bo-cache.json)
    ├── Data/
    │   ├── Destinataire.cs            # entité EF : email(50), matricule RH(6), nom, prénom, sorti
    │   └── ApplicationDbContext.cs    # base SQL des destinataires
    ├── Authorization/
    │   └── BoAuditGroupHandler.cs     # accès réservé aux groupes Windows configurés
    └── Pages/
        ├── Index.cshtml               # bouton « Rafraîchir les données BO » + date/heure, vue cartes ou destinataires
        ├── Index.cshtml.cs
        └── Publication/Detail.cshtml  # détail : rapport source, planning, dernier état, destinataires, historique
```

## Configuration (appsettings.json)

```json
"ConnectionStrings": {
  "ApplicationDbContext": "Server=...;Database=BOAudit;Trusted_Connection=True;..."
},
"BoSettings": {
  "BaseUrl": "http://bo.lignesdazur.fr:8080",
  "UserName": "CPT_BO_AUDIT",              // compte générique
  "Password": "MOT_DE_PASSE_A_RENSEIGNER",
  "Auth": "secEnterprise",
  "PublicationsFolderId": 257219,          // dossier CMC PUBLICATIONS
  "PageSize": 500
},
"Security": {
  "AllowedGroups": [ "DOMAINE\\Groupe_BOAudit" ]   // groupes Windows autorisés
}
```

Renseignez impérativement le mot de passe BO et le (ou les) groupes autorisés
(l'accès est refusé par défaut tant qu'aucun groupe n'est configuré).

## Fonctionnement

1. **Connexion BO** : `POST /biprws/logon/long` (compte générique) → jeton `X-SAP-LogonToken`,
   libéré par `POST /biprws/logoff` à chaque rafraîchissement.
2. **Publications** : `GET /biprws/v1/infostore/{PublicationsFolderId}/children` (Atom/XML).
3. **Détails de chaque publication** : `POST /biprws/v1/cmsquery`
   (`SELECT * FROM CI_INFOOBJECTS WHERE SI_ID = {id}`) — rapport source (`SI_PUBLICATION_DOCUMENTS`),
   rapport des destinataires dynamiques (`SI_ON_READ_RECIPIENTS`), colonnes de destinataires dynamiques
   (`SI_DYNAMIC_RECIPIENTS`), adresses statiques SMTP (`SI_DESTINATIONS` → `SI_MAIL_ADDRESSES`, `SI_MAIL_CC`),
   dernière exécution (`SI_LAST_RUN_TIME`).
4. **Planning** : `GET /biprws/v1/infostore/{id}/schedules` → prochaine exécution (`nextruntime`),
   statut de planification.
5. **Historique/instances** : `GET /biprws/v1/infostore/{id}/children` → état du dernier envoi
   (`SI_SCHEDULE_STATUS` via cmsquery sur l'instance : Terminé, Échec, En cours...).
6. Les adresses e-mail trouvées sont **enrichies** avec la table `Destinataires` (EF / SQL Server) :
   matricule RH, nom, prénom, flag *sorti*.

Un clic sur « Rafraîchir les données BO » recharge l'ensemble et met à jour la date/heure
de dernière actualisation ; un instantané est conservé dans `App_Data/bo-cache.json`
(données dispo dès le redémarrage, sans relancer un interrogeant BO).

## Authentification Windows

- **Visual Studio / IIS Express** : `launchSettings.json` active `windowsAuthentication` ;
  s'assurer d'ouvrir Visual Studio sur le domaine, ou d'utiliser un compte disposant du groupe.
- **Kestrel / IIS** (`dotnet run` ou publication IIS) : authentification Negotiate (Windows Integrated).
- Toutes les pages exigent : utilisateur authentifié **et** membre d'un des `Security:AllowedGroups` (fallback policy).
- En dev sous Linux, sans contrôleur de domaine, Negotiate se contente d'émettre un `401 challenge`.

## Démarrage : Document pas à pas

1. Ouvrir `BOAudit.sln` dans Visual Studio 2022 (workload « Développement web et ASP.NET »).
2. Renseigner `appsettings.json` (mot de passe BO, groupes autorisés, chaîne SQL).
3. Table `Destinataires` : en Développement, elle est créée automatiquement au premier
   démarrage (`EnsureCreated`) ; sinon `dotnet ef migrations add InitialCreate` puis
   `dotnet ef database update` (le package Microsoft.EntityFrameworkCore.Design est référencé).
4. F5 : page d'accueil → « Rafraîchir les données BO ».

## Remarques

- Les dates renvoyées par le CMS ("30 nov. 2026 06:00") sont analysées en culture française.
- Les packages sont en versions .NET 8.0.32 (correctifs de sécurité de novembre 2026).
