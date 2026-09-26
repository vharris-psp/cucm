// Typed view of every CUCM module setting, independent of Vt.ModuleSdk (built from a plain
// IReadOnlyDictionary<string,string>, not a ModuleContext). Legacy JSON/scalar parsing is
// delegated to the existing Parse* methods on PhoneConfigurationChecks/UserDidReconciliation,
// which remain available as standalone "compatibility readers" for any direct caller that only
// needs one setting - this aggregate exists so callers that need several settings at once (or a
// future structured-configuration provider) have a single typed entry point instead of each
// re-parsing the same raw strings.
internal sealed record CucmConfiguration(
    string Publisher,
    int Port,
    string AxlVersion,
    string? TrustedCertificatePath,
    IReadOnlyDictionary<string, string> RoomPartitions,
    IReadOnlyDictionary<string, BuildingPattern> BuildingPatterns,
    IReadOnlyDictionary<string, TemplateCompliancePolicy> TemplateCompliancePolicies,
    IReadOnlyDictionary<string, LineTemplate> LineTemplates,
    string? ClassroomRoomLineTemplate,
    string? ClassroomUserLineTemplate,
    PhoneCheckPlaceholder PhoneCheckPlaceholder,
    string? UserDidPartition,
    string? UserDidCallingSearchSpace,
    string? UserDidVoiceMailProfile,
    string? UserDidDescriptionPrefix,
    IReadOnlyList<string> UserDidScanPartitions,
    string? UserDidForwardCallingSearchSpace,
    string? UserDidCallingSearchSpaceActivationPolicy,
    int? UserDidNoAnswerRingDurationSeconds,
    IReadOnlyList<string> CssActivationPolicyChoices)
{
    internal static CucmConfiguration FromModuleConfiguration(IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new CucmConfiguration(
            Value(configuration, "publisher") ?? string.Empty,
            ParsePort(Value(configuration, "port")),
            Value(configuration, "axl-version") ?? string.Empty,
            Value(configuration, "trusted-certificate"),
            PhoneConfigurationChecks.ParseRoomPartitions(Value(configuration, "room-partitions") ?? "{}"),
            PhoneConfigurationChecks.ParseBuildingPatterns(Value(configuration, "building-patterns") ?? "{}"),
            PhoneConfigurationChecks.ParseTemplateCompliancePolicies(
                Value(configuration, "template-compliance-policies") ?? "{}"),
            PhoneConfigurationChecks.ParseLineTemplates(Value(configuration, "line-templates") ?? "{}"),
            Value(configuration, "classroom-room-line-template"),
            Value(configuration, "classroom-user-line-template"),
            PhoneConfigurationChecks.ParsePhoneCheckPlaceholder(
                Value(configuration, "phone-check-placeholder") ?? string.Empty),
            Value(configuration, "user-did-partition"),
            Value(configuration, "user-did-css"),
            Value(configuration, "user-did-voicemail-profile"),
            Value(configuration, "user-did-description-prefix"),
            UserDidReconciler.ParsePartitions(Value(configuration, "user-did-scan-partitions")),
            Value(configuration, "user-did-forward-css"),
            Value(configuration, "user-did-css-activation-policy"),
            ParseNullableInt(Value(configuration, "user-did-no-answer-ring-duration")),
            ParseCommaSeparatedList(Value(configuration, "css-activation-policy-choices")));
    }

    private static string? Value(IReadOnlyDictionary<string, string> configuration, string key) =>
        configuration.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int ParsePort(string? value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException("CUCM setting 'port' must be between 1 and 65535.");

    private static int? ParseNullableInt(string? value) =>
        value is not null && int.TryParse(value, out var parsed) ? parsed : null;

    private static IReadOnlyList<string> ParseCommaSeparatedList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
