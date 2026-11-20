using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Security;

/// <summary>
/// Notfallcodes für Owner: einmalig gültige Codes zum Ausdrucken, falls die Anmeldung mit Entra oder Passkey ausfällt.
/// Jeder Code hat 80 Bit Zufall; gespeichert wird nur der SHA-256-Hash. Jede Nutzung landet im Audit-Log
/// (auch als Webhook/Mail-Alarm wählbar).
/// </summary>
public sealed class RecoveryCodeService(YlmDbContext db, AuditService audit, LocalAuthService local)
{
    public const int CodeCount = 8;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 Zeichen, ohne 0/O und 1/I
    private const int CodeLength = 16;

    public sealed record Status(int Remaining, int Total, DateTimeOffset? CreatedAt);

    public static bool Eligible(PlatformUser user) => user.Role == PlatformRole.Owner && user.IsActive;

    public static string NewCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    /// <summary>Großbuchstaben, ohne Trennzeichen und Leerzeichen.</summary>
    public static string Normalize(string? code) =>
        new((code ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    public static string HashOf(string code) => TokenHashing.Hash("ylm-recovery:" + Normalize(code));

    public async Task<Status> StatusAsync(Guid userId)
    {
        var codes = await db.RecoveryCodes.AsNoTracking().Where(c => c.PlatformUserId == userId).ToListAsync();
        return new Status(codes.Count(c => c.UsedAt == null), codes.Count, codes.Count == 0 ? null : codes.Max(c => c.CreatedAt));
    }

    /// <summary>Erzeugt neue Codes; alle bisherigen werden ungültig. Die Codes gibt es nur in dieser Antwort.</summary>
    public async Task<IReadOnlyList<string>> CreateAsync(PlatformUser user)
    {
        if (!Eligible(user)) throw ApiException.Forbidden("Notfallcodes gibt es nur für Owner.");
        await db.RecoveryCodes.Where(c => c.PlatformUserId == user.Id).ExecuteDeleteAsync();
        var codes = Enumerable.Range(0, CodeCount).Select(_ => NewCode()).ToList();
        var now = DateTimeOffset.UtcNow;
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { PlatformUserId = user.Id, CodeHash = HashOf(c), CreatedAt = now }));
        audit.Add(AuditService.User(user), AuditActions.RecoveryCodesCreated, target: user.Upn, details: new { count = codes.Count });
        await db.SaveChangesAsync();
        return codes;
    }

    public async Task<(PlatformUser User, int Remaining)> LoginAsync(string? code)
    {
        var normalized = Normalize(code);
        if (normalized.Length != CodeLength) return await DenyAsync(null);
        var hash = HashOf(normalized);
        var entry = await db.RecoveryCodes.Include(c => c.PlatformUser).FirstOrDefaultAsync(c => c.CodeHash == hash);
        if (entry?.PlatformUser is not { } user || entry.UsedAt is not null || !Eligible(user)) return await DenyAsync(entry?.PlatformUser?.Upn);

        // Einmalig: nur wer den Code als Erster auf "benutzt" setzt, wird angemeldet.
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.RecoveryCodes.Where(c => c.Id == entry.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now));
        if (claimed != 1) return await DenyAsync(user.Upn);

        var remaining = await db.RecoveryCodes.CountAsync(c => c.PlatformUserId == user.Id && c.UsedAt == null);
        audit.Add(AuditService.User(user), AuditActions.RecoveryCodeUsed, target: user.Upn, details: new { remaining });
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await local.SignInAsync(user, "recovery");
        return (user, remaining);
    }

    private async Task<(PlatformUser, int)> DenyAsync(string? upn)
    {
        const string message = "Der Notfallcode ist ungültig oder wurde schon verwendet.";
        await audit.WriteAsync(new AuditActor(ActorType.User, null, upn ?? "unbekannt"), AuditActions.LoginDenied, details: new { code = "invalid_recovery_code", message });
        throw new ApiException(401, "invalid_recovery_code", message);
    }
}
