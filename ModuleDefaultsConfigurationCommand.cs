using VSharp.Cucm;
using Vt.ModuleSdk;

// "vt cucm configure defaults" - manages the module-owned ModuleDefaults store with live AXL
// selectors, superseding the raw 'user-did-*' settings edited via 'vt module configure' (which
// requires typing exact CUCM object names from memory and has no live validation).
internal static class ModuleDefaultsConfigurationCommand
{
    internal static async ValueTask<ModuleCommandOutcome> ExecuteAsync(
        ModuleContext context,
        CucmService cucm)
    {
        var store = new ModuleDefaultsStore(context.DataDirectory);
        var resourceQueries = new CucmResourceQueryService(cucm);

        if (context.Arguments.Count == 0)
        {
            var defaults = await store.LoadEffectiveAsync(context.Configuration, context.CancellationToken);
            var source = store.Exists
                ? "Local defaults store"
                : "Deprecated 'user-did-*' settings (edit here to migrate)";
            return ModuleCommandResult.Render(new ModuleTableResponse(
                "User DID defaults",
                ["FIELD", "VALUE", "SOURCE"],
                [
                    new ModuleTableRow(
                        "partition",
                        ["Route partition", Display(defaults.Partition), source],
                        ["configure", "defaults", "edit-partition"]),
                    new ModuleTableRow(
                        "css",
                        ["Calling search space", Display(defaults.CallingSearchSpace), source],
                        ["configure", "defaults", "edit-css"]),
                    new ModuleTableRow(
                        "voicemail",
                        ["Voicemail profile", Display(defaults.VoiceMailProfile), source],
                        ["configure", "defaults", "edit-voicemail"]),
                    new ModuleTableRow(
                        "forward-css",
                        ["Forward-CSS (classroom)", Display(defaults.ForwardCallingSearchSpace), source],
                        ["configure", "defaults", "edit-forward-css"]),
                    new ModuleTableRow(
                        "activation-policy",
                        [
                            "CSS activation policy (classroom)",
                            Display(defaults.CallingSearchSpaceActivationPolicy),
                            source,
                        ],
                        ["configure", "defaults", "edit-activation-policy"]),
                    new ModuleTableRow(
                        "ring-duration",
                        [
                            "No-answer ring duration (classroom)",
                            defaults.NoAnswerRingDurationSeconds is { } duration ? $"{duration}s" : "<not configured>",
                            source,
                        ],
                        ["configure", "defaults", "edit-ring-duration"]),
                    new ModuleTableRow(
                        "description-prefix",
                        ["Description prefix", Display(defaults.DescriptionPrefix), source],
                        ["configure", "defaults", "edit-description-prefix"]),
                    new ModuleTableRow(
                        "scan-partitions",
                        [
                            "Scan partitions",
                            defaults.ScanPartitions.Count == 0
                                ? "<not configured>"
                                : string.Join(", ", defaults.ScanPartitions),
                            source,
                        ],
                        ["configure", "defaults", "edit-scan-partitions"]),
                    new ModuleTableRow(
                        "classroom-room-line-template",
                        ["Classroom room line template", Display(defaults.ClassroomRoomLineTemplate), source],
                        ["configure", "defaults", "edit-classroom-room-line-template"]),
                    new ModuleTableRow(
                        "classroom-user-line-template",
                        ["Classroom user line template", Display(defaults.ClassroomUserLineTemplate), source],
                        ["configure", "defaults", "edit-classroom-user-line-template"]),
                ]));
        }

        if (context.Arguments is ["edit-partition"])
        {
            return ModuleCommandResult.Render(await CreateResourceSelectorAsync(
                resourceQueries,
                context.CancellationToken,
                "Select default route partition",
                ModuleDefaultsField.Partition,
                (resources, ct) => resources.ListRoutePartitionsAsync(ct)));
        }
        if (context.Arguments is ["edit-css"])
        {
            return ModuleCommandResult.Render(await CreateResourceSelectorAsync(
                resourceQueries,
                context.CancellationToken,
                "Select default calling search space",
                ModuleDefaultsField.CallingSearchSpace,
                (resources, ct) => resources.ListCallingSearchSpacesAsync(ct)));
        }
        if (context.Arguments is ["edit-voicemail"])
        {
            return ModuleCommandResult.Render(await CreateResourceSelectorAsync(
                resourceQueries,
                context.CancellationToken,
                "Select default voicemail profile",
                ModuleDefaultsField.VoiceMailProfile,
                (resources, ct) => resources.ListVoiceMailProfilesAsync(ct)));
        }
        if (context.Arguments is ["edit-forward-css"])
        {
            return ModuleCommandResult.Render(await CreateResourceSelectorAsync(
                resourceQueries,
                context.CancellationToken,
                "Select classroom forward-all calling search space",
                ModuleDefaultsField.ForwardCallingSearchSpace,
                (resources, ct) => resources.ListCallingSearchSpacesAsync(ct)));
        }
        if (context.Arguments is ["edit-activation-policy"])
        {
            var choices = CucmConfiguration.FromModuleConfiguration(context.Configuration).CssActivationPolicyChoices;
            if (choices.Count == 0)
            {
                return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                    "Classroom CSS activation policy",
                    "Must match the literal text of an option in CUCM's 'Calling Search Space " +
                        "Activation Policy' dropdown (Call Routing -> Directory Number) - not " +
                        "verified via AXL. Configure 'css-activation-policy-choices' for a picker instead.",
                    ["configure", "defaults", "save", "activation-policy"],
                    AllowEmpty: true));
            }
            var rows = new List<ModuleTableRow>
            {
                new("none", ["<None>", string.Empty], ["configure", "defaults", "save", "activation-policy"]),
            };
            rows.AddRange(choices.Select(choice => new ModuleTableRow(
                choice,
                [choice, string.Empty],
                ["configure", "defaults", "save", "activation-policy", choice])));
            return ModuleCommandResult.Render(new ModuleTableResponse(
                "Select classroom CSS activation policy",
                ["VALUE", "DESCRIPTION"],
                rows,
                SubmitMode: ModuleTableSubmitMode.Save));
        }
        if (context.Arguments is ["edit-ring-duration"])
        {
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                "Classroom no-answer ring duration",
                "Ring duration in seconds (blank to leave unmanaged)",
                ["configure", "defaults", "save", "ring-duration"],
                AllowEmpty: true));
        }
        if (context.Arguments is ["edit-description-prefix"])
        {
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                "Description prefix",
                "Prefix used when creating user DIDs",
                ["configure", "defaults", "save", "description-prefix"],
                AllowEmpty: true));
        }
        if (context.Arguments is ["edit-scan-partitions"])
        {
            return ModuleCommandResult.Render(new ModuleTextPromptResponse(
                "Scan partitions",
                "Comma-separated CUCM route partition names to scan when reconciling the local " +
                    "DID inventory (blank to fall back to the local inventory's recorded assignments only)",
                ["configure", "defaults", "save", "scan-partitions"],
                AllowEmpty: true));
        }
        if (context.Arguments is ["edit-classroom-room-line-template"])
        {
            return ModuleCommandResult.Render(await CreateLineTemplateSelectorAsync(
                context, "Select classroom room line template", "classroom-room-line-template", LineTemplateKind.Room));
        }
        if (context.Arguments is ["edit-classroom-user-line-template"])
        {
            return ModuleCommandResult.Render(await CreateLineTemplateSelectorAsync(
                context, "Select classroom user line template", "classroom-user-line-template", LineTemplateKind.User));
        }

        if (context.Arguments.Count is 2 or 3 && context.Arguments[0] == "save")
        {
            var fieldName = context.Arguments[1];
            var field = ParseField(fieldName);
            var value = context.Arguments.Count == 3 ? context.Arguments[2] : null;
            await store.SaveFieldAsync(field, value, context.Configuration, context.CancellationToken);
            return ModuleCommandResult.Ok($"Saved default '{fieldName}'.");
        }

        return ModuleCommandResult.Fail("Usage: vt cucm configure defaults", exitCode: 2);
    }

    private static async Task<ModuleTableResponse> CreateResourceSelectorAsync(
        CucmResourceQueryService resourceQueries,
        CancellationToken cancellationToken,
        string title,
        ModuleDefaultsField field,
        Func<CucmResourceQueryService, CancellationToken, Task<IReadOnlyList<VSharp.Cucm.Models.CucmNamedResource>>> listAsync)
    {
        var fieldRoute = FieldRoute(field);
        var rows = new List<ModuleTableRow>
        {
            new("none", ["<None>", string.Empty], ["configure", "defaults", "save", fieldRoute]),
        };
        foreach (var resource in await listAsync(resourceQueries, cancellationToken))
        {
            rows.Add(new ModuleTableRow(
                resource.Uuid ?? $"{fieldRoute}:{resource.Name}",
                [resource.Name, resource.Description ?? string.Empty],
                ["configure", "defaults", "save", fieldRoute, resource.Name]));
        }
        return new ModuleTableResponse(title, ["NAME", "DESCRIPTION"], rows, SubmitMode: ModuleTableSubmitMode.Save);
    }

    // Local line templates (not AXL objects) merged the same way Program.cs's classroom workflow
    // does: 'vt cucm templates'-managed store plus the 'line-templates' setting (setting wins on
    // a name clash, since it's operator-controlled outside the running module).
    private static async Task<ModuleTableResponse> CreateLineTemplateSelectorAsync(
        ModuleContext context,
        string title,
        string fieldRoute,
        LineTemplateKind kind)
    {
        var configTemplates = PhoneConfigurationChecks.ParseLineTemplates(
            context.Configuration.GetValueOrDefault("line-templates") ?? "{}");
        var storedTemplates = await new LineTemplateStore(context.DataDirectory).LoadAsync(context.CancellationToken);
        var merged = new Dictionary<string, LineTemplate>(storedTemplates, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, template) in configTemplates)
        {
            merged[name] = template;
        }
        var rows = merged
            .Where(pair => pair.Value.Kind == kind)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ModuleTableRow(
                pair.Key,
                [pair.Key, string.Empty],
                ["configure", "defaults", "save", fieldRoute, pair.Key]))
            .ToList();
        return new ModuleTableResponse(title, ["NAME", "DESCRIPTION"], rows, SubmitMode: ModuleTableSubmitMode.Save);
    }

    private static string FieldRoute(ModuleDefaultsField field) => field switch
    {
        ModuleDefaultsField.Partition => "partition",
        ModuleDefaultsField.CallingSearchSpace => "css",
        ModuleDefaultsField.VoiceMailProfile => "voicemail",
        ModuleDefaultsField.ForwardCallingSearchSpace => "forward-css",
        ModuleDefaultsField.CallingSearchSpaceActivationPolicy => "activation-policy",
        ModuleDefaultsField.NoAnswerRingDurationSeconds => "ring-duration",
        ModuleDefaultsField.DescriptionPrefix => "description-prefix",
        ModuleDefaultsField.ScanPartitions => "scan-partitions",
        ModuleDefaultsField.ClassroomRoomLineTemplate => "classroom-room-line-template",
        ModuleDefaultsField.ClassroomUserLineTemplate => "classroom-user-line-template",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static ModuleDefaultsField ParseField(string fieldName) => fieldName switch
    {
        "partition" => ModuleDefaultsField.Partition,
        "css" => ModuleDefaultsField.CallingSearchSpace,
        "voicemail" => ModuleDefaultsField.VoiceMailProfile,
        "forward-css" => ModuleDefaultsField.ForwardCallingSearchSpace,
        "activation-policy" => ModuleDefaultsField.CallingSearchSpaceActivationPolicy,
        "ring-duration" => ModuleDefaultsField.NoAnswerRingDurationSeconds,
        "description-prefix" => ModuleDefaultsField.DescriptionPrefix,
        "scan-partitions" => ModuleDefaultsField.ScanPartitions,
        "classroom-room-line-template" => ModuleDefaultsField.ClassroomRoomLineTemplate,
        "classroom-user-line-template" => ModuleDefaultsField.ClassroomUserLineTemplate,
        _ => throw new InvalidOperationException($"Unknown user DID default field '{fieldName}'."),
    };

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<not configured>" : value;
}
