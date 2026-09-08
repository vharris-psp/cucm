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
    .Default(RootAsync)
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
    .Command(
        "provision",
        "Provision a CUCM phone from scratch for a user (create/claim, assign DN, review, save).",
        ProvisionAsync,
        ModuleResponseKind.Table)
    .RunAsync(args);

static async ValueTask<int> RootAsync(ModuleContext context)
{
    await context.RespondAsync(new ModuleTableResponse(
        "CUCM",
        ["AREA", "DESCRIPTION"],
        [
            new ModuleTableRow(
                "users",
                ["Users", "Browse CUCM users and assign phones and DNs"],
                ["users", "list"]),
            new ModuleTableRow(
                "phones",
                ["Phones", "Browse CUCM phones and update line configuration"],
                ["phones", "list"]),
            new ModuleTableRow(
                "dn",
                ["Directory numbers", "List, inspect, and create CUCM directory numbers"],
                ["dn", "list"]),
            new ModuleTableRow(
                "dids",
                ["User DID inventory", "Manage the local approved inventory of user DNs"],
                ["dids"]),
            new ModuleTableRow(
                "provision",
                ["Provision a phone", "Create or claim a phone, assign a user and DN, from scratch"],
                ["provision"]),
        ]));
    return 0;
}

// Composite wizard that chains the existing phone (add/edit) and user (assign) primitives into
// a single guided flow: pick "new" or "existing" phone, select a user with an available local
// DID, review, then create/claim the phone + assign the DID/line to that user in one submit.
static async ValueTask<int> ProvisionAsync(ModuleContext context)
{
    try
    {
        using var cucm = await CreateCucmAsync(context);
        if (context.Arguments.Count == 0)
        {
            await context.RespondAsync(new ModuleTableResponse(
                "Provision a phone",
                ["OPTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "new",
                        ["New phone", "Create a new CUCM phone from scratch"],
                        ["provision", "new"]),
                    new ModuleTableRow(
                        "existing",
                        ["Existing phone", "Claim an unowned CUCM phone"],
                        ["provision", "existing"]),
                ]));
            return 0;
        }
        if (context.Arguments is ["new"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Provision a new phone",
                "Phone name",
                ["provision", "new-description"]));
            return 0;
        }
        if (context.Arguments is ["new-description", var newName] &&
            !string.IsNullOrWhiteSpace(newName))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Provision {newName}",
                "Description",
                ["provision", "new-product", newName],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["new-product", var productName, var productDescription])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Provision {productName}",
                "Product (e.g. \"Cisco 8841\")",
                ["provision", "new-device-pool", productName, productDescription]));
            return 0;
        }
        if (context.Arguments is
            ["new-device-pool", var dpName, var dpDescription, var dpProduct] &&
            !string.IsNullOrWhiteSpace(dpProduct))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select device pool",
                "new-template",
                new ProvisionWizardState(true, dpName, Normalize(dpDescription), dpProduct),
                "device-pool",
                allowNone: false);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-template", out var templateState))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select phone button template",
                "new-security-profile",
                templateState,
                "phone-template",
                allowNone: true);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-security-profile", out var securityState))
        {
            await RespondWithProvisionNamedSelectorAsync(
                context,
                cucm,
                "Select security profile",
                "new-user",
                securityState,
                "security-profile",
                allowNone: true);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "new-user", out var userSelectionState))
        {
            await RespondWithProvisionUserSelectorAsync(context, cucm, userSelectionState);
            return 0;
        }
        if (context.Arguments is ["existing"])
        {
            var rows = new List<ModuleTableRow>();
            await foreach (var phone in cucm.ListPhonesAsync(cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(phone.Name) || !string.IsNullOrWhiteSpace(phone.OwnerUserName))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    phone.Name,
                    [Clean(phone.Name), Clean(phone.Description), Clean(phone.Model ?? phone.Product), Clean(phone.DevicePoolName)],
                    ["provision", "existing-selected", phone.Name]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Unassigned CUCM phones",
                ["NAME", "DESCRIPTION", "MODEL", "DEVICE POOL"],
                rows));
            return 0;
        }
        if (context.Arguments is ["existing-selected", var existingPhoneName])
        {
            var phone = await RequirePhoneAsync(cucm, existingPhoneName, context.CancellationToken);
            if (!string.IsNullOrWhiteSpace(phone.OwnerUserName))
            {
                throw new InvalidOperationException(
                    $"CUCM phone '{existingPhoneName}' is already owned by '{phone.OwnerUserName}'.");
            }
            var existingState = new ProvisionWizardState(
                false,
                phone.Name ?? existingPhoneName,
                phone.Description,
                phone.Product,
                phone.DevicePoolName,
                phone.PhoneTemplateName,
                phone.SecurityProfileName);
            await RespondWithProvisionUserSelectorAsync(context, cucm, existingState);
            return 0;
        }
        if (TryParseProvisionWizardState(context.Arguments, "review", out var reviewState) &&
            !string.IsNullOrWhiteSpace(reviewState.UserId))
        {
            var reviewUser = await RequireUserAsync(cucm, reviewState.UserId, context.CancellationToken);
            var reviewDid = await RequireAvailableUserDidForUserAsync(context, reviewUser);
            await context.RespondAsync(new ModuleTableResponse(
                "Review phone provisioning",
                ["PHONE", "MODE", "PRODUCT", "DEVICE POOL", "USER", "USER DN"],
                [
                    new ModuleTableRow(
                        "submit",
                        [
                            reviewState.PhoneName,
                            reviewState.IsNewPhone ? "Create new" : "Use existing",
                            Clean(reviewState.Product),
                            Clean(reviewState.DevicePoolName),
                            reviewUser.DisplayName ?? reviewState.UserId,
                            reviewDid.Pattern,
                        ],
                        [
                            "provision", "create", reviewState.IsNewPhone.ToString(), reviewState.PhoneName,
                            reviewState.Description ?? string.Empty, reviewState.Product ?? string.Empty,
                            reviewState.DevicePoolName ?? string.Empty, reviewState.PhoneTemplateName ?? string.Empty,
                            reviewState.SecurityProfileName ?? string.Empty, reviewState.UserId,
                            reviewDid.Pattern, reviewDid.RoutePartitionName ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments.Count == 11 && context.Arguments[0] == "create" &&
            bool.TryParse(context.Arguments[1], out var createIsNew))
        {
            var createState = new ProvisionWizardState(
                createIsNew,
                context.Arguments[2],
                Normalize(context.Arguments[3]),
                Normalize(context.Arguments[4]),
                Normalize(context.Arguments[5]),
                Normalize(context.Arguments[6]),
                Normalize(context.Arguments[7]),
                Normalize(context.Arguments[8]));
            var createPattern = context.Arguments[9];
            var createPartition = context.Arguments[10];
            var createUserId = createState.UserId ??
                throw new InvalidOperationException("Provisioning state is missing a user ID.");

            var user = await RequireUserAsync(cucm, createUserId, context.CancellationToken);
            var did = await RequireAvailableUserDidAsync(context, createPattern, createPartition);

            string? phoneUuid = null;
            if (createState.IsNewPhone)
            {
                phoneUuid = await cucm.AddPhoneAsync(
                    new CucmPhoneCreateRequest(
                        createState.PhoneName,
                        createState.Product ?? string.Empty,
                        createState.DevicePoolName ?? string.Empty,
                        createState.Description,
                        PhoneTemplateName: createState.PhoneTemplateName,
                        SecurityProfileName: createState.SecurityProfileName,
                        OwnerUserName: createUserId),
                    context.CancellationToken);
            }
            else
            {
                await cucm.UpdatePhoneAsync(
                    createState.PhoneName,
                    null,
                    null,
                    createUserId,
                    context.CancellationToken);
            }

            var previousDevices = user.AssociatedDevices.ToArray();
            var addAssociation = !previousDevices.Contains(
                createState.PhoneName,
                StringComparer.OrdinalIgnoreCase);
            if (addAssociation)
            {
                await cucm.UpdateUserAssociatedDevicesAsync(
                    createUserId,
                    previousDevices.Append(createState.PhoneName),
                    context.CancellationToken);
            }

            var effectivePartition = Normalize(createPartition);
            var existingDn = await cucm.GetDirectoryNumberAsync(
                did.Pattern,
                effectivePartition,
                context.CancellationToken);
            EnsureUserDidCanBeAssigned(did, existingDn);
            if (existingDn is null)
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
            try
            {
                await cucm.AssignPhoneLineDirectoryNumberToUserAsync(
                    createState.PhoneName,
                    1,
                    did.Pattern,
                    effectivePartition,
                    createUserId,
                    context.CancellationToken);
            }
            catch (Exception assignmentException)
            {
                if (addAssociation)
                {
                    try
                    {
                        await cucm.UpdateUserAssociatedDevicesAsync(
                            createUserId,
                            previousDevices,
                            context.CancellationToken);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            $"Phone '{createState.PhoneName}' was {(createState.IsNewPhone ? "created" : "updated")} " +
                            $"and owned by '{createUserId}', but line 1 could not be assigned, and the " +
                            "user-device association could not be rolled back.",
                            new AggregateException(assignmentException, rollbackException));
                    }
                }
                throw new InvalidOperationException(
                    $"Phone '{createState.PhoneName}' was {(createState.IsNewPhone ? "created" : "updated")} and " +
                    $"owned by '{createUserId}', but line 1 could not be assigned: {assignmentException.Message} " +
                    $"Use 'phones select {createState.PhoneName}' to finish the line assignment.",
                    assignmentException);
            }
            await new UserDidStore(context.DataDirectory).MarkAssignedAsync(
                did.Pattern,
                did.RoutePartitionName,
                createState.PhoneName,
                1,
                createUserId,
                effectivePartition,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Provisioned phone '{createState.PhoneName}' for user '{createUserId}' with DN " +
                $"'{did.Pattern}'" + (string.IsNullOrWhiteSpace(phoneUuid) ? "." : $" ({phoneUuid})."));
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm provision\n" +
            "       vt cucm provision new\n" +
            "       vt cucm provision existing");
        return 2;
    }
    catch (Exception exception) when (IsExpected(exception))
    {
        context.Error.WriteLine(exception.Message);
        return 1;
    }
}

static async Task RespondWithProvisionNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    ProvisionWizardState state,
    string resourceType,
    bool allowNone)
{
    var rows = new List<ModuleTableRow>();
    if (allowNone)
    {
        rows.Add(new ModuleTableRow(
            $"none:{resourceType}",
            ["<None>", string.Empty],
            ProvisionWizardArguments(nextRoute, state)));
    }
    var resources = resourceType switch
    {
        "device-pool" => cucm.ListDevicePoolsAsync(cancellationToken: context.CancellationToken),
        "phone-template" => cucm.ListPhoneButtonTemplatesAsync(
            cancellationToken: context.CancellationToken),
        "security-profile" => cucm.ListPhoneSecurityProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException(
            $"Unknown CUCM phone resource type '{resourceType}'."),
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
            ProvisionWizardArguments(nextRoute, SetProvisionWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static async Task RespondWithProvisionUserSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    ProvisionWizardState state)
{
    var dids = await new UserDidStore(context.DataDirectory).LoadAsync(context.CancellationToken);
    var rows = new List<ModuleTableRow>();
    await foreach (var user in cucm.ListUsersAsync(cancellationToken: context.CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            continue;
        }
        var extension = UserDidStore.NormalizeUserExtension(user.TelephoneNumber);
        var status = UserDnStatus(user.UserId, extension, dids);
        rows.Add(new ModuleTableRow(
            user.UserId,
            [Clean(user.UserId), Clean(user.DisplayName), Clean(extension), status],
            status == "Available"
                ? ProvisionWizardArguments("review", state with { UserId = user.UserId })
                : null));
    }
    await context.RespondAsync(new ModuleTableResponse(
        $"Select a user for {state.PhoneName}",
        ["USER ID", "DISPLAY NAME", "USER DN", "STATUS"],
        rows));
}

static ProvisionWizardState SetProvisionWizardResource(
    ProvisionWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "device-pool" => state with { DevicePoolName = value },
    "phone-template" => state with { PhoneTemplateName = value },
    "security-profile" => state with { SecurityProfileName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM phone resource type '{resourceType}'."),
};

static IReadOnlyList<string> ProvisionWizardArguments(string route, ProvisionWizardState state) =>
    [
        "provision",
        route,
        state.IsNewPhone.ToString(),
        state.PhoneName,
        state.Description ?? string.Empty,
        state.Product ?? string.Empty,
        state.DevicePoolName ?? string.Empty,
        state.PhoneTemplateName ?? string.Empty,
        state.SecurityProfileName ?? string.Empty,
        state.UserId ?? string.Empty,
    ];

static bool TryParseProvisionWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out ProvisionWizardState state)
{
    state = new ProvisionWizardState(false, string.Empty);
    if (arguments.Count != 9 || arguments[0] != route)
    {
        return false;
    }
    state = new ProvisionWizardState(
        bool.TryParse(arguments[1], out var isNew) && isNew,
        arguments[2],
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]),
        Normalize(arguments[8]));
    return true;
}

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
                    new ModuleTableRow(
                        "edit",
                        ["Edit", "Update description, device pool, and owner"],
                        ["phones", "edit", phoneName]),
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
        if (context.Arguments is ["add"])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Add CUCM phone",
                "Phone name",
                ["phones", "add-description"]));
            return 0;
        }
        if (context.Arguments is ["add-description", var addName] &&
            !string.IsNullOrWhiteSpace(addName))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {addName}",
                "Description",
                ["phones", "add-product", addName],
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is ["add-product", var productPhoneName, var productDescription])
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {productPhoneName}",
                "Product (e.g. \"Cisco 8841\")",
                ["phones", "add-device-pool", productPhoneName, productDescription]));
            return 0;
        }
        if (context.Arguments is
            ["add-device-pool", var devicePoolPhoneName, var devicePoolDescription, var devicePoolProduct] &&
            !string.IsNullOrWhiteSpace(devicePoolProduct))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select device pool",
                "add-template",
                new PhoneWizardState(
                    devicePoolPhoneName,
                    Normalize(devicePoolDescription),
                    devicePoolProduct),
                "device-pool",
                allowNone: false);
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-template", out var templateState))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select phone button template",
                "add-security-profile",
                templateState,
                "phone-template",
                allowNone: true);
            return 0;
        }
        if (TryParsePhoneWizardState(
            context.Arguments,
            "add-security-profile",
            out var securityProfileState))
        {
            await RespondWithPhoneNamedSelectorAsync(
                context,
                cucm,
                "Select security profile",
                "add-owner",
                securityProfileState,
                "security-profile",
                allowNone: true);
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-owner", out var ownerState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Add phone {ownerState.Name}",
                "Owner user ID",
                PhoneWizardArguments("add-review-value", ownerState),
                AllowEmpty: true));
            return 0;
        }
        if (TryParsePhoneWizardStateWithValue(
            context.Arguments,
            "add-review-value",
            out var reviewValueState,
            out var ownerValue))
        {
            await RespondWithPhoneCreateReviewAsync(
                context,
                reviewValueState with { OwnerUserName = Normalize(ownerValue) });
            return 0;
        }
        if (TryParsePhoneWizardState(context.Arguments, "add-create", out var createState))
        {
            var uuid = await cucm.AddPhoneAsync(
                createState.ToCreateRequest(),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Created phone '{createState.Name}'" +
                (string.IsNullOrWhiteSpace(uuid) ? "." : $" ({uuid})."));
            return 0;
        }
        if (context.Arguments is ["edit", var editPhoneName])
        {
            var phone = await RequirePhoneAsync(cucm, editPhoneName, context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit phone {editPhoneName}",
                "Description",
                ["phones", "edit-device-pool", editPhoneName],
                phone.Description,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            ["edit-device-pool", var devicePoolEditPhoneName, var editDescriptionRaw])
        {
            var editDescription = Normalize(editDescriptionRaw);
            var rows = new List<ModuleTableRow>
            {
                new(
                    "keep",
                    ["<Keep current>", string.Empty],
                    [
                        "phones", "edit-owner", devicePoolEditPhoneName,
                        editDescription ?? string.Empty, string.Empty,
                    ]),
            };
            await foreach (var pool in cucm.ListDevicePoolsAsync(
                cancellationToken: context.CancellationToken))
            {
                if (string.IsNullOrWhiteSpace(pool.Name))
                {
                    continue;
                }
                rows.Add(new ModuleTableRow(
                    pool.Uuid ?? $"device-pool:{pool.Name}",
                    [Clean(pool.Name), Clean(pool.Description)],
                    [
                        "phones", "edit-owner", devicePoolEditPhoneName,
                        editDescription ?? string.Empty, pool.Name,
                    ]));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "Select device pool",
                ["NAME", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            ["edit-owner", var ownerEditPhoneName, var ownerEditDescriptionRaw, var ownerEditDevicePoolRaw])
        {
            var phone = await RequirePhoneAsync(
                cucm,
                ownerEditPhoneName,
                context.CancellationToken);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit phone {ownerEditPhoneName}",
                "Owner user ID",
                [
                    "phones", "edit-review", ownerEditPhoneName,
                    ownerEditDescriptionRaw, ownerEditDevicePoolRaw,
                ],
                phone.OwnerUserName,
                AllowEmpty: true));
            return 0;
        }
        if (context.Arguments is
            [
                "edit-review", var reviewPhoneName, var reviewDescriptionRaw, var reviewDevicePoolRaw,
                var reviewOwnerRaw,
            ])
        {
            // Empty values mean "leave unchanged" (UpdatePhoneAsync omits null fields), so
            // reviewing an intentional blank-out isn't distinguishable from "no change" here.
            await context.RespondAsync(new ModuleTableResponse(
                $"Review changes to {reviewPhoneName}",
                ["PHONE", "DESCRIPTION", "DEVICE POOL", "OWNER"],
                [
                    new ModuleTableRow(
                        "submit",
                        [
                            reviewPhoneName,
                            string.IsNullOrWhiteSpace(reviewDescriptionRaw)
                                ? "<Unchanged>"
                                : Clean(reviewDescriptionRaw),
                            string.IsNullOrWhiteSpace(reviewDevicePoolRaw)
                                ? "<Unchanged>"
                                : Clean(reviewDevicePoolRaw),
                            string.IsNullOrWhiteSpace(reviewOwnerRaw)
                                ? "<Unchanged>"
                                : Clean(reviewOwnerRaw),
                        ],
                        [
                            "phones", "edit-update", reviewPhoneName,
                            reviewDescriptionRaw, reviewDevicePoolRaw, reviewOwnerRaw,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is
            [
                "edit-update", var updatePhoneName, var updateDescriptionRaw, var updateDevicePoolRaw,
                var updateOwnerRaw,
            ])
        {
            await cucm.UpdatePhoneAsync(
                updatePhoneName,
                Normalize(updateDescriptionRaw),
                Normalize(updateDevicePoolRaw),
                Normalize(updateOwnerRaw),
                context.CancellationToken);
            context.Output.WriteLine($"Updated phone '{updatePhoneName}'.");
            return 0;
        }

        context.Error.WriteLine(
            "Usage: vt cucm phones list [--max <count>] [--page-size <count>]\n" +
            "       vt cucm phones add\n" +
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
                        : DirectoryNumberPatternArguments("select", rowPattern, rowPartition)));
            }
            await context.RespondAsync(new ModuleTableResponse(
                "CUCM directory numbers",
                ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL PROFILE"],
                rows));
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "select",
            out var selectPattern,
            out var selectPartition))
        {
            var selected = await RequireDirectoryNumberAsync(
                cucm,
                selectPattern,
                selectPartition,
                context.CancellationToken);
            var selectedPartition = selected.RoutePartitionName ?? selectPartition;
            await context.RespondAsync(new ModuleTableResponse(
                $"CUCM directory number: {selected.Pattern}",
                ["ACTION", "DETAIL"],
                [
                    new ModuleTableRow(
                        "info",
                        ["View details", Clean(selected.Description)],
                        DirectoryNumberPatternArguments("info", selectPattern, selectedPartition)),
                    new ModuleTableRow(
                        "edit",
                        [
                            "Edit",
                            "Update description, CSS, voicemail profile, and forwarding",
                        ],
                        DirectoryNumberPatternArguments("edit", selectPattern, selectedPartition)),
                    new ModuleTableRow(
                        "delete",
                        ["Delete", "Remove this directory number from CUCM"],
                        DirectoryNumberPatternArguments("delete", selectPattern, selectedPartition)),
                ]));
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "info",
            out var infoPattern,
            out var infoPartition))
        {
            var line = await RequireDirectoryNumberAsync(
                cucm,
                infoPattern,
                infoPartition,
                context.CancellationToken);
            await RespondWithDirectoryNumberInfoAsync(context, line);
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "edit",
            out var editPattern,
            out var editPartition))
        {
            var current = await RequireDirectoryNumberAsync(
                cucm,
                editPattern,
                editPartition,
                context.CancellationToken);
            var editState = new DnWizardState(
                current.Pattern ?? editPattern,
                current.Description,
                current.RoutePartitionName ?? editPartition,
                current.CallingSearchSpaceName,
                current.VoiceMailProfileName,
                current.CallForwardAll.Destination,
                current.CallForwardAll.CallingSearchSpaceName,
                current.CallForwardAll.ForwardToVoiceMail);
            await context.RespondAsync(new ModuleTextPromptResponse(
                $"Edit directory number {editState.Pattern}",
                "Description",
                WizardArguments("edit-css", editState),
                editState.Description,
                AllowEmpty: true));
            return 0;
        }
        if (TryParseWizardStateWithOptionalValue(context.Arguments, "edit-css", out var editCssBaseState, out var editDescription))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select line calling search space",
                "edit-voicemail-profile",
                editCssBaseState with { Description = Normalize(editDescription) },
                "css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-voicemail-profile", out var editCssState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select voicemail profile",
                "edit-forward",
                editCssState,
                "voicemail-profile");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-forward", out var editVoicemailState))
        {
            await RespondWithForwardingSelectorAsync(
                context,
                editVoicemailState,
                "edit-review",
                "edit-forward-destination",
                "edit-forward-voicemail");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "edit-forward-destination",
            out var editDestinationState))
        {
            await context.RespondAsync(new ModuleTextPromptResponse(
                "Configure forward all",
                "Forward destination",
                WizardArguments("edit-forward-destination-value", editDestinationState)));
            return 0;
        }
        if (TryParseWizardStateWithValue(
            context.Arguments,
            "edit-forward-destination-value",
            out var editDestinationValueState,
            out var editForwardDestination))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "edit-review",
                editDestinationValueState with { ForwardDestination = editForwardDestination },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(
            context.Arguments,
            "edit-forward-voicemail",
            out var editVoicemailForwardState))
        {
            await RespondWithNamedSelectorAsync(
                context,
                cucm,
                "Select forward-all calling search space",
                "edit-review",
                editVoicemailForwardState with { ForwardToVoiceMail = true },
                "forward-css");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-review", out var editReviewState))
        {
            await RespondWithDirectoryNumberReviewAsync(
                context,
                editReviewState,
                "Review directory number changes",
                "edit-update");
            return 0;
        }
        if (TryParseWizardState(context.Arguments, "edit-update", out var editUpdateState))
        {
            await cucm.UpdateDirectoryNumberAsync(
                editUpdateState.ToUpdateRequest(),
                context.CancellationToken);
            context.Output.WriteLine(
                $"Updated directory number '{editUpdateState.Pattern}' in partition " +
                $"'{DisplayPartition(editUpdateState.RoutePartitionName)}'.");
            return 0;
        }
        if (TryParseDirectoryNumberPatternArguments(
            context.Arguments,
            "delete",
            out var deletePattern,
            out var deletePartition))
        {
            var toDelete = await RequireDirectoryNumberAsync(
                cucm,
                deletePattern,
                deletePartition,
                context.CancellationToken);
            var deletePartitionResolved = toDelete.RoutePartitionName ?? deletePartition;
            await context.RespondAsync(new ModuleTableResponse(
                "Confirm delete",
                ["PATTERN", "PARTITION", "DESCRIPTION"],
                [
                    new ModuleTableRow(
                        "confirm",
                        [
                            Clean(toDelete.Pattern),
                            DisplayPartition(deletePartitionResolved),
                            Clean(toDelete.Description),
                        ],
                        [
                            "dn", "delete-confirm", deletePattern,
                            deletePartitionResolved ?? string.Empty,
                        ]),
                ],
                SubmitMode: ModuleTableSubmitMode.Save));
            return 0;
        }
        if (context.Arguments is ["delete-confirm", var confirmPattern, var confirmPartitionRaw])
        {
            var confirmPartition = Normalize(confirmPartitionRaw);
            await cucm.DeleteDirectoryNumberAsync(
                confirmPattern,
                confirmPartition,
                context.CancellationToken);
            context.Output.WriteLine(
                $"Deleted directory number '{confirmPattern}' from partition " +
                $"'{DisplayPartition(confirmPartition)}'.");
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
            await RespondWithForwardingSelectorAsync(
                context,
                forwardingState,
                "add-review",
                "add-forward-destination",
                "add-forward-voicemail");
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
            await RespondWithDirectoryNumberReviewAsync(
                context,
                reviewState,
                "Review directory number",
                "add-create");
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
        ],
        Selectable: false,
        Searchable: false));
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

static Task RespondWithForwardingSelectorAsync(
    ModuleContext context,
    DnWizardState state,
    string disabledRoute,
    string destinationRoute,
    string voicemailRoute) =>
    context.RespondAsync(new ModuleTableResponse(
        "Configure forward all",
        ["OPTION", "DETAIL"],
        [
            new ModuleTableRow(
                "disabled",
                ["Disabled", "Do not forward all calls"],
                WizardArguments(disabledRoute, state)),
            new ModuleTableRow(
                "destination",
                ["Destination", "Forward all calls to a number"],
                WizardArguments(destinationRoute, state)),
            new ModuleTableRow(
                "voicemail",
                ["Voicemail", "Forward all calls to voicemail"],
                WizardArguments(voicemailRoute, state)),
        ],
        SubmitMode: ModuleTableSubmitMode.Save)).AsTask();

static Task RespondWithDirectoryNumberReviewAsync(
    ModuleContext context,
    DnWizardState state,
    string title,
    string submitRoute)
{
    var forwarding = state.ForwardToVoiceMail
        ? "Voicemail"
        : string.IsNullOrWhiteSpace(state.ForwardDestination)
            ? "Disabled"
            : state.ForwardDestination;
    return context.RespondAsync(new ModuleTableResponse(
        title,
        ["PATTERN", "PARTITION", "DESCRIPTION", "CSS", "VOICEMAIL", "FORWARD ALL", "FORWARD CSS"],
        [
            new ModuleTableRow(
                "submit",
                [
                    Clean(state.Pattern),
                    DisplayPartition(state.RoutePartitionName),
                    Clean(state.Description),
                    Clean(state.CallingSearchSpaceName),
                    Clean(state.VoiceMailProfileName),
                    Clean(forwarding),
                    Clean(state.ForwardCallingSearchSpaceName),
                ],
                WizardArguments(submitRoute, state)),
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

static async Task RespondWithPhoneNamedSelectorAsync(
    ModuleContext context,
    CucmService cucm,
    string title,
    string nextRoute,
    PhoneWizardState state,
    string resourceType,
    bool allowNone)
{
    var rows = new List<ModuleTableRow>();
    if (allowNone)
    {
        rows.Add(new ModuleTableRow(
            $"none:{resourceType}",
            ["<None>", string.Empty],
            PhoneWizardArguments(nextRoute, state)));
    }
    var resources = resourceType switch
    {
        "device-pool" => cucm.ListDevicePoolsAsync(cancellationToken: context.CancellationToken),
        "phone-template" => cucm.ListPhoneButtonTemplatesAsync(
            cancellationToken: context.CancellationToken),
        "security-profile" => cucm.ListPhoneSecurityProfilesAsync(
            cancellationToken: context.CancellationToken),
        _ => throw new InvalidOperationException(
            $"Unknown CUCM phone resource type '{resourceType}'."),
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
            PhoneWizardArguments(nextRoute, SetPhoneWizardResource(state, resourceType, resource.Name))));
    }
    await context.RespondAsync(new ModuleTableResponse(
        title,
        ["NAME", "DESCRIPTION"],
        rows,
        SubmitMode: ModuleTableSubmitMode.Save));
}

static PhoneWizardState SetPhoneWizardResource(
    PhoneWizardState state,
    string resourceType,
    string value) => resourceType switch
{
    "device-pool" => state with { DevicePoolName = value },
    "phone-template" => state with { PhoneTemplateName = value },
    "security-profile" => state with { SecurityProfileName = value },
    _ => throw new InvalidOperationException($"Unknown CUCM phone resource type '{resourceType}'."),
};

static Task RespondWithPhoneCreateReviewAsync(ModuleContext context, PhoneWizardState state) =>
    context.RespondAsync(new ModuleTableResponse(
        "Review new phone",
        ["NAME", "DESCRIPTION", "PRODUCT", "DEVICE POOL", "PHONE TEMPLATE", "SECURITY PROFILE", "OWNER"],
        [
            new ModuleTableRow(
                "submit",
                [
                    Clean(state.Name),
                    Clean(state.Description),
                    Clean(state.Product),
                    Clean(state.DevicePoolName),
                    Clean(state.PhoneTemplateName),
                    Clean(state.SecurityProfileName),
                    Clean(state.OwnerUserName),
                ],
                PhoneWizardArguments("add-create", state)),
        ],
        SubmitMode: ModuleTableSubmitMode.Save)).AsTask();

static IReadOnlyList<string> PhoneWizardArguments(string route, PhoneWizardState state) =>
    [
        "phones",
        route,
        state.Name,
        state.Description ?? string.Empty,
        state.Product ?? string.Empty,
        state.DevicePoolName ?? string.Empty,
        state.PhoneTemplateName ?? string.Empty,
        state.SecurityProfileName ?? string.Empty,
        state.OwnerUserName ?? string.Empty,
    ];

static bool TryParsePhoneWizardState(
    IReadOnlyList<string> arguments,
    string route,
    out PhoneWizardState state)
{
    state = new PhoneWizardState(string.Empty);
    if (arguments.Count != 8 || arguments[0] != route)
    {
        return false;
    }
    state = new PhoneWizardState(
        arguments[1],
        Normalize(arguments[2]),
        Normalize(arguments[3]),
        Normalize(arguments[4]),
        Normalize(arguments[5]),
        Normalize(arguments[6]),
        Normalize(arguments[7]));
    return true;
}

static bool TryParsePhoneWizardStateWithValue(
    IReadOnlyList<string> arguments,
    string route,
    out PhoneWizardState state,
    out string value)
{
    state = new PhoneWizardState(string.Empty);
    value = string.Empty;
    if (arguments.Count != 9 || !TryParsePhoneWizardState(arguments.Take(8).ToArray(), route, out state))
    {
        return false;
    }
    value = arguments[8];
    return true;
}

static IReadOnlyList<string> DirectoryNumberPatternArguments(
    string verb,
    string pattern,
    string? partition) =>
    string.IsNullOrWhiteSpace(partition)
        ? ["dn", verb, pattern]
        : ["dn", verb, pattern, "--partition", partition];

static bool TryParseDirectoryNumberPatternArguments(
    IReadOnlyList<string> arguments,
    string verb,
    out string pattern,
    out string? partition)
{
    pattern = string.Empty;
    partition = null;
    if (arguments.Count < 2 || arguments[0] != verb || string.IsNullOrWhiteSpace(arguments[1]))
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

static async Task<CucmDirectoryNumber> RequireDirectoryNumberAsync(
    CucmService cucm,
    string pattern,
    string? partition,
    CancellationToken cancellationToken) =>
    await cucm.GetDirectoryNumberAsync(pattern, partition, cancellationToken) ??
        throw new InvalidOperationException(
            $"CUCM directory number '{pattern}' in partition " +
            $"'{DisplayPartition(partition)}' was not found.");


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

// Same shape as TryParseWizardStateWithValue, but for continuations whose value is allowed to
// be blank (e.g. clearing a description), unlike a required field such as forward destination.
static bool TryParseWizardStateWithOptionalValue(
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
    return true;
}

static void WriteDirectoryNumberUsage(ModuleContext context) =>
    context.Error.WriteLine(
        "Usage: vt cucm dn list [--max <count>] [--page-size <count>]\n" +
        "       vt cucm dn info <pattern> [--partition <name>]\n" +
        "       vt cucm dn edit <pattern> [--partition <name>]\n" +
        "       vt cucm dn delete <pattern> [--partition <name>]\n" +
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

    public CucmDirectoryNumberUpdateRequest ToUpdateRequest()
    {
        var callForwardAll = ForwardToVoiceMail || !string.IsNullOrWhiteSpace(ForwardDestination)
            ? new CucmCallForwardSettings(
                ForwardToVoiceMail,
                ForwardCallingSearchSpaceName,
                Destination: ForwardDestination)
            : null;
        return new CucmDirectoryNumberUpdateRequest(
            Pattern,
            RoutePartitionName,
            Description,
            CallingSearchSpaceName,
            VoiceMailProfileName,
            callForwardAll);
    }
}

sealed record PhoneWizardState(
    string Name,
    string? Description = null,
    string? Product = null,
    string? DevicePoolName = null,
    string? PhoneTemplateName = null,
    string? SecurityProfileName = null,
    string? OwnerUserName = null)
{
    public CucmPhoneCreateRequest ToCreateRequest() =>
        new(
            Name,
            Product ?? string.Empty,
            DevicePoolName ?? string.Empty,
            Description,
            PhoneTemplateName: PhoneTemplateName,
            SecurityProfileName: SecurityProfileName,
            OwnerUserName: OwnerUserName);
}

sealed record ProvisionWizardState(
    bool IsNewPhone,
    string PhoneName,
    string? Description = null,
    string? Product = null,
    string? DevicePoolName = null,
    string? PhoneTemplateName = null,
    string? SecurityProfileName = null,
    string? UserId = null);

