using VSharp.Cucm;
using VSharp.Cucm.Models;

// Wraps CucmService's live AXL named-resource lookups with the filtering/ordering every
// selector menu needs (skip blank names, sort by name), so menu code and any future
// configuration-context provider share one implementation instead of duplicating that logic at
// each call site.
internal sealed class CucmResourceQueryService(CucmService cucm)
{
    internal Task<IReadOnlyList<CucmNamedResource>> ListRoutePartitionsAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(cucm.ListRoutePartitionsAsync(cancellationToken: cancellationToken), cancellationToken);

    internal Task<IReadOnlyList<CucmNamedResource>> ListDevicePoolsAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(cucm.ListDevicePoolsAsync(cancellationToken: cancellationToken), cancellationToken);

    internal Task<IReadOnlyList<CucmNamedResource>> ListPhoneButtonTemplatesAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(
            cucm.ListPhoneButtonTemplatesAsync(cancellationToken: cancellationToken), cancellationToken);

    internal Task<IReadOnlyList<CucmNamedResource>> ListVoiceMailProfilesAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(cucm.ListVoiceMailProfilesAsync(cancellationToken: cancellationToken), cancellationToken);

    internal Task<IReadOnlyList<CucmNamedResource>> ListCallingSearchSpacesAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(
            cucm.ListCallingSearchSpacesAsync(cancellationToken: cancellationToken), cancellationToken);

    internal Task<IReadOnlyList<CucmNamedResource>> ListPhoneSecurityProfilesAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(
            cucm.ListPhoneSecurityProfilesAsync(cancellationToken: cancellationToken), cancellationToken);

    // AXL operation name/element ('listCallPickupGroup'/'callPickupGroup') is best-effort, not
    // verified against a live WSDL - watch for real CUCM fault text if this is ever rejected.
    internal Task<IReadOnlyList<CucmNamedResource>> ListCallPickupGroupsAsync(
        CancellationToken cancellationToken) =>
        MaterializeAsync(cucm.ListCallPickupGroupsAsync(cancellationToken: cancellationToken), cancellationToken);

    private static async Task<IReadOnlyList<CucmNamedResource>> MaterializeAsync(
        IAsyncEnumerable<CucmNamedResource> resources,
        CancellationToken cancellationToken)
    {
        var results = new List<CucmNamedResource>();
        await foreach (var resource in resources.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(resource.Name))
            {
                results.Add(resource);
            }
        }
        results.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }
}
