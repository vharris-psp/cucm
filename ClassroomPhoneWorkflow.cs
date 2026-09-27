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
    internal static IReadOnlyList<string> SelectTemplate(string phoneName) =>
        ["phones", "template", phoneName];

    internal static IReadOnlyList<string> Apply(string phoneName) =>
        ["phones", "classroom", phoneName];

    internal static IReadOnlyList<string> SelectUser(string phoneName, string userId) =>
        ["phones", "classroom-user", phoneName, userId];

    internal static IReadOnlyList<string> SelectBuilding(
        string phoneName,
        string userId,
        string buildingCode) =>
        ["phones", "classroom-room", phoneName, userId, buildingCode];

    internal static IReadOnlyList<string> SelectScope(
        string phoneName,
        string userId,
        string buildingCode) =>
        ["phones", "classroom-scope", phoneName, userId, buildingCode];

    internal static IReadOnlyList<string> ReviewRoom(
        string phoneName,
        string userId,
        string buildingCode,
        string roomNumber,
        ClassroomApplyScope scope) =>
        [
            "phones", "classroom-review", phoneName, userId, buildingCode, roomNumber,
            ClassroomApplyScopes.ToToken(scope),
        ];

    internal static IReadOnlyList<string> Save(ClassroomPhonePlan plan) =>
        [
            "phones", "classroom-apply", plan.PhoneName, plan.UserId, plan.BuildingCode,
            plan.RoomNumber, ClassroomApplyScopes.ToToken(plan.Scope), plan.Fingerprint,
        ];
}

// Lets a phone be brought partway onto a classroom template: only the room slot, only the user
// slot, or both (the original all-in-one behavior).
internal enum ClassroomApplyScope
{
    Both,
    RoomOnly,
    UserOnly,
}

internal static class ClassroomApplyScopes
{
    private const string BothToken = "both";
    private const string RoomOnlyToken = "room";
    private const string UserOnlyToken = "user";

    internal static string ToToken(ClassroomApplyScope scope) => scope switch
    {
        ClassroomApplyScope.Both => BothToken,
        ClassroomApplyScope.RoomOnly => RoomOnlyToken,
        ClassroomApplyScope.UserOnly => UserOnlyToken,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    internal static ClassroomApplyScope Parse(string token) => token switch
    {
        BothToken => ClassroomApplyScope.Both,
        RoomOnlyToken => ClassroomApplyScope.RoomOnly,
        UserOnlyToken => ClassroomApplyScope.UserOnly,
        _ => throw new InvalidOperationException($"Unknown classroom apply scope '{token}'."),
    };

    internal static string Describe(ClassroomApplyScope scope) => scope switch
    {
        ClassroomApplyScope.Both => "Room and user (full template)",
        ClassroomApplyScope.RoomOnly => "Room number only",
        ClassroomApplyScope.UserOnly => "User/DN only",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

internal sealed record ClassroomTemplateLayout(
    string PhoneTemplateName,
    int UserLineIndex,
    int RoomLineIndex,
    int? SpeedDialButtonIndex = null);

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
    string? ExternalPhoneNumberMask,
    string VoiceMailProfileName,
    string? OwnerUserId,
    bool CreateDirectoryNumber,
    bool AssignLine,
    bool UpdateDirectoryNumber,
    bool UpdateDisplay,
    bool UpdateLabel,
    bool UpdateExternalMask,
    string? ForwardCallingSearchSpaceName = null,
    string? CallingSearchSpaceActivationPolicy = null,
    bool ClearCallPickupGroup = false,
    bool ForwardNoAnswerToVoiceMail = false,
    int? NoAnswerRingDurationSeconds = null);

internal sealed record ClassroomSpeedDialPlan(int AxlIndex, string Destination, string Label, bool Update);

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
    ClassroomApplyScope Scope,
    string DevicePoolName,
    string LocationName,
    string PhoneTemplateName,
    ClassroomLinePlan UserLine,
    ClassroomLinePlan RoomLine,
    ClassroomSpeedDialPlan? AllCallSpeedDial,
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
    LineTemplate UserTemplate,
    ClassroomApplyScope Scope = ClassroomApplyScope.Both,
    string? UserForwardCallingSearchSpaceName = null,
    string? UserCallingSearchSpaceActivationPolicy = null,
    int? UserNoAnswerRingDurationSeconds = null);

internal static class ClassroomPhonePlanner
{
    private const int AllCallAxlSpeedDialIndex = 1;

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
        var speedDialSlots = policy.Slots
            .Where(slot => slot.Kind == TemplateComplianceSlotKind.SpeedDial)
            .Select(slot => slot.Index)
            .Distinct()
            .ToArray();
        if (userSlots.Length != 1 || roomSlots.Length != 1 || userSlots[0] == roomSlots[0])
        {
            throw new InvalidOperationException(
                $"Phone button template '{normalized}' must define exactly one user slot and exactly " +
                "one room slot at different indexes in 'template-compliance-policies'.");
        }
        if (speedDialSlots.Length > 1)
        {
            throw new InvalidOperationException(
                $"Phone button template '{normalized}' must define at most one 'speeddial' slot in " +
                "'template-compliance-policies'.");
        }
        if (speedDialSlots.Length == 1 &&
            (speedDialSlots[0] == userSlots[0] || speedDialSlots[0] == roomSlots[0]))
        {
            throw new InvalidOperationException(
                $"Phone button template '{normalized}' has a 'speeddial' slot at the same index as its " +
                "user or room slot.");
        }

        return new ClassroomTemplateLayout(
            normalized, userSlots[0], roomSlots[0], speedDialSlots.Length == 1 ? speedDialSlots[0] : null);
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
        var devicePoolName = Normalize(buildingPattern.DevicePoolName) ??
            throw new InvalidOperationException(
                $"Location '{input.BuildingCode}' must define 'devicePoolName' in " +
                "'building-patterns' to use the classroom workflow.");
        var locationName = Normalize(buildingPattern.LocationName) ??
            throw new InvalidOperationException(
                $"Location '{input.BuildingCode}' must define 'locationName' in its building profile " +
                "to use the classroom workflow.");
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
        var roomTemplate = CreateClassroomRoomTemplate(input.RoomTemplate) with
        {
            ExternalPhoneNumberMask = Normalize(buildingPattern.RoomExternalPhoneNumberMask),
        };

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
        var includeRoom = input.Scope != ClassroomApplyScope.UserOnly;
        var includeUser = input.Scope != ClassroomApplyScope.RoomOnly;
        var roomLine = CreateLinePlan(
            "Room",
            roomTemplate,
            input.Phone,
            input.RoomDirectoryNumber,
            layout.RoomLineIndex,
            input.RoomNumber,
            buildingPattern.RoutePartitionName,
            devicePoolName,
            input.RoomNumber,
            input.BuildingCode,
            $"{input.BuildingCode} Room {input.RoomNumber}",
            callingSearchSpaceName: null,
            ownerUserId: null,
            userDisplayName: null,
            requireExternalMask: false);
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
            userDisplayName,
            requireExternalMask: true,
            forwardCallingSearchSpaceName: Normalize(input.UserForwardCallingSearchSpaceName),
            callingSearchSpaceActivationPolicy: Normalize(input.UserCallingSearchSpaceActivationPolicy),
            clearCallPickupGroup: true,
            forwardNoAnswerToVoiceMail: true,
            noAnswerRingDurationSeconds: input.UserNoAnswerRingDurationSeconds);
        if (!includeRoom)
        {
            roomLine = ExcludeFromApply(roomLine);
        }
        if (!includeUser)
        {
            userLine = ExcludeFromApply(userLine);
        }

        var roomCurrentAppearance = input.Phone.Lines.FirstOrDefault(line => line.Index == roomLine.Index);
        var userCurrentAppearance = input.Phone.Lines.FirstOrDefault(line => line.Index == userLine.Index);
        var prospectiveLines = input.Phone.Lines
            .Where(line => line.Index != userLine.Index && line.Index != roomLine.Index)
            .Append(includeUser ? ToAppearance(userLine) : userCurrentAppearance)
            .Append(includeRoom ? ToAppearance(roomLine) : roomCurrentAppearance)
            .Where(line => line is not null)
            .Select(line => line!)
            .OrderBy(line => line.Index)
            .ToArray();
        var prospectivePhone = input.Phone with
        {
            DevicePoolName = devicePoolName,
            LocationName = locationName,
            PhoneTemplateName = layout.PhoneTemplateName,
            OwnerUserName = includeUser ? userId : input.Phone.OwnerUserName,
            Lines = prospectiveLines,
        };
        var compliance = PhoneConfigurationChecks.EvaluateTemplateCompliance(
            prospectivePhone,
            input.CompliancePolicies,
            input.BuildingPatterns);
        // A partial apply (room-only/user-only) is an intentional incremental step and may
        // legitimately leave the phone non-compliant until the remaining slot is applied later;
        // only a full "both slots" apply is required to land on a compliant phone.
        if (input.Scope == ClassroomApplyScope.Both && compliance.Status != TemplateComplianceStatus.Compliant)
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
        var addUserAssociation = includeUser && !input.User.AssociatedDevices.Contains(
            phoneName,
            StringComparer.OrdinalIgnoreCase);
        var previousOwnerUserId = Normalize(input.Phone.OwnerUserName);
        var replaceOwner = includeUser && previousOwnerUserId is not null &&
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
            !EqualsValue(input.Phone.LocationName, locationName) ||
            !EqualsValue(input.Phone.PhoneTemplateName, layout.PhoneTemplateName);
        var updateDescription = !string.Equals(
            input.Phone.Description,
            description,
            StringComparison.Ordinal);
        var recordLocalAssignment =
            includeUser && input.UserDidUsesInventoryFallback && input.UserDid.Assignment is null;

        ClassroomSpeedDialPlan? allCallSpeedDial = null;
        if (includeRoom && layout.SpeedDialButtonIndex is not null)
        {
            var allCallNumber = Normalize(buildingPattern.AllCallNumber);
            if (allCallNumber is not null)
            {
                const string allCallLabel = "All Call";
                var currentSpeedDial = (input.Phone.SpeedDials ?? [])
                    .FirstOrDefault(speedDial => speedDial.Index == AllCallAxlSpeedDialIndex);
                var needsUpdate = currentSpeedDial is null ||
                    !EqualsValue(currentSpeedDial.Dirn, allCallNumber) ||
                    !EqualsValue(currentSpeedDial.Label, allCallLabel);
                allCallSpeedDial = new ClassroomSpeedDialPlan(
                    AllCallAxlSpeedDialIndex,
                    allCallNumber,
                    allCallLabel,
                    needsUpdate);
            }
        }

        var changes = CreateChanges(
            input,
            layout,
            devicePoolName,
            locationName,
            roomLine,
            userLine,
            includeRoom,
            includeUser,
            description,
            addUserAssociation,
            previousOwnerUserId,
            removePreviousOwnerAssociation,
            input.UserDidUsesInventoryFallback,
            recordLocalAssignment,
            allCallSpeedDial,
            layout.SpeedDialButtonIndex is not null && Normalize(buildingPattern.AllCallNumber) is null);
        return new ClassroomPhonePlan(
            phoneName,
            userId,
            input.BuildingCode,
            input.RoomNumber,
            input.Scope,
            devicePoolName,
            locationName,
            layout.PhoneTemplateName,
            userLine,
            roomLine,
            allCallSpeedDial,
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
        Append(canonical, plan.Scope.ToString());
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
        string? userDisplayName,
        bool requireExternalMask = true,
        string? forwardCallingSearchSpaceName = null,
        string? callingSearchSpaceActivationPolicy = null,
        bool clearCallPickupGroup = false,
        bool forwardNoAnswerToVoiceMail = false,
        int? noAnswerRingDurationSeconds = null)
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

        var alertingName = ValidateCucmLineText(Resolve(template.AlertingName, "alertingName"), kind, "alertingName");
        var display = ValidateCucmLineText(Resolve(template.Display, "display"), kind, "display");
        var label = ValidateCucmLineText(Resolve(template.Label, "label"), kind, "label");
        // Unlike the other fields, a null template value means "not configured" (e.g. no per-building
        // room mask yet) rather than an error - the field is simply left unmanaged on this line. Only
        // the room line allows this; a user line's mask always comes from its template and is required.
        var externalMask = template.ExternalPhoneNumberMask is null && !requireExternalMask
            ? null
            : Resolve(template.ExternalPhoneNumberMask, "externalPhoneNumberMask");
        var voiceMailProfileName = Resolve(template.VoiceMailProfileName, "voiceMailProfileName");
        var currentLine = phone.Lines.FirstOrDefault(line => line.Index == index);
        var assignLine = currentLine is null ||
            !string.Equals(currentLine.Pattern, pattern, StringComparison.Ordinal) ||
            !EqualsValue(currentLine.RoutePartitionName, routePartitionName) ||
            ownerUserId is not null && !EqualsValue(phone.OwnerUserName, ownerUserId);
        var needsForwardPolicyUpdate = NeedsForwardPolicyUpdate(
            directoryNumber, forwardCallingSearchSpaceName, callingSearchSpaceActivationPolicy,
            clearCallPickupGroup, forwardNoAnswerToVoiceMail, noAnswerRingDurationSeconds);

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
                !EqualsValue(directoryNumber.VoiceMailProfileName, voiceMailProfileName) ||
                needsForwardPolicyUpdate,
            !EqualsValue(currentLine?.Display, display) || !EqualsValue(currentLine?.DisplayAscii, display),
            !EqualsValue(currentLine?.Label, label),
            externalMask is not null && !EqualsValue(currentLine?.ExternalPhoneNumberMask, externalMask),
            forwardCallingSearchSpaceName,
            callingSearchSpaceActivationPolicy,
            clearCallPickupGroup,
            forwardNoAnswerToVoiceMail,
            noAnswerRingDurationSeconds);
    }

    private static IReadOnlyList<ClassroomPhoneChange> CreateChanges(
        ClassroomPhonePlanInput input,
        ClassroomTemplateLayout layout,
        string devicePoolName,
        string locationName,
        ClassroomLinePlan roomLine,
        ClassroomLinePlan userLine,
        bool includeRoom,
        bool includeUser,
        string description,
        bool addUserAssociation,
        string? previousOwnerUserId,
        bool removePreviousOwnerAssociation,
        bool userDidUsesInventoryFallback,
        bool recordLocalAssignment,
        ClassroomSpeedDialPlan? allCallSpeedDial,
        bool allCallNotConfigured)
    {
        var roomCurrent = input.Phone.Lines.FirstOrDefault(line => line.Index == roomLine.Index);
        var userCurrent = input.Phone.Lines.FirstOrDefault(line => line.Index == userLine.Index);
        var changes = new List<ClassroomPhoneChange>
        {
            Change("phone.device-pool", "Device pool", input.Phone.DevicePoolName, devicePoolName, "Update"),
            Change("phone.location", "Location", input.Phone.LocationName, locationName, "Update"),
            Change("phone.button-template", "Phone button template", input.Phone.PhoneTemplateName,
                layout.PhoneTemplateName, "Update"),
            Change("room.dn", $"Room line {roomLine.Index} DN", roomCurrent?.Pattern,
                roomLine.Pattern, roomLine.CreateDirectoryNumber ? "Create and assign" : "Assign", includeRoom),
            Change("room.partition", "Room partition", roomCurrent?.RoutePartitionName,
                roomLine.RoutePartitionName, "Assign", includeRoom),
            Change("room.description", "Room DN description", input.RoomDirectoryNumber?.Description,
                roomLine.CreateDirectoryNumber ? roomLine.Description : input.RoomDirectoryNumber?.Description,
                roomLine.CreateDirectoryNumber ? "Create value" : "No change", includeRoom),
            Change("room.alerting-name", "Room alerting name", input.RoomDirectoryNumber?.AlertingName,
                roomLine.AlertingName, "Update", includeRoom),
            Change("room.caller-id", "Room caller ID", roomCurrent?.Display,
                roomLine.Display, "Update", includeRoom),
            Change("room.label", "Room label", roomCurrent?.Label, roomLine.Label, "Update", includeRoom),
            new ClassroomPhoneChange(
                "room.external-mask",
                "Room external mask",
                Display(roomCurrent?.ExternalPhoneNumberMask),
                !includeRoom ? "<Not applied>"
                    : roomLine.ExternalPhoneNumberMask is null ? "<Not configured for building>"
                    : Display(roomLine.ExternalPhoneNumberMask),
                !includeRoom ? "Skip (excluded)"
                    : roomLine.ExternalPhoneNumberMask is null ? "Configure via 'configure buildings'"
                    : EqualsValue(roomCurrent?.ExternalPhoneNumberMask, roomLine.ExternalPhoneNumberMask)
                        ? "No change" : "Update"),
            Change("room.voicemail", "Room voicemail profile", input.RoomDirectoryNumber?.VoiceMailProfileName,
                roomLine.VoiceMailProfileName, "Update", includeRoom),
            Change("user.dn", $"User line {userLine.Index} DN", userCurrent?.Pattern,
                userLine.Pattern, userLine.CreateDirectoryNumber ? "Create and assign" : "Assign", includeUser),
            Change("user.partition", "User partition", userCurrent?.RoutePartitionName,
                userLine.RoutePartitionName, "Assign", includeUser),
            Change("user.description", "User DN description", input.UserDirectoryNumber?.Description,
                userLine.CreateDirectoryNumber ? userLine.Description : input.UserDirectoryNumber?.Description,
                userLine.CreateDirectoryNumber ? "Create value" : "No change", includeUser),
            Change("user.css", "User calling search space", input.UserDirectoryNumber?.CallingSearchSpaceName,
                userLine.CreateDirectoryNumber
                    ? userLine.CallingSearchSpaceName
                    : input.UserDirectoryNumber?.CallingSearchSpaceName,
                userLine.CreateDirectoryNumber ? "Create value" : "No change", includeUser),
            Change("user.alerting-name", "User alerting name", input.UserDirectoryNumber?.AlertingName,
                userLine.AlertingName, "Update", includeUser),
            Change("user.caller-id", "User caller ID", userCurrent?.Display,
                userLine.Display, "Update", includeUser),
            Change("user.label", "User label", userCurrent?.Label, userLine.Label, "Update", includeUser),
            Change("user.external-mask", "User external mask", userCurrent?.ExternalPhoneNumberMask,
                userLine.ExternalPhoneNumberMask, "Update", includeUser),
            Change("user.voicemail", "User voicemail profile", input.UserDirectoryNumber?.VoiceMailProfileName,
                userLine.VoiceMailProfileName, "Update", includeUser),
            Change("user.owner", "Phone and user-line owner", input.Phone.OwnerUserName,
                input.User.UserId, "Set owner", includeUser),
            new ClassroomPhoneChange(
                "user.dn-association",
                "User DN association (Users Associated with Line)",
                "Not tracked",
                includeUser ? Display(input.User.UserId) : "<Not applied>",
                !includeUser ? "Skip (excluded)"
                    : userLine.CreateDirectoryNumber || userLine.UpdateDirectoryNumber ? "Set" : "No change"),
            new ClassroomPhoneChange(
                "user.association",
                "Selected user device association",
                addUserAssociation ? "Not associated" : "Associated",
                includeUser ? "Associated" : "<Not applied>",
                !includeUser ? "Skip (excluded)" : addUserAssociation ? "Add" : "No change"),
            new ClassroomPhoneChange(
                "previous-owner.association",
                "Previous owner device association",
                previousOwnerUserId ?? "<none>",
                !includeUser ? "<Not applied>" : removePreviousOwnerAssociation ? "Removed" : "No stale association",
                !includeUser ? "Skip (excluded)" : removePreviousOwnerAssociation ? "Remove" : "No change"),
            Change("phone.description", "Description", input.Phone.Description, description, "Update"),
            new ClassroomPhoneChange(
                "inventory.assignment",
                "Local user-DN assignment",
                userDidUsesInventoryFallback
                    ? (recordLocalAssignment ? "Unassigned" : "Assigned")
                    : "Not used",
                !includeUser ? "<Not applied>"
                    : userDidUsesInventoryFallback
                        ? $"{input.Phone.Name} line {userLine.Index}"
                        : "Not used",
                !includeUser ? "Skip (excluded)" : recordLocalAssignment ? "Record" : "No change"),
        };
        if (layout.SpeedDialButtonIndex is { } speedDialButtonIndex)
        {
            var currentSpeedDial = (input.Phone.SpeedDials ?? [])
                .FirstOrDefault(speedDial => speedDial.Index == AllCallAxlSpeedDialIndex);
            changes.Add(new ClassroomPhoneChange(
                "phone.all-call",
                $"All Call speed dial (button {speedDialButtonIndex})",
                Display(currentSpeedDial?.Dirn),
                allCallSpeedDial is not null ? allCallSpeedDial.Destination
                    : allCallNotConfigured ? "<Not configured for building>"
                    : "<none>",
                allCallSpeedDial is not null ? (allCallSpeedDial.Update ? "Update" : "No change")
                    : allCallNotConfigured ? "Configure via 'configure buildings'"
                    : "No change"));
        }
        return changes;
    }

    private static ClassroomPhoneChange Change(
        string key,
        string field,
        string? current,
        string? target,
        string action,
        bool included = true) =>
        included
            ? new(
                key,
                field,
                Display(current),
                Display(target),
                EqualsValue(current, target) ? "No change" : action)
            : new(key, field, Display(current), "<Not applied>", "Skip (excluded)");

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

    // CUCM validates a line's alertingName/display/label against the same DeviceNumPlanMap rule:
    // max 30 characters, none of []"%<>&|{}. Checked here so a bad value (e.g. a display-name
    // prefix that leaves no room for a long name) fails fast during review instead of surfacing as
    // a raw AXL fault mid-Save.
    private static readonly char[] ForbiddenCucmTextCharacters = ['[', ']', '"', '%', '<', '>', '&', '|', '{', '}'];

    private static string ValidateCucmLineText(string value, string kind, string field)
    {
        if (value.Length > 30)
        {
            throw new InvalidOperationException(
                $"The classroom {kind.ToLowerInvariant()} line template's '{field}' resolves to " +
                $"'{value}' ({value.Length} characters), which exceeds CUCM's 30-character limit " +
                "for this field. Shorten the template text or the value it substitutes (e.g. a " +
                "long display name).");
        }
        var invalidIndex = value.IndexOfAny(ForbiddenCucmTextCharacters);
        if (invalidIndex >= 0)
        {
            throw new InvalidOperationException(
                $"The classroom {kind.ToLowerInvariant()} line template's '{field}' resolves to " +
                $"'{value}', which contains the character '{value[invalidIndex]}'. CUCM does not " +
                "allow []\"%<>&|{} in this field.");
        }
        return value;
    }

    private static LineTemplate CreateClassroomRoomTemplate(LineTemplate defaults) => defaults with
    {
        AlertingName = "{building} Room {room}",
        Display = "{building} Room {room}",
        Label = "{building} Room {room}",
    };

    // Strips every actionable flag so an excluded slot's plan carries correct target values (for
    // display/compliance purposes) but the executor performs no CUCM writes for it.
    private static ClassroomLinePlan ExcludeFromApply(ClassroomLinePlan line) => line with
    {
        CreateDirectoryNumber = false,
        AssignLine = false,
        UpdateDirectoryNumber = false,
        UpdateDisplay = false,
        UpdateLabel = false,
        UpdateExternalMask = false,
    };

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

    // True when any configured forward-CSS/activation-policy/pickup-group/ring-duration default
    // doesn't already match the DN's current state, so the caller knows an updateLine is needed.
    private static bool NeedsForwardPolicyUpdate(
        CucmDirectoryNumber? directoryNumber,
        string? forwardCallingSearchSpaceName,
        string? callingSearchSpaceActivationPolicy,
        bool clearCallPickupGroup,
        bool forwardNoAnswerToVoiceMail,
        int? noAnswerRingDurationSeconds)
    {
        if (forwardCallingSearchSpaceName is null &&
            callingSearchSpaceActivationPolicy is null &&
            !clearCallPickupGroup)
        {
            return false;
        }
        if (directoryNumber is null)
        {
            return true;
        }
        if (callingSearchSpaceActivationPolicy is not null &&
            !EqualsValue(directoryNumber.CallingSearchSpaceActivationPolicy, callingSearchSpaceActivationPolicy))
        {
            return true;
        }
        if (clearCallPickupGroup && !string.IsNullOrWhiteSpace(directoryNumber.CallPickupGroupName))
        {
            return true;
        }
        if (forwardCallingSearchSpaceName is null)
        {
            return false;
        }
        bool Mismatched(
            CucmCallForwardSettings? settings,
            bool expectedForwardToVoiceMail = false,
            int? expectedDuration = null) =>
            settings is null ||
            settings.ForwardToVoiceMail != expectedForwardToVoiceMail ||
            !EqualsValue(settings.CallingSearchSpaceName, forwardCallingSearchSpaceName) ||
            (expectedDuration is not null && settings.NoAnswerRingDurationSeconds != expectedDuration);
        return Mismatched(directoryNumber.CallForwardAll) ||
            Mismatched(directoryNumber.CallForwardBusy) ||
            Mismatched(directoryNumber.CallForwardBusyInternal) ||
            Mismatched(directoryNumber.CallForwardNoAnswer, forwardNoAnswerToVoiceMail,
                noAnswerRingDurationSeconds) ||
            Mismatched(directoryNumber.CallForwardNoAnswerInternal, forwardNoAnswerToVoiceMail,
                noAnswerRingDurationSeconds) ||
            Mismatched(directoryNumber.CallForwardNoCoverage) ||
            Mismatched(directoryNumber.CallForwardNoCoverageInternal) ||
            Mismatched(directoryNumber.CallForwardOnFailure) ||
            Mismatched(directoryNumber.CallForwardNotRegistered) ||
            Mismatched(directoryNumber.CallForwardNotRegisteredInternal);
    }

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

    Task UpdateAllCallSpeedDialAsync(
        string phoneName,
        ClassroomSpeedDialPlan speedDial,
        CancellationToken cancellationToken);

    Task UpdateUserAssociationAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);

    Task UpdatePreviousOwnerAssociationAsync(
        ClassroomPhonePlan plan,
        CancellationToken cancellationToken);

    Task UpdateDescriptionAsync(ClassroomPhonePlan plan, CancellationToken cancellationToken);

    Task ApplyPhoneConfigurationAsync(string phoneName, CancellationToken cancellationToken);

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
            $"Classroom Save stopped at '{failedOperation}': {innerException.Message} Completed " +
            $"operations: " +
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
        if (plan.AllCallSpeedDial is { Update: true } allCallSpeedDial)
        {
            await ExecuteAsync(
                "all-call-speed-dial",
                ct => writer.UpdateAllCallSpeedDialAsync(plan.PhoneName, allCallSpeedDial, ct),
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
        if (completed.Count > 0)
        {
            await ExecuteAsync(
                "phone-configuration-refresh",
                ct => writer.ApplyPhoneConfigurationAsync(plan.PhoneName, ct),
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
            plan.PhoneTemplateName,
            plan.LocationName);

    public async Task CreateDirectoryNumberAsync(
        ClassroomLinePlan line,
        CancellationToken cancellationToken)
    {
        var forward = BuildForwardSettings(line);
        await cucm.AddDirectoryNumberAsync(
            new CucmDirectoryNumberCreateRequest(
                line.Pattern,
                line.RoutePartitionName,
                line.Description,
                line.CallingSearchSpaceName,
                line.VoiceMailProfileName,
                AssociatedUserId: line.OwnerUserId,
                CallForwardBusy: forward.Busy,
                CallForwardBusyInternal: forward.BusyInternal,
                CallForwardNoAnswer: forward.NoAnswer,
                CallForwardNoAnswerInternal: forward.NoAnswerInternal,
                CallForwardNoCoverage: forward.NoCoverage,
                CallForwardNoCoverageInternal: forward.NoCoverageInternal,
                CallForwardOnFailure: forward.OnFailure,
                CallForwardNotRegistered: forward.NotRegistered,
                CallForwardNotRegisteredInternal: forward.NotRegisteredInternal,
                CallingSearchSpaceActivationPolicy: line.CallingSearchSpaceActivationPolicy,
                ClearCallPickupGroup: line.ClearCallPickupGroup),
            cancellationToken);
        if (forward.All is not null)
        {
            await cucm.UpdateDirectoryNumberAsync(
                new CucmDirectoryNumberUpdateRequest(
                    line.Pattern, line.RoutePartitionName, CallForwardAll: forward.All),
                cancellationToken);
        }
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
        CancellationToken cancellationToken)
    {
        var forward = BuildForwardSettings(line);
        return cucm.UpdateDirectoryNumberAsync(
            new CucmDirectoryNumberUpdateRequest(
                line.Pattern,
                line.RoutePartitionName,
                VoiceMailProfileName: line.VoiceMailProfileName,
                AlertingName: line.AlertingName,
                AssociatedUserId: line.OwnerUserId,
                CallForwardAll: forward.All,
                CallForwardBusy: forward.Busy,
                CallForwardBusyInternal: forward.BusyInternal,
                CallForwardNoAnswer: forward.NoAnswer,
                CallForwardNoAnswerInternal: forward.NoAnswerInternal,
                CallForwardNoCoverage: forward.NoCoverage,
                CallForwardNoCoverageInternal: forward.NoCoverageInternal,
                CallForwardOnFailure: forward.OnFailure,
                CallForwardNotRegistered: forward.NotRegistered,
                CallForwardNotRegisteredInternal: forward.NotRegisteredInternal,
                CallingSearchSpaceActivationPolicy: line.CallingSearchSpaceActivationPolicy,
                ClearCallPickupGroup: line.ClearCallPickupGroup),
            cancellationToken);
    }

    // Applies the configured CSS to every forward variant. User-DN "No Answer" variants target
    // voicemail and carry the configured ring duration; all other variants remain non-voicemail.
    // "All" is handled separately since 'addLine' can't set it directly.
    internal static (
        CucmCallForwardSettings? All,
        CucmCallForwardSettings? Busy,
        CucmCallForwardSettings? BusyInternal,
        CucmCallForwardSettings? NoAnswer,
        CucmCallForwardSettings? NoAnswerInternal,
        CucmCallForwardSettings? NoCoverage,
        CucmCallForwardSettings? NoCoverageInternal,
        CucmCallForwardSettings? OnFailure,
        CucmCallForwardSettings? NotRegistered,
        CucmCallForwardSettings? NotRegisteredInternal) BuildForwardSettings(ClassroomLinePlan line)
    {
        if (line.ForwardCallingSearchSpaceName is not { } css)
        {
            return (null, null, null, null, null, null, null, null, null, null);
        }
        var settings = new CucmCallForwardSettings(ForwardToVoiceMail: false, CallingSearchSpaceName: css);
        var noAnswerSettings = settings with
        {
            ForwardToVoiceMail = line.ForwardNoAnswerToVoiceMail,
            NoAnswerRingDurationSeconds = line.NoAnswerRingDurationSeconds,
        };
        return (settings, settings, settings, noAnswerSettings, noAnswerSettings, settings, settings, settings,
            settings, settings);
    }

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

    public Task UpdateAllCallSpeedDialAsync(
        string phoneName,
        ClassroomSpeedDialPlan speedDial,
        CancellationToken cancellationToken) =>
        cucm.UpdatePhoneSpeedDialAsync(
            phoneName,
            speedDial.AxlIndex,
            speedDial.Destination,
            speedDial.Label,
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

    public Task ApplyPhoneConfigurationAsync(
        string phoneName,
        CancellationToken cancellationToken) =>
        cucm.ApplyPhoneConfigurationAsync(phoneName, cancellationToken);

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