using Ylm.Core;

namespace Ylm.Server.Graph;

/// <summary>Target audience of a tenant: members of the inclusion groups (or all users) minus members of the exclusion groups.</summary>
public static class TargetAudience
{
    public static async Task<int?> CountAsync(IGraphClient graph, IReadOnlyList<GroupRef> include, IReadOnlyList<GroupRef> exclude, CancellationToken ct = default)
    {
        // Simple case: one count request, no member lists needed.
        if (exclude.Count == 0 && include.Count <= 1)
            return await graph.CountUsersAsync(include.Count == 0 ? null : include[0].Id, ct);

        var members = new HashSet<string>();
        if (include.Count == 0) members.UnionWith(await graph.ListUserIdsAsync(null, ct));
        foreach (var g in include) members.UnionWith(await graph.ListUserIdsAsync(g.Id, ct));
        foreach (var g in exclude)
        {
            if (members.Count == 0) break;
            members.ExceptWith(await graph.ListUserIdsAsync(g.Id, ct));
        }
        return members.Count;
    }
}
