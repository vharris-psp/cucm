using VSharp.Cucm;
using VSharp.Cucm.Models;

/// <summary>
/// Live status of a locally tracked <see cref="UserDid"/> after comparing it against directory
/// numbers actually configured in CUCM's DID route partition(s). The local inventory remains
/// authoritative for "is this a valid DID" (CUCM is never trusted to expand that list); CUCM is
/// only consulted to determine whether a tracked pattern is currently in use.
/// </summary>
internal enum UserDidLiveStatus
{
    /// <summary>Not configured in CUCM's scanned partition(s); free to assign.</summary>
    Available,

    /// <summary>Configured in CUCM, either assigned to a device or reserved by function.</summary>
    Reserved,

    /// <summary>
    /// Requires attention: the local inventory and CUCM disagree (e.g. a locally recorded
    /// assignment no longer exists in CUCM).
    /// </summary>
    Anomaly,
}

internal sealed record UserDidReconciliationEntry(UserDid Did, UserDidLiveStatus Status, string Reason);

/// <summary>
/// A directory number found in a scanned CUCM DID partition that has no matching entry in the
/// local tracked inventory — e.g. a misconfigured number that was never actually assigned as a DID.
/// </summary>
internal sealed record UserDidPartitionAnomaly(
    string Pattern,
    string RoutePartitionName,
    string? Usage,
    string Reason);

internal sealed record UserDidReconciliationResult(
    IReadOnlyList<UserDidReconciliationEntry> Tracked,
    IReadOnlyList<UserDidPartitionAnomaly> Untracked)
{
    internal IEnumerable<UserDid> Available =>
        Tracked.Where(entry => entry.Status == UserDidLiveStatus.Available).Select(entry => entry.Did);
}

internal static class UserDidReconciler
{
    /// <summary>
    /// Pure reconciliation of the tracked inventory against an already-fetched snapshot of CUCM
    /// lines, kept separate from CUCM I/O so it can be unit tested without an AXL connection.
    /// </summary>
    internal static UserDidReconciliationResult Resolve(
        IReadOnlyList<UserDid> tracked,
        IReadOnlyList<CucmDirectoryNumber> configuredLines,
        IReadOnlyCollection<ReservedExtension>? reservedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(configuredLines);
        var reserved = new HashSet<(string Pattern, string RoutePartitionName)>(
            (reservedExtensions ?? [])
                .Select(extension => (extension.Pattern, extension.RoutePartitionName)));

        var byPattern = new Dictionary<string, List<CucmDirectoryNumber>>(StringComparer.Ordinal);
        foreach (var line in configuredLines)
        {
            if (string.IsNullOrWhiteSpace(line.Pattern) || string.IsNullOrWhiteSpace(line.RoutePartitionName))
            {
                continue;
            }
            if (!byPattern.TryGetValue(line.Pattern, out var lines))
            {
                lines = [];
                byPattern[line.Pattern] = lines;
            }
            lines.Add(line);
        }

        var matched = new HashSet<CucmDirectoryNumber>();
        var trackedResults = new List<UserDidReconciliationEntry>();
        foreach (var did in tracked)
        {
            var match = FindMatch(did, byPattern);
            if (match is not null)
            {
                matched.Add(match);
                trackedResults.Add(new UserDidReconciliationEntry(
                    did,
                    UserDidLiveStatus.Reserved,
                    DescribeReserved(did, match)));
                continue;
            }

            trackedResults.Add(did.Assignment is { } assignment
                ? new UserDidReconciliationEntry(
                    did,
                    UserDidLiveStatus.Anomaly,
                    $"Locally recorded as assigned to phone '{assignment.PhoneName}' line " +
                        $"{assignment.LineIndex}" +
                        (assignment.UserId is null ? string.Empty : $" for user '{assignment.UserId}'") +
                        ", but no matching directory number was found in the scanned CUCM DID " +
                        "partition(s). It may have been removed or reassigned outside this tool.")
                : new UserDidReconciliationEntry(
                    did,
                    UserDidLiveStatus.Available,
                    "Not currently configured in CUCM."));
        }

        var untracked = byPattern.Values
            .SelectMany(lines => lines)
            .Where(line => !matched.Contains(line))
            .Where(line => !reserved.Contains((line.Pattern!, line.RoutePartitionName!)))
            .Select(line => new UserDidPartitionAnomaly(
                line.Pattern!,
                line.RoutePartitionName!,
                line.Usage,
                $"Configured in CUCM route partition '{line.RoutePartitionName}' but is not present " +
                    "in the tracked local DID inventory. Confirm it is a real DID and add it, or " +
                    "correct/remove the misconfigured number in CUCM."))
            .ToArray();

        return new UserDidReconciliationResult(trackedResults, untracked);
    }

    /// <summary>Fetches the configured scan partitions from CUCM, then resolves against them.</summary>
    internal static async Task<UserDidReconciliationResult> ResolveFromCucmAsync(
        CucmService cucm,
        IReadOnlyList<UserDid> tracked,
        IReadOnlyList<string> scanPartitions,
        CancellationToken ct = default,
        IReadOnlyCollection<ReservedExtension>? reservedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(cucm);
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(scanPartitions);

        var lines = new List<CucmDirectoryNumber>();
        foreach (var partition in scanPartitions)
        {
            await foreach (var line in cucm.ListDirectoryNumbersAsync(
                cancellationToken: ct,
                routePartitionName: partition))
            {
                lines.Add(line);
            }
        }
        return Resolve(tracked, lines, reservedExtensions);
    }

    internal static IReadOnlyList<string> ParsePartitions(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static CucmDirectoryNumber? FindMatch(
        UserDid did,
        IReadOnlyDictionary<string, List<CucmDirectoryNumber>> byPattern)
    {
        if (!byPattern.TryGetValue(did.Pattern, out var candidates))
        {
            return null;
        }
        if (did.RoutePartitionName is { } partition)
        {
            var exact = candidates.FirstOrDefault(line =>
                string.Equals(line.RoutePartitionName, partition, StringComparison.Ordinal));
            if (exact is not null)
            {
                return exact;
            }
        }
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static string DescribeReserved(UserDid did, CucmDirectoryNumber line)
    {
        if (!string.IsNullOrWhiteSpace(line.Usage) &&
            !line.Usage.Equals("Device", StringComparison.OrdinalIgnoreCase))
        {
            return $"Reserved by function in CUCM (usage: {line.Usage}).";
        }
        return did.Assignment is { } assignment
            ? $"Assigned to phone '{assignment.PhoneName}' line {assignment.LineIndex}" +
                (assignment.UserId is null ? "." : $" for user '{assignment.UserId}'.")
            : "Configured in CUCM and already in use (not tracked locally as a provisioned " +
                "assignment).";
    }
}
