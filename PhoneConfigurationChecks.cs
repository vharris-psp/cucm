using System.Text.Json;
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

internal static class PhoneConfigurationChecks
{
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