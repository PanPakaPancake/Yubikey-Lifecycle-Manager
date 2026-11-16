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
