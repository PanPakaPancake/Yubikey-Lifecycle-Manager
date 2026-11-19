using System.Text.Json;

namespace Ylm.Server.Graph;

public sealed class GraphException(string message, int statusCode, string? graphCode = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? GraphCode { get; } = graphCode;
}

public sealed record GraphUser(string Id, string UserPrincipalName, string? DisplayName, string? Mail, string? Department, bool AccountEnabled);

public sealed record GraphGroup(string Id, string DisplayName);

public sealed record GraphFido2Method(string Id, string? DisplayName, DateTimeOffset? CreatedDateTime, Guid? AaGuid, string? Model);

public sealed record GraphOrganization(string Id, string DisplayName, string? DefaultDomain);

public sealed record CreationOptions(JsonElement PublicKey, DateTimeOffset? ChallengeTimeout);

public sealed record NewFido2Credential(string CredentialId, string ClientDataJson, string AttestationObject);

/// <summary>Eine Zeile des Entra-Berichts userRegistrationDetails (bzw. aus der Einzelabfrage ohne P1 gebildet).</summary>
public sealed record GraphUserRegistration(
    string Id, string? UserPrincipalName, string? DisplayName, string? UserType, bool IsAdmin,
    IReadOnlyList<string> MethodsRegistered, string? DefaultMfaMethod, DateTimeOffset? LastUpdated);

/// <summary>Ergebnis der Registrierungsabfrage: aus dem Bericht (schnell, braucht Entra ID P1) oder pro Benutzer abgefragt.</summary>
public sealed record GraphRegistrationResult(IReadOnlyList<GraphUserRegistration> Users, bool FromReport);

/// <summary>Benutzer mit den Feldern, die die Rollout-Übersicht braucht.</summary>
public sealed record GraphDirectoryUser(
    string Id, string UserPrincipalName, string? DisplayName, string? Mail, string? Department, bool AccountEnabled, string? UserType,
    DateTimeOffset? LastSignIn);

/// <summary>Eine registrierte Anmeldemethode eines Benutzers (live aus /authentication/methods).</summary>
/// <param name="Kind">Typ ohne Präfix/Suffix, z. B. „phone“, „microsoftAuthenticator“, „fido2“.</param>
/// <param name="ReportName">Name wie im Bericht (methodsRegistered), z. B. „mobilePhone“.</param>
public sealed record GraphAuthMethod(string Id, string Kind, string ReportName, string? DisplayName, string? Detail, DateTimeOffset? CreatedDateTime);

/// <summary>Einstellungen der Methode „Befristeter Zugriffspass“ im Tenant.</summary>
public sealed record GraphTapPolicy(bool Enabled, int DefaultLifetimeInMinutes, int MinimumLifetimeInMinutes, int MaximumLifetimeInMinutes, bool DefaultUsableOnce, bool IsUsableOnceEnforced, int TargetCount);

/// <summary>Neu erzeugter TAP. <see cref="Pass"/> liefert Graph nur bei der Erstellung.</summary>
public sealed record GraphTemporaryAccessPass(string Id, string Pass, DateTimeOffset? StartDateTime, int LifetimeInMinutes, bool IsUsableOnce);
