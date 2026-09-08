using Vt.ModuleSdk;
using VSharp.Cucm;
using VSharp.Cucm.Models;

return await ModuleApplication
    .Create("cucm", "CUCM")
    .Setting("publisher", "CUCM publisher hostname")
    .Setting("port", "CUCM AXL HTTPS port", defaultValue: "8443")
    .Setting("axl-version", "CUCM AXL schema version", defaultValue: "15.0")
    .Setting(
        "trusted-certificate",
        "Path to a PEM certificate trusted for CUCM AXL",
        required: false)
    .Setting(
        "room-partitions",
        "JSON object mapping phone locations to CUCM room route partitions",
        required: false,
        defaultValue: "{}")
    .Setting(
        "phone-check-placeholder",
        "Temporary JSON assignment with userDn, roomNumber, and location",
        required: false,
        defaultValue: "{\"userDn\":\"1000\",\"roomNumber\":\"3000\",\"location\":\"default\"}")
    .Setting(
        "user-did-partition",
        "Route partition used when importing and creating user DIDs",
        required: false)
    .Setting(
        "user-did-css",
        "Calling search space used when creating user DIDs",
        required: false)
    .Setting(
        "user-did-voicemail-profile",
        "Voicemail profile used when creating user DIDs",
        required: false)
    .Setting(
        "user-did-description-prefix",
        "Description prefix used when creating user DIDs",
        required: false,
        defaultValue: "User DID")
    .Secret("AXL_USERNAME", "CUCM AXL username")
    .Secret("AXL_PASSWORD", "CUCM AXL password")
    .Default("Run 'vt cucm users list', 'vt cucm phones list', or 'vt cucm dn list'.")
    .Command(
        "users",
        "Browse CUCM users and their phones through AXL.",
        UsersAsync,
        ModuleResponseKind.Table)
    .Command(
        "phones",
        "Browse CUCM phones and update line appearance labels through AXL.",
        PhonesAsync,
        ModuleResponseKind.Table)
    .Command(
        "dn",
        "List, inspect, and create CUCM directory numbers through AXL.",
        DirectoryNumbersAsync,
        ModuleResponseKind.Table)
    .Command(
        "dids",
        "Manage the local approved inventory of user DNs.",
        UserDidsAsync,
        ModuleResponseKind.Table)
    .Command(
        "number",
        "Alias for the CUCM directory-number command.",
        DirectoryNumbersAsync,
        ModuleResponseKind.Table)
    .RunAsync(args);

static async ValueTask<int> UsersAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var dids = await new UserDidStore(context.DataDirectory)
                .LoadAsync(context.CancellationToken);
            var rows = new List<ModuleTableRow>();
            await foreach (var user in cucm.ListUsersAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var id = user.UserId ?? user.Uuid ?? $"user-{rows.Count + 1}";
                var userExtension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
                rows.Add(new ModuleTableRow(
                    id,
                    [
                        Clean(user.UserId),
                        Clean(user.DisplayName),
                        Clean(user.Email),
                        Clean(user.TelephoneNumber),
                        Clean(userExtension),
                        UserDnStatus(user.UserId, userExtension, dids),
                        Clean(string.Join(",", user.AssociatedDevices)),
                    ],
                    string.IsNullOrWhiteSpace(user.UserId)
                        ? null
                        : ["users", "select", user.UserId]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM users",
                ["USER ID", "DISPLAY NAME", "EMAIL", "LDAP PHONE", "USER DN", "DN STATUS", "DEVICES"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var userId])
        {
            var user = await cucm.GetUserAsync(userId, context.CancellationToken) ??
                throw new InvalidOperationException($"CUCM user '{userId}' was not found.");
            var userExtension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
            var did = await FindUserDidAsync(context, userExtension);
            var rows = user.AssociatedDevices
                .Select(device => new ModuleTableRow(
                    device,
                    ["Phone", device],
                    ["phones", "select", device]))
                .Prepend(new ModuleTableRow(
                    "assign-phone",
                    [
                        "Assign phone and DN",
                        UserDnStatus(user.UserId, userExtension, did is null ? [] : [did]),
                    ],
                    did is { Assignment: null }
                        ? ["users", "phones", userId]
                        : null))
                .Prepend(new ModuleTableRow(
                    "user-dn",
                    ["LDAP user DN", Clean(userExtension)]))
                .Prepend(new ModuleTableRow(
                    "all-phones",
                    ["Browse", "All CUCM phones"],
                    ["phones", "list"]))
                .ToArray();
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM user: {user.UserId ?? userId}",
                ["ACTION", "DETAIL"],
                rows));
            return 0;
        }
        if (context.Arguments is ["phones", var phoneUserId])
        {
            var user = await RequireUserAsync(cucm, phoneUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(context, user);
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(
                cancellationToken: context.CancellationToken))
            {
                var phoneName = phone.Name;
                if (string.IsNullOrWhiteSpace(phoneName))
                {
                    continue;
                }
                var eligible = string.IsNullOrWhiteSpace(phone.OwnerUserName) ||
                    phone.OwnerUserName.Equals(phoneUserId, StringComparison.OrdinalIgnoreCase);
                rows.Add(new ModuleTableRow(
                    phoneName,
                    [
                        phoneName,
                        Clean(phone.Description),
                        Clean(phone.OwnerUserName),
                        eligible ? $"Assign {did.Pattern}" : "Owned by another user",
                    ],
                    eligible ? ["users", "phone", phoneUserId, phoneName] : null));
            }
            await context.RespondAsync(new ModuleTableResponse(
                $"Select a phone for {user.DisplayName ?? phoneUserId} ({did.Pattern})",
                ["PHONE", "DESCRIPTION", "OWNER", "STATUS"],
                rows));
            return 0;
        }
        if (context.Arguments is ["phone", var slotUserId, var slotPhoneName])
        {
            var user = await RequireUserAsync(cucm, slotUserId, context.CancellationToken);
            _ = await RequireAvailableUserDidForUserAsync(context, user);
            var phone = await RequirePhoneAsync(cucm, slotPhoneName, context.CancellationToken);
            EnsurePhoneCanBeAssignedToUser(phone, slotUserId);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Assign {UserDidStore.NormalizeUserExtension(user.TelephoneNumber)} to {slotPhoneName}",
                "Line slot index",
                ["users", "review", slotUserId, slotPhoneName],
                "1"));
            return 0;
        }
        if (context.Arguments is
            ["review", var reviewUserId, var reviewPhoneName, var reviewSlotText] &&
            int.TryParse(reviewSlotText, out var reviewSlot) && reviewSlot > 0)
        {
            var user = await RequireUserAsync(cucm, reviewUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidForUserAsync(context, user);
            var phone = await RequirePhoneAsync(cucm, reviewPhoneName, context.CancellationToken);
            EnsurePhoneCanBeAssignedToUser(phone, reviewUserId);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                did.RoutePartitionName,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            var reviewUserResolvedPartition = existing?.RoutePartitionName ?? did.RoutePartitionName;
            var currentLine = phone.Lines.FirstOrDefault(line => line.Index == reviewSlot);
            var alreadyAssociated = user.AssociatedDevices.Contains(
                reviewPhoneName,
                StringComparer.OrdinalIgnoreCase);
            await context.RespondAsync(new ModuleTableResponse(
                "Review user phone assignment",
                ["USER", "USER DN", "PARTITION", "PHONE", "SLOT", "CURRENT", "ASSOCIATION", "DN ACTION"],
                [
                    new ModuleTableRow(
                        "assign",
                        [
                            user.DisplayName ?? reviewUserId,
                            did.Pattern,
                            DisplayPartition(reviewUserResolvedPartition),
                            reviewPhoneName,
                            reviewSlot.ToString(),
                            Clean(currentLine?.Pattern),
                            alreadyAssociated ? "Keep existing" : "Add to user",
                            existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                        ],
                        [
                            "users", "assign", reviewUserId, reviewPhoneName, reviewSlot.ToString(),
                            did.Pattern, did.RoutePartitionName ?? string.Empty,
                            reviewUserResolvedPartition ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["assign", var assignmentUserId, var assignmentPhoneName, var assignmentSlotText,
                var assignmentPattern, var inventoryPartition, var assignmentUserResolvedPartition] &&
            int.TryParse(assignmentSlotText, out var assignmentSlot) && assignmentSlot > 0)
        {
            var user = await RequireUserAsync(cucm, assignmentUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(
                context,
                assignmentPattern,
                inventoryPartition);
            if (!string.Equals(
                UserDidStore.NormalizeUserExtension(user.TelephoneNumber),
                did.Pattern,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"CUCM user '{assignmentUserId}' no longer maps to DN '{did.Pattern}'.");
            }
            var phone = await RequirePhoneAsync(
                cucm,
                assignmentPhoneName,
                context.CancellationToken);
            EnsurePhoneCanBeAssignedToUser(phone, assignmentUserId);
            var effectivePartition = Normalize(assignmentUserResolvedPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                effectivePartition,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern,
                        effectivePartition,
                        did.Description,
                        did.CallingSearchSpaceName,
                        did.VoiceMailProfileName),
                    context.CancellationToken);
            }

            var previousDevices = user.AssociatedDevices.ToArray();
            var addAssociation = !previousDevices.Contains(
                assignmentPhoneName,
                StringComparer.OrdinalIgnoreCase);
            if (addAssociation)
            {
                await cucm.UpdateUserAssociatedDevicesAsync(
                    assignmentUserId,
                    previousDevices.Append(assignmentPhoneName),
                    context.CancellationToken);
            }
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                    assignmentPhoneName,
                    assignmentSlot,
                    did.Pattern,
                    effectivePartition,
                    assignmentUserId,
                    context.CancellationToken);
            }
            catch (Exception assignmentException)
            {
                if (addAssociation)
                {
                    try
                    {
                        await cucm.UpdateUserAssociatedDevicesAsync(
                            assignmentUserId,
                            previousDevices,
                            context.CancellationToken);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "The phone update failed, and the user-device association could not " +
                            "be rolled back.",
                            new AggregateException(assignmentException, rollbackException));
                    }
                }
                throw;
            }
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                assignmentPhoneName,
                assignmentSlot,
                assignmentUserId,
                effectivePartition,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Assigned phone '{assignmentPhoneName}' and DN '{did.Pattern}' " +
                $"to CUCM user '{assignmentUserId}'.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm users list [--max <count>] [--page-size <count>]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static string UserDnStatus(
    string? userId,
    string? extension,
    IReadOnlyList<UserDid> dids)
{
    if (extension is null)
    {
        return "No four-digit LDAP DN";
    }
    var did = dids.FirstOrDefault(candidate =>
        candidate.Pattern.Equals(extension, StringComparison.Ordinal));
    if (did is null)
    {
        return "Not in local pool";
    }
    if (did.Assignment is null)
    {
        return "Available";
    }
    return string.Equals(did.Assignment.UserId, userId, StringComparison.OrdinalIgnoreCase)
        ? $"Assigned to {did.Assignment.PhoneName}"
        : $"Assigned to {did.Assignment.UserId ?? did.Assignment.PhoneName}";
}

static async Task<UserDid?> FindUserDidAsync(ModuleContext context, string? extension)
{
    if (extension is null)
    {
        return null;
    }
    var matches = (await new UserDidStore(context.DataDirectory)
        .LoadAsync(context.CancellationToken))
        .Where(did => did.Pattern.Equals(extension, StringComparison.Ordinal))
        .ToArray();
    return matches.Length switch
    {
        0 => null,
        1 => matches[0],
        _ => throw new InvalidOperationException(
            $"User DN '{extension}' appears more than once in the local inventory."),
    };
}

static async Task<UserDid> RequireAvailableUserDidForUserAsync(
    ModuleContext context,
    CucmUser user)
{
    var extension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber) ??
        throw new InvalidOperationException(
            $"CUCM user '{user.UserId}' does not have a usable four-digit LDAP telephone number.");
    var did = await FindUserDidAsync(context, extension) ??
        throw new InvalidOperationException(
            $"CUCM user '{user.UserId}' maps to DN '{extension}', which is not in the local pool.");
    if (did.Assignment is not null)
    {
        throw new InvalidOperationException(
            $"User DN '{extension}' is already assigned to '{did.Assignment.PhoneName}'.");
    }
    return did;
}

static async Task<CucmUser> RequireUserAsync(
    CucmService cucm,
    string userId,
    CancellationToken cancellationToken) =>
    await cucm.GetUserAsync(userId, cancellationToken) ??
        throw new InvalidOperationException($"CUCM user '{userId}' was not found.");

static void EnsurePhoneCanBeAssignedToUser(CucmPhone phone, string userId)
{
    if (!string.IsNullOrWhiteSpace(phone.OwnerUserName) &&
        !phone.OwnerUserName.Equals(userId, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"CUCM phone '{phone.Name}' is owned by '{phone.OwnerUserName}' and cannot be " +
            $"assigned to '{userId}'.");
    }
}

static async ValueTask<int> PhonesAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            await context.ReportProgressAsync("Counting CUCM phones", 0, 1);
            var availablePhones = await cucm.CountPhonesAsync(context.CancellationToken);
            var totalPhones = Math.Min(availablePhones, maxRecords ?? availablePhones);
            if (totalPhones == 0)
            {
                await context.ReportProgressAsync("No CUCM phones found", 1, 1);
            }
            else
            {
                await context.ReportProgressAsync(
                    $"Loading 0 of {totalPhones} CUCM phones",
                    0,
                    totalPhones);
            }
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var id = phone.Name ?? phone.Uuid ?? $"phone-{rows.Count + 1}";
                rows.Add(new ModuleTableRow(
                    id,
                    [
                        Clean(phone.Name),
                        Clean(phone.Description),
                        Clean(phone.Model ?? phone.Product),
                        Clean(phone.Protocol),
                        Clean(phone.OwnerUserName),
                    ],
                    string.IsNullOrWhiteSpace(phone.Name)
                        ? null
                        : ["phones", "select", phone.Name]));
                if (rows.Count <= totalPhones)
                {
                    await context.ReportProgressAsync(
                        $"Loading {rows.Count} of {totalPhones} CUCM phones",
                        rows.Count,
                        totalPhones);
                }
            }
            if (rows.Count != totalPhones)
            {
                await context.ReportProgressAsync(
                    $"Loaded {rows.Count} CUCM phones",
                    1,
                    1);
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM phones",
                ["NAME", "DESCRIPTION", "MODEL", "PROTOCOL", "OWNER"],
                rows));
            return 0;
        }
        if (context.Arguments is ["select", var phoneName])
        {
            var phone = await RequirePhoneAsync(cucm, phoneName, context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM phone: {phone.Name ?? phoneName}",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "numbers",
                        ["Numbers", $"{phone.Lines.Count} line appearance(s)"],
                        ["phones", "lines", phoneName]),
                    new ModuleTableRow(
                        "assign-user-did",
                        ["Assign user DN to slot", "Select an available user DN for a line index"],
                        ["phones", "assign-slot", phoneName]),
                    new ModuleTableRow(
                        "checks",
                        ["Check configuration", "Validate this phone against a named profile"],
                        ["phones", "check", phoneName]),
                ]));
            return 0;
        }
        if (context.Arguments is ["assign-slot", var phoneNameForSlot])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Assign user DID on {phoneNameForSlot}",
                "Line slot index",
                ["phones", "slot", phoneNameForSlot]));
            return 0;
        }
        if (context.Arguments is ["slot", var phoneNameForSlotSelection, var slotIndexText] &&
            int.TryParse(slotIndexText, out var slotIndex) && slotIndex > 0)
        {
            _ = await RequirePhoneAsync(cucm, phoneNameForSlotSelection, context.CancellationToken);
            await RespondWithAvailableUserDidsAsync(
                context,
                phoneNameForSlotSelection,
                slotIndex);
            return 0;
        }
        if (context.Arguments is ["check", var phoneNameForChecks])
        {
            await context.RespondAsync(new ModuleTableResponse(
                $"Configuration checks for {phoneNameForChecks}",
                ["PROFILE", "DETAIL"],
                [
                    new ModuleTableRow(
                        "basic-room",
                        ["Basic room", "No checks configured yet"],
                        ["phones", "check", phoneNameForChecks, "basic-room"]),
                    new ModuleTableRow(
                        "classroom",
                        ["Classroom", "Validate assigned-user and room lines"],
                        ["phones", "check", phoneNameForChecks, "classroom"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["check", var phoneNameToCheck, var profileName] &&
            PhoneConfigurationChecks.TryParseProfile(profileName, out var profile))
        {
            await context.ReportProgressAsync("Loading CUCM phone", 0, 3);
            var phone = await RequirePhoneAsync(cucm, phoneNameToCheck, context.CancellationToken);
            await context.ReportProgressAsync("Resolving phone assignment", 1, 3);
            var assignment = PhoneConfigurationChecks.ResolvePlaceholderAssignment(
                phone,
                context.Configuration.GetValueOrDefault("phone-check-placeholder") ?? "{}");
            var roomPartitions = PhoneConfigurationChecks.ParseRoomPartitions(
                context.Configuration.GetValueOrDefault("room-partitions") ?? "{}");
            await context.ReportProgressAsync("Evaluating classroom configuration", 2, 3);
            var results = PhoneConfigurationChecks.Evaluate(
                profile,
                phone,
                assignment,
                roomPartitions);
            await context.ReportProgressAsync("Phone configuration check complete", 3, 3);
            await context.RespondAsync(new ModuleTableResponse(
                $"{FormatProfile(profile)} check: {phone.Name ?? phoneNameToCheck}",
                ["CHECK", "EXPECTED", "ACTUAL", "RESULT", "DETAIL"],
                results.Select((result, index) => new ModuleTableRow(
                    $"check-{index + 1}",
                    [
                        result.Name,
                        result.Expected,
                        result.Actual,
                        PhoneConfigurationChecks.DisplayStatus(result.Status),
                        result.Detail,
                    ])).ToArray(),
                Selectable: false,
                Searchable: false));
            return 0;
        }
        if (context.Arguments is ["lines", var phoneNameForLines])
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForLines,
                context.CancellationToken);
            var rows = phone.Lines.Select(line => new ModuleTableRow(
                $"{phoneNameForLines}:{line.Index}",
                [
                    line.Index.ToString(),
                    Clean(line.Pattern),
                    Clean(line.RoutePartitionName),
                    Clean(line.Label),
                    Clean(line.Display),
                ],
                ["phones", "line", phoneNameForLines, line.Index.ToString()])).ToArray();
            await context.RespondAsync(new ModuleTableResponse(
                $"Numbers on {phone.Name ?? phoneNameForLines}",
                ["INDEX", "NUMBER", "PARTITION", "LABEL", "DISPLAY"],
                rows));
            return 0;
        }
        if (context.Arguments is ["line", var phoneNameForLine, var indexText] &&
            int.TryParse(indexText, out var lineIndex) && lineIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForLine,
                lineIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                $"{line.Pattern ?? "Number"} on {phoneNameForLine}",
                ["ACTION", "CURRENT VALUE"],
                [
                    new ModuleTableRow(
                        "set-dn",
                        ["Set directory number", Clean(line.Pattern)],
                        ["phones", "dn", phoneNameForLine, lineIndex.ToString()]),
                    new ModuleTableRow(
                        "assign-user-did",
                        ["Assign available user DN", "Select from the local approved inventory"],
                        ["phones", "dids", phoneNameForLine, lineIndex.ToString()]),
                    new ModuleTableRow(
                        "set-label",
                        ["Set label", Clean(line.Label)],
                        ["phones", "label", phoneNameForLine, lineIndex.ToString()]),
                ]));
            return 0;
        }
        if (context.Arguments is ["dn", var phoneNameForDnPrompt, var dnIndexText] &&
            int.TryParse(dnIndexText, out var dnIndex) && dnIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForDnPrompt,
                dnIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set directory number for line {dnIndex} on {phoneNameForDnPrompt}",
                "Directory number",
                ["phones", "set-dn", phoneNameForDnPrompt, dnIndex.ToString()],
                line.Pattern));
            return 0;
        }
        if (context.Arguments is ["dids", var phoneNameForDids, var didIndexText] &&
            int.TryParse(didIndexText, out var didIndex) && didIndex > 0)
        {
            await RespondWithAvailableUserDidsAsync(context, phoneNameForDids, didIndex);
            return 0;
        }
        if (context.Arguments is
            ["did-review", var phoneNameForDidReview, var reviewIndexText, var reviewPattern, var reviewPartition] &&
            int.TryParse(reviewIndexText, out var reviewIndex) && reviewIndex > 0)
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForDidReview,
                context.CancellationToken);
            var line = phone.Lines.FirstOrDefault(candidate => candidate.Index == reviewIndex);
            var did = await RequireAvailableUserDidAsync(
                context,
                reviewPattern,
                reviewPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                did.RoutePartitionName,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            var reviewResolvedPartition = existing?.RoutePartitionName ?? did.RoutePartitionName;
            await context.RespondAsync(new ModuleTableResponse(
                "Review user DN assignment",
                ["PHONE", "SLOT", "CURRENT", "USER DN", "PARTITION", "DN ACTION", "OWNER"],
                [
                    new ModuleTableRow(
                        "assign",
                        [
                            phone.Name ?? phoneNameForDidReview,
                            reviewIndex.ToString(),
                            string.IsNullOrWhiteSpace(line?.Pattern) ? "<Empty>" : Clean(line.Pattern),
                            did.Pattern,
                            DisplayPartition(reviewResolvedPartition),
                            existing is null ? "Create in CUCM" : "Use existing CUCM DN",
                            Clean(phone.OwnerUserName),
                        ],
                        [
                            "phones", "assign-did", phoneNameForDidReview, reviewIndex.ToString(),
                            did.Pattern, did.RoutePartitionName ?? string.Empty,
                            reviewResolvedPartition ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["assign-did", var phoneNameForDidAssignment, var assignmentIndexText,
                var assignmentPattern, var inventoryPartition, var assignmentResolvedPartition] &&
            int.TryParse(assignmentIndexText, out var assignmentIndex) && assignmentIndex > 0)
        {
            var phone = await RequirePhoneAsync(
                cucm,
                phoneNameForDidAssignment,
                context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(
                context,
                assignmentPattern,
                inventoryPartition);
            var existing = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                Normalize(assignmentResolvedPartition),
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existing);
            if (existing is null)
            {
                await cucm.AddDirectoryNumberAsync(
                    new CucmDirectoryNumberCreateRequest(
                        did.Pattern,
                        Normalize(assignmentResolvedPartition),
                        did.Description,
                        did.CallingSearchSpaceName,
                        did.VoiceMailProfileName),
                    context.CancellationToken);
            }
            await cucm.AssignPhoneLineDirectoryNumberAsync(
                phoneNameForDidAssignment,
                assignmentIndex,
                did.Pattern,
                Normalize(assignmentResolvedPartition),
                context.CancellationToken);
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                phoneNameForDidAssignment,
                assignmentIndex,
                phone.OwnerUserName,
                Normalize(assignmentResolvedPartition),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Assigned user DN '{did.Pattern}' to line {assignmentIndex} on {phoneNameForDidAssignment}" +
                (existing is null ? " after creating the DN in CUCM." : "."));
            return 0;
        }
        if (context.Arguments is ["set-dn", var phoneNameForDnUpdate, var dnUpdateIndexText, var newDn] &&
            int.TryParse(dnUpdateIndexText, out var dnUpdateIndex) && dnUpdateIndex > 0)
        {
            await cucm.UpdatePhoneLineDirectoryNumberAsync(
                phoneNameForDnUpdate,
                dnUpdateIndex,
                newDn,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {dnUpdateIndex} on {phoneNameForDnUpdate} to directory number '{newDn}'.");
            return 0;
        }
        if (context.Arguments is ["label", var phoneNameForPrompt, var labelIndexText] &&
            int.TryParse(labelIndexText, out var labelIndex) && labelIndex > 0)
        {
            var line = await RequireLineAsync(
                cucm,
                phoneNameForPrompt,
                labelIndex,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Set label for {line.Pattern ?? $"line {labelIndex}"}",
                "Label",
                ["phones", "set-label", phoneNameForPrompt, labelIndex.ToString()],
                line.Label,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["set-label", var phoneNameToUpdate, var updateIndexText, var newLabel] &&
            int.TryParse(updateIndexText, out var updateIndex) && updateIndex > 0)
        {
            await cucm.UpdatePhoneLineLabelAsync(
                phoneNameToUpdate,
                updateIndex,
                newLabel,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated line {updateIndex} on {phoneNameToUpdate} with label '{newLabel}'.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm phones list [--max <count>] [--page-size <count>]\n" +
            "       vt cucm phones check <phone> [basic-room|classroom]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithAvailableUserDidsAsync(
    ModuleContext context,
    string phoneName,
    int lineIndex)
{
    var available = (await new UserDidStore(context.DataDirectory)
        .LoadAsync(context.CancellationToken))
        .Where(did => did.Assignment is null)
        .ToArray();
    await context.RespondAsync(new ModuleTableResponse(
        $"Available user DNs for line {lineIndex} on {phoneName}",
        ["DID", "PARTITION", "DESCRIPTION", "STATUS"],
        available.Select(did => new ModuleTableRow(
            $"{did.Pattern}:{did.RoutePartitionName}",
            [
                did.Pattern,
                DisplayPartition(did.RoutePartitionName),
                Clean(did.Description),
                string.IsNullOrWhiteSpace(did.RoutePartitionName)
                    ? "Resolve from CUCM"
                    : "Ready",
            ],
            [
                "phones", "did-review", phoneName, lineIndex.ToString(),
                did.Pattern, did.RoutePartitionName ?? string.Empty,
            ])).ToArray()));
}

static async ValueTask<int> UserDidsAsync(ModuleContext context)
{
    try
    {
        var store = new UserDidStore(context.DataDirectory);
        if (context.Arguments.Count == 0)
        {
            await context.RespondAsync(new ModuleTableResponse(
                "User DN inventory",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "list",
                        ["List", "Browse available and assigned user DNs"],
                        ["dids", "list"]),
                    new ModuleTableRow(
                        "add",
                        ["Add", "Import comma-separated four-digit DNs or a numeric start..end range"],
                        ["dids", "add"]),
                    new ModuleTableRow(
                        "replace",
                        ["Replace", "Replace the unassigned inventory after Save confirmation"],
                        ["dids", "replace"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["list"])
        {
            var dids = await store.LoadAsync(context.CancellationToken);
            await context.RespondAsync(new ModuleTableResponse(
                "Local user DN inventory",
                ["DID", "PARTITION", "STATUS", "USER", "PHONE", "SLOT"],
                dids.Select(did => new ModuleTableRow(
                    $"{did.Pattern}:{did.RoutePartitionName}",
                    [
                        did.Pattern,
                        DisplayPartition(
                            did.Assignment?.RoutePartitionName ?? did.RoutePartitionName),
                        did.Assignment is null ? "Available" : "Assigned",
                        Clean(did.Assignment?.UserId),
                        Clean(did.Assignment?.PhoneName),
                        did.Assignment?.LineIndex.ToString() ?? string.Empty,
                    ])).ToArray(),
                Selectable: false));
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add user DNs",
                "Comma-separated four-digit DNs or numeric start..end range",
                ["dids", "add-values"]));
            return 0;
        }
        if (context.Arguments is ["replace"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Replace user DNs",
                "Comma-separated four-digit extensions or numeric start..end range",
                ["dids", "replace-review"]));
            return 0;
        }
        if (context.Arguments is ["replace-review", var replacementValues])
        {
            var replacementPatterns = UserDidStore.ParsePatterns(replacementValues);
            await context.RespondAsync(new ModuleTableResponse(
                "Review user DN inventory replacement",
                ["ACTION", "NEW COUNT", "FIRST", "LAST"],
                [
                    new ModuleTableRow(
                        "replace",
                        [
                            "Replace inventory",
                            replacementPatterns.Count.ToString(),
                            replacementPatterns.Order(StringComparer.Ordinal).First(),
                            replacementPatterns.Order(StringComparer.Ordinal).Last(),
                        ],
                        ["dids", "replace-values", replacementValues]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["replace-values", var confirmedReplacementValues])
        {
            var replacementPatterns = UserDidStore.ParsePatterns(confirmedReplacementValues);
            await store.ReplaceAsync(
                replacementPatterns,
                context.Configuration.GetValueOrDefault("user-did-partition"),
                context.Configuration.GetValueOrDefault("user-did-description-prefix"),
                context.Configuration.GetValueOrDefault("user-did-css"),
                context.Configuration.GetValueOrDefault("user-did-voicemail-profile"),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Replaced the local inventory with {replacementPatterns.Count} user DNs.");
            return 0;
        }
        if (context.Arguments.Count == 2 &&
            context.Arguments[0] is "add-values" or "add")
        {
            var values = context.Arguments[1];
            var patterns = UserDidStore.ParsePatterns(values);
            var added = await store.AddAsync(
                patterns,
                context.Configuration.GetValueOrDefault("user-did-partition"),
                context.Configuration.GetValueOrDefault("user-did-description-prefix"),
                context.Configuration.GetValueOrDefault("user-did-css"),
                context.Configuration.GetValueOrDefault("user-did-voicemail-profile"),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Added {added} user DN{(added == 1 ? string.Empty : "s")}; " +
                $"{patterns.Count - added} already existed in the local inventory.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm dids [list|add [<dn,dn|start..end>]|replace]");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task<UserDid> RequireAvailableUserDidAsync(
    ModuleContext context,
    string pattern,
    string routePartitionName)
{
    var partition = Normalize(routePartitionName);
    var did = (await new UserDidStore(context.DataDirectory).LoadAsync(context.CancellationToken))
        .FirstOrDefault(candidate =>
            candidate.Pattern.Equals(pattern, StringComparison.Ordinal) &&
            string.Equals(candidate.RoutePartitionName, partition, StringComparison.Ordinal));
    return did is null
        ? throw new InvalidOperationException(
            $"User DN '{pattern}' is not present in the local inventory.")
        : did.Assignment is not null
            ? throw new InvalidOperationException($"User DN '{pattern}' is no longer available.")
            : did;
}

static void EnsureUserDidCanBeAssigned(UserDid did, CucmDirectoryNumber? existing)
{
    if (existing is null && string.IsNullOrWhiteSpace(did.RoutePartitionName))
    {
        throw new InvalidOperationException(
            $"User DN '{did.Pattern}' does not exist in CUCM and has no route partition. " +
            "Configure its location-specific defaults before creating it.");
    }
}

static async ValueTask<int> DirectoryNumbersAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (TryParseListArguments(context.Arguments, out var maxRecords, out var pageSize))
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var line in cucm.ListDirectoryNumbersAsync(
                maxRecords,
                pageSize,
                context.CancellationToken))
            {
                var rowPattern = line.Pattern ?? string.Empty;
                var rowPartition = line.RoutePartitionName ?? string.Empty;
                rows.Add(new ModuleTableRow(
                    line.Uuid ?? $"{rowPattern}:{rowPartition}",
                    [
                        Clean(rowPattern),
                        Clean(rowPartition),
                        Clean(line.Description),
                        Clean(line.CallingSearchSpaceName),
                        Clean(line.VoiceMailProfileName),
                    ],
                    string.IsNullOrWhiteSpace(rowPattern)
                        ? null
                        : DirectoryNumberInfoArguments(rowPattern, rowPartition)));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM directory numbers",
                ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL PROFILE"],
                rows));
            return 0;
        }
        if (TryParseDirectoryNumberInfoArguments(
            context.Arguments,
            out var infoPattern,
            out var infoPartition))
        {
            var line = await cucm.GetDirectoryNumberAsync(
                infoPattern,
                infoPartition,
                context.CancellationToken) ??
                throw new InvalidOperationException(
                    $"CUCM directory number '{infoPattern}' in partition " +
                    $"'{DisplayPartition(infoPartition)}' was not found.");
            await RespondWithDirectoryNumberInfoAsync(context, line);
            return 0;
        }
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add CUCM directory number",
                "Directory number",
                ["dn", "add-description"]));
            return 0;
        }
        if (context.Arguments is ["add-description", var descriptionPattern])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add directory number {descriptionPattern}",
                "Description",
                ["dn", "add-partition", descriptionPattern],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["add-partition", var partitionPattern, var description])
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select route partition",
                "add-css",
                new DnWizardState(partitionPattern, description),
                "partition");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-css", out var partitionState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select line calling search space",
                "add-voicemail-profile",
                partitionState,
                "css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-voicemail-profile",
            out var cssState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select voicemail profile",
                "add-forward",
                cssState,
                "voicemail-profile");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-forward", out var forwardingState))
        {
            await RespondWithForwardingSelectorAsync(context, forwardingState);
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-forward-destination",
            out var destinationState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Configure forward all",
                "Forward destination",
                WizardArguments("add-forward-destination-value", destinationState)));
            return 0;
        }
        if (TryParseWizardStateWithValue(
            context.Arguments,
            "add-forward-destination-value",
            out var destinationValueState,
            out var forwardDestination))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "add-review",
                destinationValueState with { ForwardDestination = forwardDestination },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "add-forward-voicemail",
            out var voicemailForwardState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "add-review",
                voicemailForwardState with { ForwardToVoiceMail = true },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-review", out var reviewState))
        {
            await RespondWithDirectoryNumberReviewAsync(context, reviewState);
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "add-create", out var createState))
        {
            var uuid = await cucm.AddDirectoryNumberAsync(
                createState.ToRequest(),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Created directory number '{createState.Pattern}' in partition " +
                $"'{DisplayPartition(createState.RoutePartitionName)}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid})."));
            return 0;
        }
        if (context.Arguments.Count > 1 && context.Arguments[0] == "add")
        {
            if (!TryParseDirectoryNumberAddArguments(
                context.Arguments.Skip(1).ToArray(),
                out var request,
                out var error))
            {
                context.Error.WriteLine(error);
                WriteDirectoryNumberUsage(context);
                return 2;
            }
            var uuid = await cucm.AddDirectoryNumberAsync(request, context.CancellationToken);
            context.Output.WriteLine(
                $"Created directory number '{request.Pattern}' in partition " +
                $"'{DisplayPartition(request.RoutePartitionName)}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid})."));
            return 0;
        }

        WriteDirectoryNumberUsage(context);
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithDirectoryNumberInfoAsync(
    ModuleContext context,
    CucmDirectoryNumber line)
{
    var cfa = line.CallForwardAll;
    var forwarding = cfa.ForwardToVoiceMail
        ? "Voicemail"
        : string.IsNullOrWhiteSpace(cfa.Destination)
            ? "Disabled"
            : cfa.Destination;
    await context.RespondAsync(new ModuleTableResponse(
        $"CUCM directory number: {line.Pattern}",
        ["PROPERTY", "VALUE"],
        [
            new ModuleTableRow("pattern", ["Pattern", Clean(line.Pattern)]),
            new ModuleTableRow(
                "partition",
                ["Route partition", DisplayPartition(line.RoutePartitionName)]),
            new ModuleTableRow("description", ["Description", Clean(line.Description)]),
            new ModuleTableRow("css", ["Calling search space", Clean(line.CallingSearchSpaceName)]),
            new ModuleTableRow(
                "voicemail-profile",
                ["Voicemail profile", Clean(line.VoiceMailProfileName)]),
            new ModuleTableRow("forward-all", ["Forward all", Clean(forwarding)]),
            new ModuleTableRow(
                "forward-css",
                ["Forward-all CSS", Clean(cfa.CallingSearchSpaceName)]),
        ]));
}

static async Task RespondWithNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    DnWizardState state,
    string resourceType)
{
    var rows = new List<ModuleTableRow>
    {
        new($"none:{resourceType}", ["<None>", string.Empty], WizardArguments(nextRoute, state)),
    };
    var resources = resourceType switch
    {
        "partition" => cucm.ListRoutePartitionsAsync(cancellationToken: context.CancellationToken),
        "css" or "forward-css" => cucm.ListCallingSearchSpacesAsync(
            cancellationToken: context.CancellationToken),
        "voicemail-profile" => cucm.ListVoiceMailProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException($"Unknown CUCM resource type '{resourceType}'."),
    };
    await foreach (var resource in resources)
    {
        if (string.IsNullOrWhiteSpace(resource.Name))
        {
            continue;
        }
        rows.Add(new ModuleTableRow(
            resource.Uuid ?? $"{resourceType}:{resource.Name}",
            [Clean(resource.Name), Clean(resource.Description)],
            WizardArguments(nextRoute, SetWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static Task RespondWithForwardingSelectorAsync(ModuleContext context, DnWizardState state) =>
    context.RespondAsync(new ModuleTableResponse(
        "Configure forward all",
        ["OPTION", "DETAIL"],
        [
            new ModuleTableRow(
                "disabled",
                ["Disabled", "Do not forward all calls"],
                WizardArguments("add-review", state)),
            new ModuleTableRow(
                "destination",
                ["Destination", "Forward all calls to a number"],
                WizardArguments("add-forward-destination", state)),
            new ModuleTableRow(
                "voicemail",
                ["Voicemail", "Forward all calls to voicemail"],
                WizardArguments("add-forward-voicemail", state)),
        ],
        SubmitMode: ModuleTableSubmitMode.Save)).AsTask();

static Task RespondWithDirectoryNumberReviewAsync(
    ModuleContext context,
    DnWizardState state)
{
    var forwarding = state.ForwardToVoiceMail
        ? "Voicemail"
        : string.IsNullOrWhiteSpace(state.ForwardDestination)
            ? "Disabled"
            : state.ForwardDestination;
    return context.RespondAsync(new ModuleTableResponse(
        "Review directory number",
        ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL", "FORWARD ALL", "FORWARD CSS"],
        [
            new ModuleTableRow(
                "create",
                [
                    Clean(state.Pattern),
                    DisplayPartition(state.RoutePartitionName),
                    Clean(state.Description),
                    Clean(state.CallingSearchSpaceName),
                    Clean(state.VoiceMailProfileName),
                    Clean(forwarding),
                    Clean(state.ForwardCallingSearchSpaceName),
                ],
                WizardArguments("add-create", state)),
            ],
            SubmitMode: ModuleTableSubmitMode.Save)).AsTask();
}

static DnWizardState SetWizardResource(
    DnWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "partition" => state with { RoutePartitionName = value },
    "css" => state with { CallingSearchSpaceName = value },
    "voicemail-profile" => state with { VoiceMailProfileName = value },
    "forward-css" => state with { ForwardCallingSearchSpaceName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM resource type '{resourceType}'."),
};

static IReadOnlyList<string> DirectoryNumberInfoArguments(string pattern, string partition) =>
    string.IsNullOrWhiteSpace(partition)
        ? ["dn", "info", pattern]
        : ["dn", "info", pattern, "--partition", partition];

static bool TryParseDirectoryNumberInfoArguments(
    IReadOnlyList<string> arguments,
    out string pattern,
    out string? partition)
{
    pattern = string.Empty;
    partition = null;
    if (arguments.Count < 2 || arguments[0] != "info" || string.IsNullOrWhiteSpace(arguments[1]))
    {
        return false;
    }
    pattern = arguments[1];
    if (arguments.Count == 2)
    {
        return true;
    }
    if (arguments.Count == 4 && arguments[2] == "--partition")
    {
        partition = arguments[3];
        return true;
    }
    return false;
}

static bool TryParseDirectoryNumberAddArguments(
    IReadOnlyList<string> arguments,
    out CucmDirectoryNumberCreateRequest request,
    out string error)
{
    request = new CucmDirectoryNumberCreateRequest(string.Empty);
    error = string.Empty;
    if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
    {
        error = "A directory-number pattern is required.";
        return false;
    }

    string? partition = null;
    string? description = null;
    string? css = null;
    string? voicemailProfile = null;
    string? forwardDestination = null;
    string? forwardCss = null;
    var forwardToVoiceMail = false;
    for (var index = 1; index < arguments.Count; index++)
    {
        var option = arguments[index];
        if (option == "--forward-to-voicemail")
        {
            forwardToVoiceMail = true;
            continue;
        }
        if (index + 1 >= arguments.Count)
        {
            error = $"Option '{option}' requires a value.";
            return false;
        }
        var value = arguments[++index];
        switch (option)
        {
            case "--partition":
                partition = value;
                break;
            case "--description":
                description = value;
                break;
            case "--css":
                css = value;
                break;
            case "--voicemail-profile":
                voicemailProfile = value;
                break;
            case "--forward-destination":
                forwardDestination = value;
                break;
            case "--forward-css":
                forwardCss = value;
                break;
            default:
                error = $"Unknown directory-number option '{option}'.";
                return false;
        }
    }
    if (forwardToVoiceMail && !string.IsNullOrWhiteSpace(forwardDestination))
    {
        error = "Use either --forward-to-voicemail or --forward-destination, not both.";
        return false;
    }
    if (!string.IsNullOrWhiteSpace(forwardCss) &&
        !forwardToVoiceMail &&
        string.IsNullOrWhiteSpace(forwardDestination))
    {
        error = "--forward-css requires --forward-destination or --forward-to-voicemail.";
        return false;
    }

    var callForwardAll = forwardToVoiceMail || !string.IsNullOrWhiteSpace(forwardDestination)
        ? new CucmCallForwardSettings(
            forwardToVoiceMail,
            Normalize(forwardCss),
            Destination: Normalize(forwardDestination))
        : null;
    request = new CucmDirectoryNumberCreateRequest(
        arguments[0],
        Normalize(partition),
        Normalize(description),
        Normalize(css),
        Normalize(voicemailProfile),
        callForwardAll);
    return true;
}

static IReadOnlyList<string> WizardArguments(string route, DnWizardState state) =>
    [
        "dn",
        route,
        state.Pattern,
        state.Description ?? string.Empty,
        state.RoutePartitionName ?? string.Empty,
        state.CallingSearchSpaceName ?? string.Empty,
        state.VoiceMailProfileName ?? string.Empty,
        state.ForwardDestination ?? string.Empty,
        state.ForwardCallingSearchSpaceName ?? string.Empty,
        state.ForwardToVoiceMail.ToString(),
    ];

static bool TryParseWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out DnWizardState state)
{
    state = new DnWizardState(string.Empty);
    if (arguments.Count != 9 || arguments[0] != route)
    {
        return false;
    }
    state = new DnWizardState(
        arguments[1],
        Normalize(arguments[2]),
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]),
        bool.TryParse(arguments[8], out var forwardToVoiceMail) && forwardToVoiceMail);
    return true;
}

static bool TryParseWizardStateWithValue(
    IReadOnlyList<string> arguments,
    string route,
    out DnWizardState state,
    out string value)
{
    state = new DnWizardState(string.Empty);
    value = string.Empty;
    if (arguments.Count != 10 || !TryParseWizardState(arguments.Take(9).ToArray(), route, out state))
    {
        return false;
    }
    value = arguments[9];
    return !string.IsNullOrWhiteSpace(value);
}

static void WriteDirectoryNumberUsage(ModuleContext context) =>
    context.Error.WriteLine(
        "Usage: vt cucm dn list [--max <count>] [--page-size <count>]\n" +
        "       vt cucm dn info <pattern> [--partition <name>]\n" +
        "       vt cucm dn add [<pattern> [--partition <name>] [--description <text>] " +
        "[--css <name>] [--voicemail-profile <name>] [--forward-destination <number>] " +
        "[--forward-css <name>] [--forward-to-voicemail]]");

static string DisplayPartition(string? partition) =>
    string.IsNullOrWhiteSpace(partition) ? "<None>" : Clean(partition);

static string? Normalize(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value;

static string FormatProfile(PhoneConfigurationProfile profile) => profile switch
{
    PhoneConfigurationProfile.BasicRoom => "Basic room",
    PhoneConfigurationProfile.Classroom => "Classroom",
    _ => profile.ToString(),
};

static async Task<CucmService> CreateCucmAsync(ModuleContext context)
{
    var publisher = context.RequireConfiguration("publisher").Trim();
    if (!int.TryParse(context.RequireConfiguration("port"), out var port) ||
        port is < 1 or > 65535)
    {
        throw new InvalidOperationException("CUCM setting 'port' must be between 1 and 65535.");
    }
    var certificatePath = context.Configuration.GetValueOrDefault("trusted-certificate");
    var certificate = string.IsNullOrWhiteSpace(certificatePath)
        ? null
        : await File.ReadAllTextAsync(certificatePath, context.CancellationToken);
    var endpoint = new UriBuilder(Uri.UriSchemeHttps, publisher, port, "axl/").Uri.ToString();
    return new CucmService(new CucmServiceConfig(
        endpoint,
        context.RequireConfiguration("axl-version"),
        context.RequireSecret("AXL_USERNAME"),
        context.RequireSecret("AXL_PASSWORD"),
        certificate));
}

static async Task<VSharp.Cucm.Models.CucmPhone> RequirePhoneAsync(
    CucmService cucm,
    string phoneName,
    CancellationToken cancellationToken) =>
    await cucm.GetPhoneAsync(phoneName, cancellationToken) ??
        throw new InvalidOperationException($"CUCM phone '{phoneName}' was not found.");

static async Task<VSharp.Cucm.Models.CucmPhoneLineAppearance> RequireLineAsync(
    CucmService cucm,
    string phoneName,
    int lineIndex,
    CancellationToken cancellationToken)
{
    var phone = await RequirePhoneAsync(cucm, phoneName, cancellationToken);
    return phone.Lines.FirstOrDefault(line => line.Index == lineIndex) ??
        throw new InvalidOperationException(
            $"CUCM phone '{phoneName}' has no line appearance at index {lineIndex}.");
}

static bool TryParseListArguments(
    IReadOnlyList<string> arguments,
    out int? maxRecords,
    out int pageSize)
{
    maxRecords = null;
    pageSize = CucmService.DefaultPageSize;
    if (arguments.Count == 0 || arguments[0] != "list")
    {
        return false;
    }
    for (var index = 1; index < arguments.Count; index++)
    {
        if (index + 1 >= arguments.Count ||
            !int.TryParse(arguments[index + 1], out var value) ||
            value <= 0)
        {
            return false;
        }
        switch (arguments[index])
        {
            case "--max":
                maxRecords = value;
                break;
            case "--page-size":
                pageSize = value;
                break;
            default:
                return false;
        }
        index++;
    }
    return true;
}

static bool IsExpected(Exception exception) =>
    exception is ArgumentException or
                 HttpRequestException or
                 IOException or
                 InvalidOperationException;

static string Clean(string? value) =>
    value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;

sealed record DnWizardState(
    string Pattern,
    string? Description = null,
    string? RoutePartitionName = null,
    string? CallingSearchSpaceName = null,
    string? VoiceMailProfileName = null,
    string? ForwardDestination = null,
    string? ForwardCallingSearchSpaceName = null,
    bool ForwardToVoiceMail = false)
{
    public CucmDirectoryNumberCreateRequest ToRequest()
    {
        var callForwardAll = ForwardToVoiceMail || !string.IsNullOrWhiteSpace(ForwardDestination)
            ? new CucmCallForwardSettings(
                ForwardToVoiceMail,
                ForwardCallingSearchSpaceName,
                Destination: ForwardDestination)
            : null;
        return new CucmDirectoryNumberCreateRequest(
            Pattern,
            RoutePartitionName,
            Description,
            CallingSearchSpaceName,
            VoiceMailProfileName,
            callForwardAll);
    }
}
