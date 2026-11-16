using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Ylm.Core;

namespace Ylm.Server.Data;

public sealed class YlmDbContext(DbContextOptions<YlmDbContext> options) : DbContext(options)
{
    /// <summary>Advisory lock id that serializes writes to the audit hash chain.</summary>
    private const long AuditLockId = 0x594c4d41554449; // "YLMAUDI"

    public DbSet<InstallationSettings> Installation => Set<InstallationSettings>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();
    public DbSet<PlatformUserTenant> PlatformUserTenants => Set<PlatformUserTenant>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<YubiKey> Keys => Set<YubiKey>();
    public DbSet<KeyRegistration> Registrations => Set<KeyRegistration>();
    public DbSet<RegistrationProfile> Profiles => Set<RegistrationProfile>();
    public DbSet<CustomFieldDefinition> CustomFields => Set<CustomFieldDefinition>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<InstallationSettings>(e =>
        {
            e.ToTable("installation");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Ignore(x => x.IsSetupComplete);
        });

        b.Entity<PlatformUser>(e =>
        {
            e.ToTable("platform_users");
            e.HasIndex(x => x.EntraObjectId).IsUnique();
            e.HasIndex(x => x.Upn).IsUnique();
            e.HasMany(x => x.TenantAssignments).WithOne().HasForeignKey(x => x.PlatformUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlatformUserTenant>(e =>
        {
            e.ToTable("platform_user_tenants");
            e.HasKey(x => new { x.PlatformUserId, x.TenantId });
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasIndex(x => x.EntraTenantId).IsUnique();
            e.Property(x => x.LastChecksJson).HasColumnType("jsonb");
            e.Property(x => x.PolicySnapshotJson).HasColumnType("jsonb");
        });

        b.Entity<YubiKey>(e =>
        {
            e.ToTable("keys");
            e.HasIndex(x => new { x.TenantId, x.SerialNumber }).IsUnique().HasFilter("\"SerialNumber\" IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.Property(x => x.CustomFields).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string?>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string?>(),
                new ValueComparer<Dictionary<string, string?>>(
                    (a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
                    v => new Dictionary<string, string?>(v)));
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Registrations).WithOne(x => x.Key).HasForeignKey(x => x.KeyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<KeyRegistration>(e =>
        {
            e.ToTable("key_registrations");
            e.HasIndex(x => new { x.TenantId, x.EntraMethodId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.UserPrincipalName });
        });

        b.Entity<RegistrationProfile>(e =>
        {
            e.ToTable("registration_profiles");
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomFieldDefinition>(e =>
        {
            e.ToTable("custom_field_definitions");
            e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasIndex(x => x.Sequence).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Timestamp });
            e.HasIndex(x => x.KeyId);
            // text, not jsonb: jsonb normalizes JSON and would break the hash chain.
            e.Property(x => x.Details).HasColumnType("text");
        });

        b.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasOne(x => x.PlatformUser).WithMany().HasForeignKey(x => x.PlatformUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PairingCode>(e =>
        {
            e.ToTable("pairing_codes");
            e.HasIndex(x => x.CodeHash).IsUnique();
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys");
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync.");

    /// <summary>
    /// Saves changes; new audit entries are chained (sequence + hash) under a PostgreSQL advisory lock
    /// in the same transaction as the business change.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var newAudit = ChangeTracker.Entries<AuditEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
        foreach (var entry in ChangeTracker.Entries<AuditEntry>().Where(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit entries are append-only.");

        if (newAudit.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownTransaction = Database.CurrentTransaction is null;
        var tx = ownTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            await Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [AuditLockId], cancellationToken);
            var last = await AuditEntries.AsNoTracking().OrderByDescending(a => a.Sequence)
                .Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(cancellationToken);
            var seq = last?.Sequence ?? 0;
            var prev = last?.Hash ?? AuditChain.Genesis;
            foreach (var a in newAudit)
            {
                a.Sequence = ++seq;
                a.Timestamp = AuditChain.Normalize(a.Timestamp == default ? DateTimeOffset.UtcNow : a.Timestamp);
                a.PreviousHash = prev;
                a.Hash = AuditChain.ComputeHash(prev, a.Sequence, a.Timestamp, a.ActorName, a.Action, a.TenantId, a.KeyId, a.Target, a.Details);
                prev = a.Hash;
            }
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            if (tx is not null) await tx.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }
}
