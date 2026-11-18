using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

public sealed class PolicyPatchTests
{
    // Shape as returned by GET .../authenticationMethodConfigurations/fido2 for a disabled method.
    private const string Raw = """
        {
          "@odata.type": "#microsoft.graph.fido2AuthenticationMethodConfiguration",
          "id": "Fido2",
          "state": "disabled",
          "isSelfServiceRegistrationAllowed": true,
          "isAttestationEnforced": false,
          "excludeTargets": [],
          "keyRestrictions": { "isEnforced": false, "enforcementType": "block", "aaGuids": [] },
          "includeTargets": [
            { "targetType": "group", "id": "all_users", "isRegistrationRequired": false, "allowedPasskeyProfiles": ["p1"] }
          ]
        }
        """;

    private static JsonElement Parse() => JsonDocument.Parse(Raw).RootElement;

    [Fact]
    public void Enabling_only_sends_state()
    {
        var raw = Parse();
        var after = GraphClient.ParsePolicy(raw) with { Enabled = true };
        var patch = GraphClient.PolicyPatch(raw, after)!;
        Assert.Equal("enabled", patch["state"]!.GetValue<string>());
        Assert.Equal(new[] { "@odata.type", "state" }, patch.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void No_change_means_no_patch()
    {
        var raw = Parse();
        Assert.Null(GraphClient.PolicyPatch(raw, GraphClient.ParsePolicy(raw)));
    }

    [Fact]
    public void Adding_a_target_keeps_existing_target_fields()
    {
        var raw = Parse();
        var before = GraphClient.ParsePolicy(raw);
        var after = before with { IncludeTargets = [.. before.IncludeTargets, new PolicyTarget("grp-1")] };
        var patch = GraphClient.PolicyPatch(raw, after)!;
        Assert.False(patch.ContainsKey("state"));
        Assert.False(patch.ContainsKey("keyRestrictions"));
        var targets = patch["includeTargets"]!.AsArray();
        Assert.Equal(2, targets.Count);
        Assert.Equal("p1", targets[0]!["allowedPasskeyProfiles"]![0]!.GetValue<string>());
        Assert.Equal("grp-1", targets[1]!["id"]!.GetValue<string>());
        Assert.False(targets[1]!["isRegistrationRequired"]!.GetValue<bool>());
    }

    [Fact]
    public void Key_restriction_change_sends_complete_restrictions()
    {
        var raw = Parse();
        var aaguid = Guid.NewGuid();
        var after = GraphClient.ParsePolicy(raw) with { KeyRestrictionsEnforced = true, EnforcementType = "allow", AaGuids = [aaguid] };
        var kr = GraphClient.PolicyPatch(raw, after)!["keyRestrictions"]!;
        Assert.True(kr["isEnforced"]!.GetValue<bool>());
        Assert.Equal("allow", kr["enforcementType"]!.GetValue<string>());
        Assert.Equal(aaguid.ToString(), kr["aaGuids"]![0]!.GetValue<string>());
    }
}
