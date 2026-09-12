using System.Text.Json;
using System.Text.RegularExpressions;
using VSharp.Cucm.Models;

internal enum PhoneConfigurationProfile
{
    BasicRoom,
    Classroom,
    RoomRouting,
}

internal enum PhoneCheckStatus
{
    Passed,
    Failed,
    Skipped,
    Unresolved,
}

internal sealed record PhoneAssignment(
    string? UserId,
    string? UserDn,
    string? RoomNumber,
    string? Location);

internal sealed record PhoneCheckResult(
    string Name,
    string Expected,
    string Actual,
    PhoneCheckStatus Status,
    string Detail);

internal sealed record BuildingPattern(
    string RoutePartitionName,
    IReadOnlyList<string> DevicePoolNames);

internal enum TemplateComplianceSlotKind
{
    User,
    Room,
}

internal sealed record TemplateComplianceSlot(int Index, TemplateComplianceSlotKind Kind);

internal sealed record TemplateCompliancePolicy(IReadOnlyList<TemplateComplianceSlot> Slots);

internal enum TemplateComplianceStatus
{
    /// <summary>No template is assigned, no policy is configured for the assigned template, or the
    /// phone's device pool/lines don't contain enough information to evaluate the policy.</summary>
    Unknown,
    Compliant,
    NonCompliant,
}

internal sealed record TemplateComplianceResult(
    TemplateComplianceStatus Status,
    string? RoomNumber,
    string Detail);

internal enum LineTemplateKind
{
    Room,
    User,
}

/// <summary>
/// A named preset applied to a single phone line in one step (see the 'line-templates' setting).
/// Every string field may reference the tokens documented in
/// <see cref="PhoneConfigurationChecks.ApplyLineTemplateTokens"/> (e.g. "{room}", "{userDisplayName}")
/// and is substituted at apply-time. A null/absent field means "leave that CUCM value unchanged" when
/// applied. For <see cref="LineTemplateKind.Room"/> templates, <see cref="RoutePartitionName"/> is
/// ignored — the partition is always derived from the building resolved from the phone's
/// button-template-name prefix (see
/// <see cref="PhoneConfigurationChecks.FindBuildingCodeForPhoneButtonTemplate"/>).
/// </summary>
internal sealed record LineTemplate(
    LineTemplateKind Kind,
    string? RoutePartitionName,
    string? AlertingName,
    string? Display,
    string? Label,
    string? ExternalPhoneNumberMask,
    bool AssociateEndUser);

internal static class PhoneConfigurationChecks
{
    internal const string CompliantIndicator = "\u2714";
    internal const string NonCompliantIndicator = "\u2718";
    internal const string UnknownIndicator = "?";

    private static readonly Regex ComposedDescriptionPattern = new(
        $@"^(?:{Regex.Escape(CompliantIndicator)}|{Regex.Escape(NonCompliantIndicator)})\s*\|\s*[^|]*\|\s*(?<raw>.*)$",
        RegexOptions.Singleline);

    private static readonly Regex UnknownDescriptionPattern = new(
        @"^\?\s*\|\s*(?<raw>.*)$",
        RegexOptions.Singleline);
    internal static PhoneAssignment ResolvePlaceholderAssignment(
        CucmPhone phone,
        string configuration)
    {
        ArgumentNullException.ThrowIfNull(phone);
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new InvalidOperationException(
                "CUCM setting 'phone-check-placeholder' must be a JSON object.");
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "CUCM setting 'phone-check-placeholder' must be a JSON object.");
            }

            return new PhoneAssignment(
                Normalize(phone.OwnerUserName),
                ReadString(document.RootElement, "userDn"),
                ReadString(document.RootElement, "roomNumber"),
                ReadString(document.RootElement, "location"));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "CUCM setting 'phone-check-placeholder' contains invalid JSON.",
                exception);
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseRoomPartitions(
        string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "CUCM setting 'room-partitions' must be a JSON object.");
            }

            var partitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    throw new InvalidOperationException(
                        "CUCM setting 'room-partitions' must map location names to non-empty partition names.");
                }
                partitions[property.Name.Trim()] = property.Value.GetString()!.Trim();
            }
            return partitions;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "CUCM setting 'room-partitions' contains invalid JSON.",
                exception);
        }
    }

    internal static IReadOnlyDictionary<string, BuildingPattern> ParseBuildingPatterns(
        string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "CUCM setting 'building-patterns' must be a JSON object.");
            }

            var patterns = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "CUCM setting 'building-patterns' must map building codes to objects with " +
                        "'routePartitionName' and 'devicePools'.");
                }
                var routePartitionName = ReadString(property.Value, "routePartitionName");
                if (string.IsNullOrWhiteSpace(routePartitionName))
                {
                    throw new InvalidOperationException(
                        $"Building '{property.Name}' in 'building-patterns' is missing a non-empty " +
                        "'routePartitionName'.");
                }
                var devicePools = new List<string>();
                if (property.Value.TryGetProperty("devicePools", out var devicePoolsNode))
                {
                    if (devicePoolsNode.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidOperationException(
                            $"Building '{property.Name}' in 'building-patterns' has a 'devicePools' " +
                            "value that is not an array.");
                    }
                    foreach (var devicePool in devicePoolsNode.EnumerateArray())
                    {
                        if (devicePool.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(devicePool.GetString()))
                        {
                            throw new InvalidOperationException(
                                $"Building '{property.Name}' in 'building-patterns' has a non-string " +
                                "or empty entry in 'devicePools'.");
                        }
                        devicePools.Add(devicePool.GetString()!.Trim());
                    }
                }
                patterns[property.Name.Trim()] = new BuildingPattern(
                    routePartitionName!,
                    devicePools);
            }
            return patterns;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "CUCM setting 'building-patterns' contains invalid JSON.",
                exception);
        }
    }

    internal static IReadOnlyDictionary<string, TemplateCompliancePolicy> ParseTemplateCompliancePolicies(
        string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return new Dictionary<string, TemplateCompliancePolicy>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "CUCM setting 'template-compliance-policies' must be a JSON object.");
            }

            var policies = new Dictionary<string, TemplateCompliancePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "CUCM setting 'template-compliance-policies' must map phone button template names " +
                        "to objects with a 'slots' array.");
                }
                if (!property.Value.TryGetProperty("slots", out var slotsNode) ||
                    slotsNode.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"Phone button template '{property.Name}' in 'template-compliance-policies' is " +
                        "missing a 'slots' array.");
                }
                var slots = new List<TemplateComplianceSlot>();
                foreach (var slotNode in slotsNode.EnumerateArray())
                {
                    if (slotNode.ValueKind != JsonValueKind.Object ||
                        !slotNode.TryGetProperty("index", out var indexNode) ||
                        indexNode.ValueKind != JsonValueKind.Number ||
                        !slotNode.TryGetProperty("kind", out var kindNode) ||
                        kindNode.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            $"Phone button template '{property.Name}' in 'template-compliance-policies' has " +
                            "an invalid slot entry; each slot needs an integer 'index' and a 'kind' of " +
                            "'user' or 'room'.");
                    }
                    var index = indexNode.GetInt32();
                    if (index <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Phone button template '{property.Name}' in 'template-compliance-policies' has " +
                            "a slot with a non-positive index.");
                    }
                    var kind = kindNode.GetString()!.Trim().ToLowerInvariant() switch
                    {
                        "user" => TemplateComplianceSlotKind.User,
                        "room" => TemplateComplianceSlotKind.Room,
                        var other => throw new InvalidOperationException(
                            $"Phone button template '{property.Name}' in 'template-compliance-policies' has " +
                            $"an unknown slot kind '{other}'; expected 'user' or 'room'."),
                    };
                    slots.Add(new TemplateComplianceSlot(index, kind));
                }
                policies[property.Name.Trim()] = new TemplateCompliancePolicy(slots);
            }
            return policies;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "CUCM setting 'template-compliance-policies' contains invalid JSON.",
                exception);
        }
    }

    internal static IReadOnlyDictionary<string, LineTemplate> ParseLineTemplates(string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return new Dictionary<string, LineTemplate>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("CUCM setting 'line-templates' must be a JSON object.");
            }

            var templates = new Dictionary<string, LineTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "CUCM setting 'line-templates' must map template names to objects with a 'kind' " +
                        "of 'room' or 'user'.");
                }
                if (!property.Value.TryGetProperty("kind", out var kindNode) ||
                    kindNode.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException(
                        $"Line template '{property.Name}' in 'line-templates' is missing a 'kind' of " +
                        "'room' or 'user'.");
                }
                var kind = kindNode.GetString()!.Trim().ToLowerInvariant() switch
                {
                    "room" => LineTemplateKind.Room,
                    "user" => LineTemplateKind.User,
                    var other => throw new InvalidOperationException(
                        $"Line template '{property.Name}' in 'line-templates' has an unknown kind " +
                        $"'{other}'; expected 'room' or 'user'."),
                };
                var associateEndUser = property.Value.TryGetProperty("associateEndUser", out var associateNode) &&
                    associateNode.ValueKind == JsonValueKind.True;
                templates[property.Name.Trim()] = new LineTemplate(
                    kind,
                    ReadString(property.Value, "routePartitionName"),
                    ReadString(property.Value, "alertingName"),
                    ReadString(property.Value, "display"),
                    ReadString(property.Value, "label"),
                    ReadString(property.Value, "externalPhoneNumberMask"),
                    associateEndUser);
            }
            return templates;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "CUCM setting 'line-templates' contains invalid JSON.",
                exception);
        }
    }

    /// <summary>
    /// Evaluates a phone's CUCM description compliance based on the compliance policy configured for
    /// its assigned phone button template (see 'template-compliance-policies'). The phone is
    /// <see cref="TemplateComplianceStatus.Unknown"/> whenever there isn't enough configuration to make
    /// a confident pass/fail determination (no template assigned, no policy for that template, the
    /// device pool doesn't map to a known building, or the policy's room slot has no valid room
    /// number). Otherwise every configured slot is checked and the phone is
    /// <see cref="TemplateComplianceStatus.Compliant"/> only if all of them pass.
    /// </summary>
    internal static TemplateComplianceResult EvaluateTemplateCompliance(
        CucmPhone phone,
        IReadOnlyDictionary<string, TemplateCompliancePolicy> policies,
        IReadOnlyDictionary<string, BuildingPattern> buildingPatterns)
    {
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(buildingPatterns);

        if (string.IsNullOrWhiteSpace(phone.PhoneTemplateName))
        {
            return new TemplateComplianceResult(
                TemplateComplianceStatus.Unknown, null, "No phone button template is assigned.");
        }
        if (!policies.TryGetValue(phone.PhoneTemplateName, out var policy) || policy.Slots.Count == 0)
        {
            return new TemplateComplianceResult(
                TemplateComplianceStatus.Unknown,
                null,
                $"No compliance policy is configured for phone button template '{phone.PhoneTemplateName}'.");
        }

        var roomSlot = policy.Slots.FirstOrDefault(slot => slot.Kind == TemplateComplianceSlotKind.Room);
        if (roomSlot is null)
        {
            return new TemplateComplianceResult(
                TemplateComplianceStatus.Unknown,
                null,
                $"Phone button template '{phone.PhoneTemplateName}' has no 'room' slot in its compliance " +
                "policy, so a room number can't be derived.");
        }

        var buildingCodes = FindBuildingCodesForDevicePool(buildingPatterns, phone.DevicePoolName);
        if (buildingCodes.Count != 1)
        {
            return new TemplateComplianceResult(
                TemplateComplianceStatus.Unknown,
                null,
                buildingCodes.Count == 0
                    ? $"No 'building-patterns' entry lists device pool '{Display(phone.DevicePoolName)}'."
                    : $"Device pool '{phone.DevicePoolName}' is listed under multiple buildings.");
        }

        var buildingCode = buildingCodes[0];
        var buildingPattern = buildingPatterns[buildingCode];
        var roomLine = phone.Lines.FirstOrDefault(line => line.Index == roomSlot.Index);
        if (roomLine is null || !IsRoomNumber(roomLine.Pattern))
        {
            return new TemplateComplianceResult(
                TemplateComplianceStatus.Unknown,
                null,
                $"Line {roomSlot.Index} has no valid 3-digit room number to derive a room number from.");
        }

        var roomNumber = buildingCode + roomLine.Pattern;
        var failures = new List<string>();
        if (!string.Equals(
            roomLine.RoutePartitionName, buildingPattern.RoutePartitionName, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Line {roomSlot.Index} is in partition '{Display(roomLine.RoutePartitionName)}', expected " +
                $"'{buildingPattern.RoutePartitionName}' for building {buildingCode}.");
        }
        foreach (var userSlot in policy.Slots.Where(slot => slot.Kind == TemplateComplianceSlotKind.User))
        {
            var userLine = phone.Lines.FirstOrDefault(line => line.Index == userSlot.Index);
            if (userLine is null || string.IsNullOrWhiteSpace(userLine.Pattern))
            {
                failures.Add($"Line {userSlot.Index} is expected to have a user directory number assigned.");
            }
        }

        return failures.Count == 0
            ? new TemplateComplianceResult(
                TemplateComplianceStatus.Compliant, roomNumber, "All configured compliance checks passed.")
            : new TemplateComplianceResult(
                TemplateComplianceStatus.NonCompliant, roomNumber, string.Join(" ", failures));
    }

    /// <summary>
    /// Strips a previously auto-composed "&#x2714;/&#x2718; | room | text" or "? | text" wrapper from a
    /// CUCM phone description, returning the underlying manual/raw text. Descriptions that were never
    /// auto-composed are returned unchanged (trimmed).
    /// </summary>
    internal static string ExtractRawDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }
        var trimmed = description.Trim();
        var composedMatch = ComposedDescriptionPattern.Match(trimmed);
        if (composedMatch.Success)
        {
            return composedMatch.Groups["raw"].Value;
        }
        var unknownMatch = UnknownDescriptionPattern.Match(trimmed);
        return unknownMatch.Success ? unknownMatch.Groups["raw"].Value : trimmed;
    }

    /// <summary>
    /// Composes the final CUCM phone description from a compliance result. Compliant/non-compliant
    /// phones get "&#x2714;/&#x2718; | {RoomNumber} | {userOrDescription}"; phones that could not be
    /// evaluated get "? | {rawText}" (the current/raw description, unchanged).
    /// </summary>
    internal static string ComposeDescription(
        TemplateComplianceResult compliance,
        string rawText,
        string userOrDescription) =>
        compliance.Status switch
        {
            TemplateComplianceStatus.Compliant =>
                $"{CompliantIndicator} | {compliance.RoomNumber} | {userOrDescription}",
            TemplateComplianceStatus.NonCompliant =>
                $"{NonCompliantIndicator} | {compliance.RoomNumber} | {userOrDescription}",
            _ => $"{UnknownIndicator} | {rawText}",
        };

    internal static IReadOnlyList<string> FindBuildingCodesForDevicePool(
        IReadOnlyDictionary<string, BuildingPattern> buildingPatterns,
        string? devicePoolName)
    {
        ArgumentNullException.ThrowIfNull(buildingPatterns);
        if (string.IsNullOrWhiteSpace(devicePoolName))
        {
            return [];
        }
        return buildingPatterns
            .Where(pair => pair.Value.DevicePoolNames.Any(pool =>
                pool.Equals(devicePoolName, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key)
            .ToArray();
    }

    /// <summary>
    /// Resolves a building code from the prefix of a phone button template name (e.g. "CE" from
    /// "CE-UserRoom"), validated against the known building codes in 'building-patterns'. Returns the
    /// canonically-cased building code, or null if the template name has no recognizable prefix or the
    /// prefix doesn't match any configured building.
    /// </summary>
    internal static string? FindBuildingCodeForPhoneButtonTemplate(
        IReadOnlyDictionary<string, BuildingPattern> buildingPatterns,
        string? phoneButtonTemplateName)
    {
        ArgumentNullException.ThrowIfNull(buildingPatterns);
        if (string.IsNullOrWhiteSpace(phoneButtonTemplateName))
        {
            return null;
        }
        var separatorIndex = phoneButtonTemplateName.IndexOf('-');
        var prefix = (separatorIndex > 0 ? phoneButtonTemplateName[..separatorIndex] : phoneButtonTemplateName).Trim();
        if (prefix.Length == 0)
        {
            return null;
        }
        return buildingPatterns.Keys.FirstOrDefault(code => code.Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Token names recognized inside 'line-templates' string fields (alerting name, display, label,
    /// external mask). Unrecognized tokens are left as-is in the output.
    /// </summary>
    internal static readonly IReadOnlyList<string> ApplyLineTemplateTokens =
    [
        "{room}", "{building}", "{pattern}", "{phoneName}", "{lineIndex}",
        "{devicePoolName}", "{userDisplayName}", "{userId}",
    ];

    /// <summary>
    /// Substitutes the tokens in <see cref="ApplyLineTemplateTokens"/> within a template string. Any
    /// token whose corresponding value is null is replaced with an empty string. Returns null unchanged
    /// (meaning "leave this field unchanged").
    /// </summary>
    internal static string? SubstituteLineTemplateTokens(
        string? value,
        string? room = null,
        string? building = null,
        string? pattern = null,
        string? phoneName = null,
        string? lineIndex = null,
        string? devicePoolName = null,
        string? userDisplayName = null,
        string? userId = null)
    {
        if (value is null)
        {
            return null;
        }
        return value
            .Replace("{room}", room ?? string.Empty)
            .Replace("{building}", building ?? string.Empty)
            .Replace("{pattern}", pattern ?? string.Empty)
            .Replace("{phoneName}", phoneName ?? string.Empty)
            .Replace("{lineIndex}", lineIndex ?? string.Empty)
            .Replace("{devicePoolName}", devicePoolName ?? string.Empty)
            .Replace("{userDisplayName}", userDisplayName ?? string.Empty)
            .Replace("{userId}", userId ?? string.Empty);
    }

    internal static bool IsRoomNumber(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiDigit);

    internal static IReadOnlyList<PhoneCheckResult> EvaluateRoomRouting(
        CucmPhone phone,
        IReadOnlyDictionary<string, BuildingPattern> buildingPatterns,
        int roomLineIndex = 3)
    {
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(buildingPatterns);

        var results = new List<PhoneCheckResult>();
        var matches = FindBuildingCodesForDevicePool(buildingPatterns, phone.DevicePoolName);
        if (matches.Count == 0)
        {
            results.Add(new PhoneCheckResult(
                "Building (from device pool)",
                "Exactly one configured building",
                Display(phone.DevicePoolName),
                PhoneCheckStatus.Unresolved,
                $"No 'building-patterns' entry lists device pool '{Display(phone.DevicePoolName)}'."));
            return results;
        }
        if (matches.Count > 1)
        {
            results.Add(new PhoneCheckResult(
                "Building (from device pool)",
                "Exactly one configured building",
                Display(phone.DevicePoolName),
                PhoneCheckStatus.Failed,
                $"Device pool '{phone.DevicePoolName}' is listed under multiple buildings: " +
                $"{string.Join(", ", matches)}."));
            return results;
        }

        var buildingCode = matches[0];
        var buildingPattern = buildingPatterns[buildingCode];
        results.Add(new PhoneCheckResult(
            "Building (from device pool)",
            buildingCode,
            buildingCode,
            PhoneCheckStatus.Passed,
            $"Resolved from device pool '{phone.DevicePoolName}'."));

        var roomLine = phone.Lines.FirstOrDefault(line => line.Index == roomLineIndex);
        if (roomLine is null || string.IsNullOrWhiteSpace(roomLine.Pattern))
        {
            results.Add(new PhoneCheckResult(
                $"Line {roomLineIndex} room DN",
                "A room directory number",
                "<missing>",
                PhoneCheckStatus.Failed,
                $"Line {roomLineIndex} has no directory number."));
            return results;
        }

        results.Add(new PhoneCheckResult(
            $"Line {roomLineIndex} room number format",
            "3-digit room number",
            roomLine.Pattern,
            IsRoomNumber(roomLine.Pattern) ? PhoneCheckStatus.Passed : PhoneCheckStatus.Failed,
            "Room extensions are expected to be exactly 3 digits."));

        results.Add(EqualsCheck(
            $"Line {roomLineIndex} room partition",
            buildingPattern.RoutePartitionName,
            roomLine.RoutePartitionName,
            $"Building {buildingCode} expects partition '{buildingPattern.RoutePartitionName}'."));

        return results;
    }

    internal static IReadOnlyList<PhoneCheckResult> Evaluate(
        PhoneConfigurationProfile profile,
        CucmPhone phone,
        PhoneAssignment assignment,
        IReadOnlyDictionary<string, string> roomPartitions)
    {
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(roomPartitions);

        if (profile == PhoneConfigurationProfile.BasicRoom)
        {
            return
            [
                new PhoneCheckResult(
                    "Basic room configuration",
                    "No checks configured",
                    "No checks configured",
                    PhoneCheckStatus.Skipped,
                    "The basic room profile is intentionally a no-op for now."),
            ];
        }

        var lineOne = phone.Lines.FirstOrDefault(line => line.Index == 1);
        var lineThree = phone.Lines.FirstOrDefault(line => line.Index == 3);
        var results = new List<PhoneCheckResult>();

        if (string.IsNullOrWhiteSpace(assignment.UserId))
        {
            results.Add(new PhoneCheckResult(
                "Line 1 assigned user DN",
                "Not required",
                Display(lineOne?.Pattern),
                PhoneCheckStatus.Skipped,
                "The phone has no assigned user."));
        }
        else if (string.IsNullOrWhiteSpace(assignment.UserDn))
        {
            results.Add(new PhoneCheckResult(
                "Line 1 assigned user DN",
                "User DN from assignment source",
                Display(lineOne?.Pattern),
                PhoneCheckStatus.Unresolved,
                $"No DN was returned for assigned user '{assignment.UserId}'."));
        }
        else
        {
            results.Add(EqualsCheck(
                "Line 1 assigned user DN",
                assignment.UserDn,
                lineOne?.Pattern,
                $"Assigned user: {assignment.UserId}"));
        }

        if (string.IsNullOrWhiteSpace(assignment.RoomNumber))
        {
            results.Add(new PhoneCheckResult(
                "Line 3 room DN",
                "Room number from assignment source",
                Display(lineThree?.Pattern),
                PhoneCheckStatus.Unresolved,
                "No room number was returned for this phone."));
        }
        else
        {
            results.Add(EqualsCheck(
                "Line 3 room DN",
                assignment.RoomNumber,
                lineThree?.Pattern,
                "Room directory number"));
        }

        if (string.IsNullOrWhiteSpace(assignment.Location))
        {
            results.Add(new PhoneCheckResult(
                "Line 3 room partition",
                "Partition mapped from location",
                Display(lineThree?.RoutePartitionName),
                PhoneCheckStatus.Unresolved,
                "No location was returned for this phone."));
        }
        else if (!roomPartitions.TryGetValue(assignment.Location, out var expectedPartition))
        {
            results.Add(new PhoneCheckResult(
                "Line 3 room partition",
                $"Partition mapped from {assignment.Location}",
                Display(lineThree?.RoutePartitionName),
                PhoneCheckStatus.Unresolved,
                $"No room-partitions mapping exists for location '{assignment.Location}'."));
        }
        else
        {
            results.Add(EqualsCheck(
                "Line 3 room partition",
                expectedPartition,
                lineThree?.RoutePartitionName,
                $"Location: {assignment.Location}"));
        }

        return results;
    }

    internal static bool TryParseProfile(string value, out PhoneConfigurationProfile profile)
    {
        profile = value.ToLowerInvariant() switch
        {
            "basic-room" => PhoneConfigurationProfile.BasicRoom,
            "classroom" => PhoneConfigurationProfile.Classroom,
            "room-routing" => PhoneConfigurationProfile.RoomRouting,
            _ => default,
        };
        return value.Equals("basic-room", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("classroom", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("room-routing", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DisplayStatus(PhoneCheckStatus status) => status switch
    {
        PhoneCheckStatus.Passed => "PASS",
        PhoneCheckStatus.Failed => "FAIL",
        PhoneCheckStatus.Skipped => "SKIP",
        PhoneCheckStatus.Unresolved => "UNRESOLVED",
        _ => status.ToString().ToUpperInvariant(),
    };

    private static PhoneCheckResult EqualsCheck(
        string name,
        string expected,
        string? actual,
        string detail)
    {
        var passed = string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        return new PhoneCheckResult(
            name,
            expected,
            Display(actual),
            passed ? PhoneCheckStatus.Passed : PhoneCheckStatus.Failed,
            passed ? detail : $"{detail}; values do not match.");
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? Normalize(value.GetString())
            : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<missing>" : value;
}