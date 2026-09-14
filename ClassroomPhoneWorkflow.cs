using System.Security.Cryptography;
using System.Text;
using VSharp.Cucm;
using VSharp.Cucm.Models;

internal static class ClassroomConfiguration
{
    internal static string RequireValue(IReadOnlyDictionary<string, string> configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return configuration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException(
                $"CUCM setting '{key}' is required for the classroom workflow.");
    }
}

internal static class ClassroomWorkflowNavigation
{
    internal static IReadOnlyList<string> SelectUser(string phoneName, string userId) =>
        ["phones", "classroom-user", phoneName, userId];

    internal static IReadOnlyList<string> SelectBuilding(
        string phoneName,
        string userId,
        string buildingCode) =>
        ["phones", "classroom-room", phoneName, userId, buildingCode];

    internal static IReadOnlyList<string> ReviewRoom(
        string phoneName,
        string userId,
        string buildingCode) =>
        ["phones", "classroom-review", phoneName, userId, buildingCode];

    internal static IReadOnlyList<string> Save(ClassroomPhonePlan plan) =>
        [
            "phones", "classroom-apply", plan.PhoneName, plan.UserId, plan.BuildingCode,
            plan.RoomNumber, plan.Fingerprint,
        ];
}

internal sealed record ClassroomTemplateLayout(
    string PhoneTemplateName,
    int UserLineIndex,
    int RoomLineIndex);

internal sealed record ClassroomLinePlan(
    string Kind,
    int Index,
    string Pattern,
    string? RoutePartitionName,
    string? Description,
    string? CallingSearchSpaceName,
    string AlertingName,
    string Display,
    string Label,
    string ExternalPhoneNumberMask,
    string VoiceMailProfileName,
    string? OwnerUserId,
    bool CreateDirectoryNumber,
    bool AssignLine,
    bool UpdateDirectoryNumber,
    bool UpdateDisplay,
    bool UpdateLabel,
    bool UpdateExternalMask);

internal sealed record ClassroomPhoneChange(
    string Key,
    string Field,
    string Current,
    string Target,
    string Action);

internal sealed record ClassroomPhonePlan(
    string PhoneName,
    string UserId,
    string BuildingCode,
    string RoomNumber,
    string DevicePoolName,
    string PhoneTemplateName,
    ClassroomLinePlan UserLine,
    ClassroomLinePlan RoomLine,
    string? UserDidInventoryRoutePartitionName,
    IReadOnlyList<string> UserAssociatedDevices,
    string? PreviousOwnerUserId,
    IReadOnlyList<string> PreviousOwnerRemainingDevices,
    string Description,
    bool UpdatePhoneProfile,
    bool AddUserAssociation,
    bool RemovePreviousOwnerAssociation,
    bool UpdateDescription,
    bool RecordLocalAssignment,
    IReadOnlyList<ClassroomPhoneChange> Changes)
{
    internal string Fingerprint => ClassroomPhonePlanner.CreateFingerprint(this);
}

internal sealed record ClassroomPhonePlanInput(
    CucmPhone Phone,
    CucmUser User,
    UserDid UserDid,
    bool UserDidUsesInventoryFallback,
    CucmDirectoryNumber? UserDirectoryNumber,
    CucmDirectoryNumber? RoomDirectoryNumber,
    CucmUser? PreviousOwner,
    IReadOnlyDictionary<string, BuildingPattern> BuildingPatterns,
    IReadOnlyDictionary<string, TemplateCompliancePolicy> CompliancePolicies,
    string BuildingCode,
    string RoomNumber,
    LineTemplate RoomTemplate,
    LineTemplate UserTemplate);

internal static class ClassroomPhonePlanner
{
    internal static ClassroomTemplateLayout ResolveTemplateLayout(
        string? phoneTemplateName,
        IReadOnlyDictionary<string, TemplateCompliancePolicy> policies)
    {
        var normalized = Normalize(phoneTemplateName) ??
            throw new InvalidOperationException(
                "The classroom flow requires either the selected building's 'phoneTemplateName' or an " +
                "existing phone button template.");
        if (!policies.TryGetValue(normalized, out var policy))
        {
            throw new InvalidOperationException(
                $"Phone button template '{normalized}' has no 'template-compliance-policies' entry.");
        }

        var userSlots = policy.Slots
            .Where(slot => slot.Kind == TemplateComplianceSlotKind.User)
            .Select(slot => slot.Index)
            .Distinct()
            .ToArray();
        var roomSlots = policy.Slots
            .Where(slot => slot.Kind == TemplateComplianceSlotKind.Room)
            .Select(slot => slot.Index)
            .Distinct()
            .ToArray();
        if (userSlots.Length != 1 || roomSlots.Length != 1 || userSlots[0] == roomSlots[0])
        {
            throw new InvalidOperationException(
                $"Phone button template '{normalized}' must define exactly one user slot and exactly " +
                "one room slot at different indexes in 'template-compliance-policies'.");
        }

        return new ClassroomTemplateLayout(normalized, userSlots[0], roomSlots[0]);
    }

    internal static ClassroomPhonePlan Create(ClassroomPhonePlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var phoneName = Normalize(input.Phone.Name) ??
            throw new InvalidOperationException("The selected CUCM phone has no name.");
        var userId = Normalize(input.User.UserId) ??
            throw new InvalidOperationException("The selected CUCM user has no user ID.");
        if (!PhoneConfigurationChecks.IsRoomNumber(input.RoomNumber))
        {
            throw new InvalidOperationException(
                $"'{input.RoomNumber}' is not a valid three-digit room number.");
        }
        if (!input.BuildingPatterns.TryGetValue(input.BuildingCode, out var buildingPattern))
        {
            throw new InvalidOperationException(
                $"Building '{input.BuildingCode}' is not present in the 'building-patterns' setting.");
        }
        if (buildingPattern.DevicePoolNames.Count != 1)
        {
            throw new InvalidOperationException(
                $"Building '{input.BuildingCode}' must list exactly one device pool in " +
                "'building-patterns' to use the classroom template.");
        }

        var devicePoolName = buildingPattern.DevicePoolNames[0];
        var mappedBuildings = PhoneConfigurationChecks.FindBuildingCodesForDevicePool(
            input.BuildingPatterns,
            devicePoolName);
        if (mappedBuildings.Count != 1 ||
            !mappedBuildings[0].Equals(input.BuildingCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Device pool '{devicePoolName}' must map only to building '{input.BuildingCode}' in " +
                "'building-patterns'.");
        }

        var layout = ResolveTemplateLayout(
            buildingPattern.PhoneTemplateName ?? input.Phone.PhoneTemplateName,
            input.CompliancePolicies);
        ValidateLineTemplates(input.RoomTemplate, input.UserTemplate);

        EnsureRoomDirectoryNumberCanBeAssigned(
            input.RoomNumber,
            buildingPattern.RoutePartitionName,
            input.RoomDirectoryNumber);
        var userRoutePartitionName = Normalize(input.UserDirectoryNumber?.RoutePartitionName) ??
            Normalize(input.UserDid.RoutePartitionName);
        if (input.UserDirectoryNumber is null && userRoutePartitionName is null)
        {
            throw new InvalidOperationException(
                $"User DN '{input.UserDid.Pattern}' does not exist in CUCM and has no route partition. " +
                "Configure its location-specific defaults before creating it.");
        }

        var userDisplayName = Normalize(input.User.DisplayName) ?? Normalize(string.Join(
            " ",
            new[] { input.User.FirstName, input.User.LastName }
                .Where(part => !string.IsNullOrWhiteSpace(part))));
        var roomLine = CreateLinePlan(
            "Room",
            input.RoomTemplate,
            input.Phone,
            input.RoomDirectoryNumber,
            layout.RoomLineIndex,
            input.RoomNumber,
            buildingPattern.RoutePartitionName,
            devicePoolName,
            input.RoomNumber,
            input.BuildingCode,
            $"Room {input.RoomNumber} ({input.BuildingCode})",
            callingSearchSpaceName: null,
            ownerUserId: null,
            userDisplayName: null);
        var userLine = CreateLinePlan(
            "User",
            input.UserTemplate,
            input.Phone,
            input.UserDirectoryNumber,
            layout.UserLineIndex,
            input.UserDid.Pattern,
            userRoutePartitionName,
            devicePoolName,
            room: null,
            building: null,
            input.UserDid.Description,
            input.UserDid.CallingSearchSpaceName,
            userId,
            userDisplayName);

        var prospectiveLines = input.Phone.Lines
            .Where(line => line.Index != userLine.Index && line.Index != roomLine.Index)
            .Append(ToAppearance(userLine))
            .Append(ToAppearance(roomLine))
            .OrderBy(line => line.Index)
            .ToArray();
        var prospectivePhone = input.Phone with
        {
            DevicePoolName = devicePoolName,
            PhoneTemplateName = layout.PhoneTemplateName,
            OwnerUserName = userId,
            Lines = prospectiveLines,
        };
        var compliance = PhoneConfigurationChecks.EvaluateTemplateCompliance(
            prospectivePhone,
            input.CompliancePolicies,
            input.BuildingPatterns);
        if (compliance.Status != TemplateComplianceStatus.Compliant)
        {
            throw new InvalidOperationException(
                $"The selected classroom policy does not produce a compliant phone: {compliance.Detail}");
        }
        var rawDescription = PhoneConfigurationChecks.ExtractRawDescription(input.Phone.Description);
        var description = PhoneConfigurationChecks.ComposeDescription(
            compliance,
            rawDescription,
            userDisplayName ?? rawDescription);

        var userDevices = input.User.AssociatedDevices
            .Append(phoneName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var addUserAssociation = !input.User.AssociatedDevices.Contains(
            phoneName,
            StringComparer.OrdinalIgnoreCase);
        var previousOwnerUserId = Normalize(input.Phone.OwnerUserName);
        var replaceOwner = previousOwnerUserId is not null &&
            !previousOwnerUserId.Equals(userId, StringComparison.OrdinalIgnoreCase);
        var previousOwnerRemainingDevices = input.PreviousOwner?.AssociatedDevices
            .Where(device => !device.Equals(phoneName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var removePreviousOwnerAssociation = replaceOwner &&
            input.PreviousOwner?.AssociatedDevices.Contains(
                phoneName,
                StringComparer.OrdinalIgnoreCase) == true;
        var updatePhoneProfile =
            !EqualsValue(input.Phone.DevicePoolName, devicePoolName) ||
            !EqualsValue(input.Phone.PhoneTemplateName, layout.PhoneTemplateName);
        var updateDescription = !string.Equals(
            input.Phone.Description,
            description,
            StringComparison.Ordinal);
        var recordLocalAssignment =
            input.UserDidUsesInventoryFallback && input.UserDid.Assignment is null;

        var changes = CreateChanges(
            input,
            layout,
            devicePoolName,
            roomLine,
            userLine,
            description,
            addUserAssociation,
            previousOwnerUserId,
            removePreviousOwnerAssociation,
            input.UserDidUsesInventoryFallback,
            recordLocalAssignment);
        return new ClassroomPhonePlan(
            phoneName,
            userId,
            input.BuildingCode,
            input.RoomNumber,
            devicePoolName,
            layout.PhoneTemplateName,
            userLine,
            roomLine,
            input.UserDidUsesInventoryFallback ? input.UserDid.RoutePartitionName : null,
            userDevices,
            replaceOwner ? previousOwnerUserId : null,
            previousOwnerRemainingDevices,
            description,
            updatePhoneProfile,
            addUserAssociation,
            removePreviousOwnerAssociation,
            updateDescription,
            recordLocalAssignment,
            changes);
    }

    internal static string CreateFingerprint(ClassroomPhonePlan plan)
    {
        var canonical = new StringBuilder();
        Append(canonical, plan.PhoneName);
        Append(canonical, plan.UserId);
        Append(canonical, plan.BuildingCode);
        Append(canonical, plan.RoomNumber);
        foreach (var change in plan.Changes)
        {
            Append(canonical, change.Key);
            Append(canonical, change.Current);
            Append(canonical, change.Target);
            Append(canonical, change.Action);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static ClassroomLinePlan CreateLinePlan(
        string kind,
        LineTemplate template,
        CucmPhone phone,
        CucmDirectoryNumber? directoryNumber,
        int index,
        string pattern,
        string? routePartitionName,
        string devicePoolName,
        string? room,
        string? building,
        string? description,
        string? callingSearchSpaceName,
        string? ownerUserId,
        string? userDisplayName)
    {
        string Resolve(string? value, string field) => Normalize(
            PhoneConfigurationChecks.SubstituteLineTemplateTokens(
                value,
                room,
                building,
                pattern,
                phone.Name,
                index.ToString(),
                devicePoolName,
                userDisplayName,
                ownerUserId)) ??
            throw new InvalidOperationException(
                $"The classroom {kind.ToLowerInvariant()} line template must resolve '{field}' " +
                "to a non-empty value.");

        var alertingName = Resolve(template.AlertingName, "alertingName");
        var display = Resolve(template.Display, "display");
        var label = Resolve(template.Label, "label");
        var externalMask = Resolve(template.ExternalPhoneNumberMask, "externalPhoneNumberMask");
        var voiceMailProfileName = Resolve(template.VoiceMailProfileName, "voiceMailProfileName");
        var currentLine = phone.Lines.FirstOrDefault(line => line.Index == index);
        var assignLine = currentLine is null ||
            !string.Equals(currentLine.Pattern, pattern, StringComparison.Ordinal) ||
            !EqualsValue(currentLine.RoutePartitionName, routePartitionName) ||
            ownerUserId is not null && !EqualsValue(phone.OwnerUserName, ownerUserId);

        return new ClassroomLinePlan(
            kind,
            index,
            pattern,
            routePartitionName,
            Normalize(description),
            Normalize(callingSearchSpaceName),
            alertingName,
            display,
            label,
            externalMask,
            voiceMailProfileName,
            ownerUserId,
            directoryNumber is null,
            assignLine,
            directoryNumber is null ||
                !EqualsValue(directoryNumber.AlertingName, alertingName) ||
                !EqualsValue(directoryNumber.VoiceMailProfileName, voiceMailProfileName),
            !EqualsValue(currentLine?.Display, display) || !EqualsValue(currentLine?.DisplayAscii, display),
            !EqualsValue(currentLine?.Label, label),
            !EqualsValue(currentLine?.ExternalPhoneNumberMask, externalMask));
    }

    private static IReadOnlyList<ClassroomPhoneChange> CreateChanges(
        ClassroomPhonePlanInput input,
        ClassroomTemplateLayout layout,
        string devicePoolName,
        ClassroomLinePlan roomLine,
        ClassroomLinePlan userLine,
        string description,
        bool addUserAssociation,
        string? previousOwnerUserId,
        bool removePreviousOwnerAssociation,
        bool userDidUsesInventoryFallback,
        bool recordLocalAssignment)
    {
        var roomCurrent = input.Phone.Lines.FirstOrDefault(line => line.Index == roomLine.Index);
        var userCurrent = input.Phone.Lines.FirstOrDefault(line => line.Index == userLine.Index);
        return
        [
            Change("phone.device-pool", "Device pool", input.Phone.DevicePoolName, devicePoolName, "Update"),
            Change("phone.button-template", "Phone button template", input.Phone.PhoneTemplateName,
                layout.PhoneTemplateName, "Update"),
            Change("room.dn", $"Room line {roomLine.Index} DN", roomCurrent?.Pattern,
                roomLine.Pattern, roomLine.CreateDirectoryNumber ? "Create and assign" : "Assign"),
            Change("room.partition", "Room partition", roomCurrent?.RoutePartitionName,
                roomLine.RoutePartitionName, "Assign"),
            Change("room.description", "Room DN description", input.RoomDirectoryNumber?.Description,
                roomLine.CreateDirectoryNumber ? roomLine.Description : input.RoomDirectoryNumber?.Description,
                roomLine.CreateDirectoryNumber ? "Create value" : "No change"),
            Change("room.alerting-name", "Room alerting name", input.RoomDirectoryNumber?.AlertingName,
                roomLine.AlertingName, "Update"),
            Change("room.caller-id", "Room caller ID", roomCurrent?.Display,
                roomLine.Display, "Update"),
            Change("room.label", "Room label", roomCurrent?.Label, roomLine.Label, "Update"),
            Change("room.external-mask", "Room external mask", roomCurrent?.ExternalPhoneNumberMask,
                roomLine.ExternalPhoneNumberMask, "Update"),
            Change("room.voicemail", "Room voicemail profile", input.RoomDirectoryNumber?.VoiceMailProfileName,
                roomLine.VoiceMailProfileName, "Update"),
            Change("user.dn", $"User line {userLine.Index} DN", userCurrent?.Pattern,
                userLine.Pattern, userLine.CreateDirectoryNumber ? "Create and assign" : "Assign"),
            Change("user.partition", "User partition", userCurrent?.RoutePartitionName,
                userLine.RoutePartitionName, "Assign"),
            Change("user.description", "User DN description", input.UserDirectoryNumber?.Description,
                userLine.CreateDirectoryNumber ? userLine.Description : input.UserDirectoryNumber?.Description,
                userLine.CreateDirectoryNumber ? "Create value" : "No change"),
            Change("user.css", "User calling search space", input.UserDirectoryNumber?.CallingSearchSpaceName,
                userLine.CreateDirectoryNumber
                    ? userLine.CallingSearchSpaceName
                    : input.UserDirectoryNumber?.CallingSearchSpaceName,
                userLine.CreateDirectoryNumber ? "Create value" : "No change"),
            Change("user.alerting-name", "User alerting name", input.UserDirectoryNumber?.AlertingName,
                userLine.AlertingName, "Update"),
            Change("user.caller-id", "User caller ID", userCurrent?.Display,
                userLine.Display, "Update"),
            Change("user.label", "User label", userCurrent?.Label, userLine.Label, "Update"),
            Change("user.external-mask", "User external mask", userCurrent?.ExternalPhoneNumberMask,
                userLine.ExternalPhoneNumberMask, "Update"),
            Change("user.voicemail", "User voicemail profile", input.UserDirectoryNumber?.VoiceMailProfileName,
                userLine.VoiceMailProfileName, "Update"),
            Change("user.owner", "Phone and user-line owner", input.Phone.OwnerUserName,
                input.User.UserId, "Set owner"),
            new ClassroomPhoneChange(
                "user.association",
                "Selected user device association",
                addUserAssociation ? "Not associated" : "Associated",
                "Associated",
                addUserAssociation ? "Add" : "No change"),
            new ClassroomPhoneChange(
                "previous-owner.association",
                "Previous owner device association",
                previousOwnerUserId ?? "<none>",
                removePreviousOwnerAssociation ? "Removed" : "No stale association",
                removePreviousOwnerAssociation ? "Remove" : "No change"),
            Change("phone.description", "Description", input.Phone.Description, description, "Update"),
            new ClassroomPhoneChange(
                "inventory.assignment",
                "Local user-DN assignment",
                userDidUsesInventoryFallback
                    ? (recordLocalAssignment ? "Unassigned" : "Assigned")
                    : "Not used",
                userDidUsesInventoryFallback
                    ? $"{input.Phone.Name} line {userLine.Index}"
                    : "Not used",
                recordLocalAssignment ? "Record" : "No change"),
        ];
    }

    private static ClassroomPhoneChange Change(
        string key,
        string field,
        string? current,
        string? target,
        string action) =>
        new(
            key,
            field,
            Display(current),
            Display(target),
            EqualsValue(current, target) ? "No change" : action);

    private static void ValidateLineTemplates(LineTemplate roomTemplate, LineTemplate userTemplate)
    {
        if (roomTemplate.Kind != LineTemplateKind.Room || userTemplate.Kind != LineTemplateKind.User)
        {
            throw new InvalidOperationException(
                "The classroom flow requires a configured room-kind line template and a " +
                "configured user-kind line template.");
        }
        if (!userTemplate.AssociateEndUser)
        {
            throw new InvalidOperationException(
                "The configured classroom user line template must set 'associateEndUser' to true.");
        }
    }

    private static void EnsureRoomDirectoryNumberCanBeAssigned(
        string roomNumber,
        string expectedRoutePartitionName,
        CucmDirectoryNumber? existing)
    {
        if (existing is not null &&
            !EqualsValue(existing.RoutePartitionName, expectedRoutePartitionName))
        {
            throw new InvalidOperationException(
                $"Room DN '{roomNumber}' already exists in partition '{Display(existing.RoutePartitionName)}', " +
                $"not the expected '{expectedRoutePartitionName}'. Resolve the conflict in CUCM before " +
                "assigning it.");
        }
    }

    private static CucmPhoneLineAppearance ToAppearance(ClassroomLinePlan line) =>
        new(
            line.Index,
            line.Pattern,
            line.RoutePartitionName,
            line.Label,
            line.Display,
            line.Display,
            line.ExternalPhoneNumberMask);

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static bool EqualsValue(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Display(string? value) => Normalize(value) ?? "<none>";
}

internal interface IClassroomPhoneWriter
{
    Task UpdatePhoneProfileAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);

    Task CreateDirectoryNumberAsync(ClassroomLinePlan line, CancellationToken cancellationToken);

    Task AssignLineAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken);

    Task UpdateDirectoryNumberAsync(ClassroomLinePlan line, CancellationToken cancellationToken);

    Task UpdateLineDisplayAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken);

    Task UpdateLineLabelAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken);

    Task UpdateLineExternalMaskAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken);

    Task UpdateUserAssociationAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);

    Task UpdatePreviousOwnerAssociationAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken);

    Task UpdateDescriptionAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);

    Task RecordLocalAssignmentAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);
}

internal sealed record ClassroomPhoneApplyResult(IReadOnlyList<string> CompletedOperations);

internal sealed class ClassroomPhoneApplyException : InvalidOperationException
{
    internal ClassroomPhoneApplyException(
        string failedOperation,
        IReadOnlyList<string> completedOperations,
        Exception innerException)
        : base(
            $"Classroom Save stopped at '{failedOperation}'. Completed operations: " +
            $"{(completedOperations.Count == 0 ? "none" : string.Join(", ", completedOperations))}. " +
            "No automatic rollback is available for CUCM AXL updates. Open the classroom workflow " +
            "again to review the remaining changes before retrying Save.",
            innerException)
    {
        FailedOperation = failedOperation;
        CompletedOperations = completedOperations;
    }

    internal string FailedOperation { get; }

    internal IReadOnlyList<string> CompletedOperations { get; }
}

internal static class ClassroomPhoneExecutor
{
    internal static async Task<ClassroomPhoneApplyResult> ExecuteAsync(
        ClassroomPhonePlan plan,
        IClassroomPhoneWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writer);
        var completed = new List<string>();

        if (plan.UpdatePhoneProfile)
        {
            await ExecuteAsync(
                "phone-profile",
                ct => writer.UpdatePhoneProfileAsync(plan, ct),
                completed,
                cancellationToken);
        }
        await ExecuteLineCreationAsync(plan.RoomLine, writer, completed, cancellationToken);
        await ExecuteLineAssignmentAsync(
            plan.PhoneName,
            plan.RoomLine,
            writer,
            completed,
            cancellationToken);
        await ExecuteLineOptionsAsync(
            plan.PhoneName,
            plan.RoomLine,
            writer,
            completed,
            cancellationToken);
        await ExecuteLineCreationAsync(plan.UserLine, writer, completed, cancellationToken);
        if (plan.AddUserAssociation)
        {
            await ExecuteAsync(
                "selected-user-association",
                ct => writer.UpdateUserAssociationAsync(plan, ct),
                completed,
                cancellationToken);
        }
        if (plan.RemovePreviousOwnerAssociation)
        {
            await ExecuteAsync(
                "previous-owner-association",
                ct => writer.UpdatePreviousOwnerAssociationAsync(plan, ct),
                completed,
                cancellationToken);
        }
        if (plan.UserLine.AssignLine)
        {
            await ExecuteLineAssignmentAsync(
                plan.PhoneName,
                plan.UserLine,
                writer,
                completed,
                cancellationToken);
        }
        await ExecuteLineOptionsAsync(
            plan.PhoneName,
            plan.UserLine,
            writer,
            completed,
            cancellationToken);
        if (plan.UpdateDescription)
        {
            await ExecuteAsync(
                "phone-description",
                ct => writer.UpdateDescriptionAsync(plan, ct),
                completed,
                cancellationToken);
        }
        if (plan.RecordLocalAssignment)
        {
            await ExecuteAsync(
                "local-user-dn-assignment",
                ct => writer.RecordLocalAssignmentAsync(plan, ct),
                completed,
                cancellationToken);
        }

        return new ClassroomPhoneApplyResult(completed);
    }

    private static async Task ExecuteLineCreationAsync(
        ClassroomLinePlan line,
        IClassroomPhoneWriter writer,
        List<string> completed,
        CancellationToken cancellationToken)
    {
        var prefix = line.Kind.ToLowerInvariant() + $"-line-{line.Index}";
        if (line.CreateDirectoryNumber)
        {
            await ExecuteAsync(
                prefix + "-create-dn",
                ct => writer.CreateDirectoryNumberAsync(line, ct),
                completed,
                cancellationToken);
        }
    }

    private static async Task ExecuteLineAssignmentAsync(
        string phoneName,
        ClassroomLinePlan line,
        IClassroomPhoneWriter writer,
        List<string> completed,
        CancellationToken cancellationToken)
    {
        if (line.AssignLine)
        {
            await ExecuteAsync(
                line.Kind.ToLowerInvariant() + $"-line-{line.Index}-assign",
                ct => writer.AssignLineAsync(phoneName, line, ct),
                completed,
                cancellationToken);
        }
    }

    private static async Task ExecuteLineOptionsAsync(
        string phoneName,
        ClassroomLinePlan line,
        IClassroomPhoneWriter writer,
        List<string> completed,
        CancellationToken cancellationToken)
    {
        var prefix = line.Kind.ToLowerInvariant() + $"-line-{line.Index}";
        if (line.UpdateDirectoryNumber)
        {
            await ExecuteAsync(
                prefix + "-dn-options",
                ct => writer.UpdateDirectoryNumberAsync(line, ct),
                completed,
                cancellationToken);
        }
        if (line.UpdateDisplay)
        {
            await ExecuteAsync(
                prefix + "-caller-id",
                ct => writer.UpdateLineDisplayAsync(phoneName, line, ct),
                completed,
                cancellationToken);
        }
        if (line.UpdateLabel)
        {
            await ExecuteAsync(
                prefix + "-label",
                ct => writer.UpdateLineLabelAsync(phoneName, line, ct),
                completed,
                cancellationToken);
        }
        if (line.UpdateExternalMask)
        {
            await ExecuteAsync(
                prefix + "-external-mask",
                ct => writer.UpdateLineExternalMaskAsync(phoneName, line, ct),
                completed,
                cancellationToken);
        }
    }

    private static async Task ExecuteAsync(
        string operation,
        Func<CancellationToken, Task> action,
        List<string> completed,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
            completed.Add(operation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ClassroomPhoneApplyException(operation, completed.ToArray(), exception);
        }
    }
}

internal sealed class CucmClassroomPhoneWriter(
    CucmService cucm,
    UserDidStore userDidStore) : IClassroomPhoneWriter
{
    public Task UpdatePhoneProfileAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneAsync(
            plan.PhoneName,
            description: null,
            plan.DevicePoolName,
            ownerUserName: null,
            cancellationToken,
            plan.PhoneTemplateName);

    public async Task CreateDirectoryNumberAsync(
        ClassroomLinePlan line,
        CancellationToken cancellationToken)
    {
        await cucm.AddDirectoryNumberAsync(
            new CucmDirectoryNumberCreateRequest(
                line.Pattern,
                line.RoutePartitionName,
                line.Description,
                line.CallingSearchSpaceName,
                line.VoiceMailProfileName),
            cancellationToken);
    }

    public Task AssignLineAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken) =>
        line.OwnerUserId is null
            ? cucm.AssignPhoneLineDirectoryNumberAsync(
                phoneName,
                line.Index,
                line.Pattern,
                line.RoutePartitionName,
                cancellationToken)
            : cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                phoneName,
                line.Index,
                line.Pattern,
                line.RoutePartitionName,
                line.OwnerUserId,
                cancellationToken);

    public Task UpdateDirectoryNumberAsync(
        ClassroomLinePlan line,
        CancellationToken cancellationToken) =>
        cucm.UpdateDirectoryNumberAsync(
            new CucmDirectoryNumberUpdateRequest(
                line.Pattern,
                line.RoutePartitionName,
                VoiceMailProfileName: line.VoiceMailProfileName,
                AlertingName: line.AlertingName),
            cancellationToken);

    public Task UpdateLineDisplayAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneLineDisplayAsync(
            phoneName,
            line.Index,
            line.Display,
            line.Display,
            cancellationToken);

    public Task UpdateLineLabelAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneLineLabelAsync(phoneName, line.Index, line.Label, cancellationToken);

    public Task UpdateLineExternalMaskAsync(
        string phoneName,
        ClassroomLinePlan line,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneLineExternalMaskAsync(
            phoneName,
            line.Index,
            line.ExternalPhoneNumberMask,
            cancellationToken);

    public Task UpdateUserAssociationAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken) =>
        cucm.UpdateUserAssociatedDevicesAsync(
            plan.UserId,
            plan.UserAssociatedDevices,
            cancellationToken);

    public Task UpdatePreviousOwnerAssociationAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken) =>
        cucm.UpdateUserAssociatedDevicesAsync(
            plan.PreviousOwnerUserId ??
                throw new InvalidOperationException("The classroom plan has no previous owner."),
            plan.PreviousOwnerRemainingDevices,
            cancellationToken);

    public Task UpdateDescriptionAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneAsync(
            plan.PhoneName,
            plan.Description,
            devicePoolName: null,
            ownerUserName: null,
            cancellationToken);

    public Task RecordLocalAssignmentAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken) =>
        userDidStore.MarkAssignedAsync(
            plan.UserLine.Pattern,
            plan.UserDidInventoryRoutePartitionName,
            plan.PhoneName,
            plan.UserLine.Index,
            plan.UserId,
            plan.UserLine.RoutePartitionName,
            cancellationToken);
}