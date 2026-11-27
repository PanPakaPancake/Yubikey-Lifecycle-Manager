using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Inventory;
using Ylm.Server.Tenants;

namespace Ylm.Server.Demo;

/// <summary>Füllt eine leere Datenbank einmalig mit den Beispieldaten der Demo.</summary>
public sealed class DemoSeeder(YlmDbContext db, DemoGraphFactory graphs, SyncService sync, TenantCheckService checks, AuditService audit, ILogger<DemoSeeder> logger)
{
    public async Task SeedAsync(string dataDir, CancellationToken ct = default)
    {
        var marker = Path.Combine(dataDir, DemoMode.MarkerFile);
        if (File.Exists(marker)) return;
        logger.LogInformation("Demo-Modus: Beispieldaten werden angelegt");
        var rnd = new Random(2026);
        var now = DateTimeOffset.UtcNow;

        var settings = await db.Installation.FirstOrDefaultAsync(ct);
        if (settings is null) db.Installation.Add(settings = new InstallationSettings());
        settings.OrganizationName = "Demo (Beispieldaten)";
        settings.HomeTenantId = DemoData.ContosoTenantId;
        settings.ClientId = Guid.Parse("d3e0c0a1-0000-4000-8000-0000000000aa");
        settings.EntraSsoEnabled = false;
        settings.SetupCompletedAt = now;
        settings.BootstrapOwnerUpn = DemoMode.Accounts[0].Upn;

        var users = DemoMode.Accounts.Select(a => new PlatformUser
        {
            Upn = a.Upn, DisplayName = a.DisplayName, Role = a.Role, LoginKind = UserLoginKind.Local, CreatedAt = now.AddDays(-200),
        }).ToList();
        db.PlatformUsers.AddRange(users);
        var operatorUser = users.First(u => u.Role == PlatformRole.Operator);

        var standard = await db.Profiles.FirstOrDefaultAsync(p => p.IsDefault, ct);
        db.Profiles.Add(new RegistrationProfile
        {
            Name = "Führungskräfte",
            Description = "Nur aktuelle Keys (ab Firmware 5.7), Werksreset vor der Registrierung.",
            PinStrategy = PinStrategy.Random, PinLength = 8, ForcePinChange = true, FactoryResetBeforeRegistration = true,
            MinimumFirmware = "5.7", DisplayNameTemplate = "YubiKey {Seriennummer}",
        });
        db.CustomFields.AddRange(
            new CustomFieldDefinition { Key = "kostenstelle", Label = "Kostenstelle", Type = CustomFieldType.Text, SortOrder = 1 },
            new CustomFieldDefinition { Key = "standort", Label = "Standort", Type = CustomFieldType.Select, Options = ["Hamburg", "München", "Remote"], SortOrder = 2 });

        string[] sites = ["Hamburg", "München", "Remote"];
        var tenants = new List<Tenant>();
        foreach (var g in graphs.Tenants)
        {
            var tenant = new Tenant
            {
                EntraTenantId = g.TenantId, DisplayName = g.DisplayName, DefaultDomain = g.Domain, Status = TenantStatus.Active,
                CreatedAt = now.AddDays(-400), ConsentedAt = now.AddDays(-400), ConsentedBy = DemoMode.Accounts[0].Upn,
                TargetIncludeGroups = [new GroupRef(DemoData.GroupYubiKey, "YubiKey-Nutzer")],
                ProtectedGroups = [new GroupRef(DemoData.GroupBreakglass, "Notfallkonten (Breakglass)")],
                RequireBreakglassApproval = g.TenantId == DemoData.ContosoTenantId,
            };
            db.Tenants.Add(tenant);
            tenants.Add(tenant);

            // Keys, die über die Plattform registriert wurden: mit Seriennummer, Firmware und Modell.
            var byUser = g.Users.ToDictionary(u => u.Id);
            foreach (var group in g.Fido2.SelectMany(kv => kv.Value.Select(m => (UserId: kv.Key, Method: m)))
                         .Where(x => DemoData.KeySpecs.ContainsKey(x.Method.Id))
                         .GroupBy(x => DemoData.KeySpecs[x.Method.Id].Serial))
            {
                var spec = DemoData.KeySpecs[group.First().Method.Id];
                var registered = group.Min(x => x.Method.CreatedDateTime) ?? now;
                var key = new YubiKey
                {
                    TenantId = tenant.Id, SerialNumber = spec.Serial, FirmwareVersion = spec.Firmware, Model = spec.Model, FormFactor = spec.FormFactor,
                    AaGuid = group.First().Method.AaGuid, Status = KeyStatus.Active, Source = KeySource.Platform, Usage = spec.Usage, Custodian = spec.Custodian,
                    ProfileId = standard?.Id, PinChangeForced = FirmwareVersion.TryParse(spec.Firmware, out var fw) && fw >= FirmwareVersion.ForcePinChangeMinimum,
                    CreatedAt = registered.AddDays(-3), UpdatedAt = registered, StatusChangedAt = registered,
                    Tags = spec.Usage == KeyUsage.Personal && rnd.NextDouble() < 0.2 ? ["Ersatzkey"] : [],
                    CustomFields = new Dictionary<string, string?> { ["kostenstelle"] = (4000 + rnd.Next(1, 40) * 10).ToString(), ["standort"] = sites[rnd.Next(sites.Length)] },
                };
                db.Keys.Add(key);
                foreach (var (userId, method) in group)
                {
                    var person = byUser[userId];
                    db.Registrations.Add(new KeyRegistration
                    {
                        TenantId = tenant.Id, Key = key, EntraUserId = userId, UserPrincipalName = person.UserPrincipalName, UserDisplayName = person.DisplayName,
                        EntraMethodId = method.Id, DisplayName = method.DisplayName, AaGuid = method.AaGuid, RegisteredAt = method.CreatedDateTime ?? now,
                        RegisteredById = operatorUser.Id, RegisteredByName = operatorUser.DisplayName, ProfileId = standard?.Id, Source = KeySource.Platform,
                        LastSeenInSyncAt = now,
                    });
                }
            }

            // Lager: neue Keys im Schrank, darunter ältere Firmware (Sicherheitshinweise).
            var serial = (g.TenantId == DemoData.ContosoTenantId ? 32_550_000 : 32_660_000);
            var stock = g.TenantId == DemoData.ContosoTenantId ? 14 : 6;
            for (var i = 0; i < stock; i++)
                db.Keys.Add(StockKey(tenant.Id, serial += rnd.Next(1, 9), i % 5 == 4 ? "5.4.3" : "5.7.4", i % 3 == 0 ? "YubiKey 5C NFC" : "YubiKey 5 NFC", now.AddDays(-rnd.Next(5, 60))));

            // Ein verlorener und ein ausgemusterter Key mit früherer Zuordnung.
            var lostOwner = g.Users.Where(u => !g.Fido2.ContainsKey(u.Id) && u.AccountEnabled).Skip(1).First();
            var lost = new YubiKey
            {
                TenantId = tenant.Id, SerialNumber = serial += 13, FirmwareVersion = "5.4.3", Model = "YubiKey 5 NFC", FormFactor = "UsbAKeychain",
                AaGuid = DemoData.Aaguid54Nfc, Status = KeyStatus.Locked, LockReason = LockReason.Lost, StatusNote = "Im Zug liegen gelassen (Demo)",
                Source = KeySource.Platform, CreatedAt = now.AddDays(-300), UpdatedAt = now.AddDays(-6), StatusChangedAt = now.AddDays(-6),
            };
            db.Keys.Add(lost);
            db.Registrations.Add(new KeyRegistration
            {
                TenantId = tenant.Id, Key = lost, EntraUserId = lostOwner.Id, UserPrincipalName = lostOwner.UserPrincipalName, UserDisplayName = lostOwner.DisplayName,
                EntraMethodId = "demo-lost-" + tenant.EntraTenantId.ToString()[..4], DisplayName = $"YubiKey {lost.SerialNumber}", AaGuid = lost.AaGuid,
                RegisteredAt = now.AddDays(-290), RegisteredByName = operatorUser.DisplayName, Source = KeySource.Platform,
                RemovedAt = now.AddDays(-6), RemovedReason = "Key gesperrt (verloren)",
            });
            db.Keys.Add(new YubiKey
            {
                TenantId = tenant.Id, SerialNumber = serial += 7, FirmwareVersion = "5.2.7", Model = "YubiKey 5C", FormFactor = "UsbCKeychain",
                Status = KeyStatus.Retired, StatusNote = "Defekt, entsorgt (Demo)", Source = KeySource.Import,
                CreatedAt = now.AddDays(-700), UpdatedAt = now.AddDays(-40), StatusChangedAt = now.AddDays(-40),
            });

            if (g.TenantId == DemoData.ContosoTenantId)
            {
                foreach (var (person, ticket) in g.Users.Where(u => !g.Fido2.ContainsKey(u.Id) && u.AccountEnabled && u.Id != lostOwner.Id).Take(3).Select((u, i) => (u, 20431 + i * 17)))
                {
                    db.Todos.Add(new RegistrationTodo
                    {
                        TenantId = tenant.Id, EntraUserId = person.Id, UserPrincipalName = person.UserPrincipalName, UserDisplayName = person.DisplayName,
                        Department = person.Department, TicketId = "INC-" + ticket, Note = "Neue Mitarbeiterin/neuer Mitarbeiter, Key zum ersten Arbeitstag",
                        Source = "API: Ticketsystem (Demo)", DueDate = DateOnly.FromDateTime(now.AddDays(3 + ticket % 5).UtcDateTime), CreatedAt = now.AddDays(-2),
                        CreatedByName = "Ticketsystem",
                    });
                }
            }
        }

        // Partnerlager: noch keinem Kunden zugeordnet.
        var partnerSerial = 33_010_000;
        for (var i = 0; i < 8; i++)
            db.Keys.Add(StockKey(null, partnerSerial += rnd.Next(1, 9), "5.7.4", i % 2 == 0 ? "YubiKey 5 NFC" : "YubiKey 5C NFC", now.AddDays(-rnd.Next(1, 30))));

        audit.Add(AuditService.System, AuditActions.SetupCompleted, details: new { mode = "demo" });
        await db.SaveChangesAsync(ct);

        // Wie im Echtbetrieb: Sync übernimmt selbst registrierte Keys (ohne Seriennummer), Prüfungen zeigen die Richtlinie.
        foreach (var tenant in tenants)
        {
            await sync.SyncAsync(tenant.Id, AuditService.System, ct);
            await checks.RunAsync(tenant, AuditService.System, ct);
        }

        await File.WriteAllTextAsync(marker, "Diese Datenbank enthält die Beispieldaten des Demo-Modus.\n", ct);
        logger.LogInformation("Demo-Modus: Beispieldaten angelegt ({Tenants} Tenants)", tenants.Count);
    }

    private static YubiKey StockKey(Guid? tenantId, int serial, string firmware, string model, DateTimeOffset at) => new()
    {
        TenantId = tenantId, SerialNumber = serial, FirmwareVersion = firmware, Model = model, FormFactor = model.Contains("5C") ? "UsbCKeychain" : "UsbAKeychain",
        AaGuid = firmware.StartsWith("5.7") ? DemoData.Aaguid57Nfc : DemoData.Aaguid54Nfc, Status = KeyStatus.InStock, Source = KeySource.StockIntake,
        CreatedAt = at, UpdatedAt = at, StatusChangedAt = at,
    };
}
