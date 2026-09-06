using System.Text.Json;
using VSharp.Cucm.Models;

internal enum PhoneConfigurationProfile
{
    BasicRoom,
    Classroom,
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
            _ => default,
        };
        return value.Equals("basic-room", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("classroom", StringComparison.OrdinalIgnoreCase);
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