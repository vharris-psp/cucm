public sealed class CucmConfigurationTests
{
    private static readonly Dictionary<string, string> BaseValues = new()
    {
        ["publisher"] = "cucmpub.example.org",
        ["port"] = "8443",
        ["axl-version"] = "14.0",
    };

    [Fact]
    public void FromModuleConfigurationBindsEveryLegacyJsonSettingToTheTypedModel()
    {
        var values = new Dictionary<string, string>(BaseValues)
        {
            ["room-partitions"] = """{"HS": "HS-Rooms"}""",
            ["building-patterns"] =
                """{"PHS":{"routePartitionName":"HS-Rooms","devicePoolName":"HighSchool"}}""",
            ["template-compliance-policies"] =
                """{"Standard 7841": {"slots": [{"index": 1, "kind": "user"}]}}""",
            ["line-templates"] = """{"UserDN": {"kind": "user", "associateEndUser": true}}""",
            ["phone-check-placeholder"] =
                """{"userDn":"1000","roomNumber":"3000","location":"default"}""",
        };

        var configuration = CucmConfiguration.FromModuleConfiguration(values);

        Assert.Equal("HS-Rooms", configuration.RoomPartitions["HS"]);
        Assert.Equal("HighSchool", configuration.BuildingPatterns["PHS"].DevicePoolName);
        Assert.Equal(
            TemplateComplianceSlotKind.User,
            Assert.Single(configuration.TemplateCompliancePolicies["Standard 7841"].Slots).Kind);
        Assert.Equal(LineTemplateKind.User, configuration.LineTemplates["UserDN"].Kind);
        Assert.Equal("1000", configuration.PhoneCheckPlaceholder.UserDn);
        Assert.Equal("3000", configuration.PhoneCheckPlaceholder.RoomNumber);
        Assert.Equal("default", configuration.PhoneCheckPlaceholder.Location);
    }

    [Fact]
    public void FromModuleConfigurationParsesScalarTypesAndAppliesEmptyDefaultsForOmittedJson()
    {
        var configuration = CucmConfiguration.FromModuleConfiguration(new Dictionary<string, string>(BaseValues)
        {
            ["user-did-no-answer-ring-duration"] = "20",
        });

        Assert.Equal("cucmpub.example.org", configuration.Publisher);
        Assert.Equal(8443, configuration.Port);
        Assert.Equal("14.0", configuration.AxlVersion);
        Assert.Equal(20, configuration.UserDidNoAnswerRingDurationSeconds);
        Assert.Empty(configuration.RoomPartitions);
        Assert.Empty(configuration.BuildingPatterns);
        Assert.Empty(configuration.TemplateCompliancePolicies);
        Assert.Empty(configuration.LineTemplates);
        Assert.Null(configuration.PhoneCheckPlaceholder.UserDn);
    }

    [Fact]
    public void FromModuleConfigurationAcceptsLegacyCommaSeparatedScanPartitions()
    {
        var configuration = CucmConfiguration.FromModuleConfiguration(new Dictionary<string, string>(BaseValues)
        {
            ["user-did-scan-partitions"] = "AllPhones, Reserved ;AllPhones",
        });

        Assert.Equal(["AllPhones", "Reserved"], configuration.UserDidScanPartitions);
    }

    [Fact]
    public void FromModuleConfigurationParsesCssActivationPolicyChoicesAndDefaultsToEmpty()
    {
        var withoutChoices = CucmConfiguration.FromModuleConfiguration(new Dictionary<string, string>(BaseValues));
        Assert.Empty(withoutChoices.CssActivationPolicyChoices);

        var withChoices = CucmConfiguration.FromModuleConfiguration(new Dictionary<string, string>(BaseValues)
        {
            ["css-activation-policy-choices"] = "Use System Default, Activate With Line,Use System Default",
        });
        Assert.Equal(["Use System Default", "Activate With Line"], withChoices.CssActivationPolicyChoices);
    }

    [Fact]
    public void FromModuleConfigurationLeavesUnsetOptionalScalarsNull()
    {
        var configuration = CucmConfiguration.FromModuleConfiguration(new Dictionary<string, string>(BaseValues));

        Assert.Null(configuration.TrustedCertificatePath);
        Assert.Null(configuration.UserDidPartition);
        Assert.Null(configuration.UserDidCallingSearchSpace);
        Assert.Null(configuration.UserDidVoiceMailProfile);
        Assert.Null(configuration.UserDidForwardCallingSearchSpace);
        Assert.Null(configuration.UserDidCallingSearchSpaceActivationPolicy);
        Assert.Null(configuration.UserDidNoAnswerRingDurationSeconds);
        Assert.Empty(configuration.UserDidScanPartitions);
    }

    [Fact]
    public void FromModuleConfigurationRejectsOutOfRangePort()
    {
        var values = new Dictionary<string, string>(BaseValues) { ["port"] = "70000" };

        var exception = Assert.Throws<InvalidOperationException>(
            () => CucmConfiguration.FromModuleConfiguration(values));
        Assert.Contains("port", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
