using VSharp.Cucm;
using Vt.ModuleSdk;

public sealed class BuildingConfigurationCommandTests : IDisposable
{
    private const string LegacyConfiguration =
        "{\"PHS\":{\"routePartitionName\":\"HS-Rooms\",\"devicePoolName\":\"HighSchool\"," +
        "\"devicePools\":[\"HighSchool\",\"HighSchool_SRST\"]," +
        "\"phoneTemplateName\":\"Standard 7841 SIP 1DN-1SdBLF-2DN\"}}";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-building-command-{Guid.NewGuid():N}");

    [Fact]
    public async Task BuildingsListsLegacyProfilesAndImportActionWithoutWriting()
    {
        using var cucm = CreateCucm();
        var outcome = await BuildingConfigurationCommand.ExecuteAsync(
            CreateContext(["buildings"]),
            cucm);

        var result = Assert.IsType<ModuleCommandResult>(outcome);
        var response = Assert.IsType<ModuleTableResponse>(result.Response);
        var profile = Assert.Single(response.Rows, row => row.Id == "PHS");
        Assert.Equal("Legacy module setting", profile.Cells[5]);
        Assert.Equal(["configure", "buildings", "select", "PHS"], profile.Arguments);
        Assert.Contains(response.Rows, row => row.Id == "add");
        Assert.Contains(response.Rows, row => row.Id == "import");
        Assert.False(new BuildingProfileStore(_directory).Exists);
    }

    [Fact]
    public async Task ImportRequiresReviewThenCreatesAuthoritativeLocalStore()
    {
        using var cucm = CreateCucm();
        var reviewOutcome = await BuildingConfigurationCommand.ExecuteAsync(
            CreateContext(["buildings", "import-review"]),
            cucm);

        var review = Assert.IsType<ModuleTableResponse>(
            Assert.IsType<ModuleCommandResult>(reviewOutcome).Response);
        Assert.Equal(ModuleTableSubmitMode.Save, review.SubmitMode);
        Assert.Equal(
            ["configure", "buildings", "import-save"],
            Assert.Single(review.Rows, row => row.Id == "save").Arguments);
        Assert.False(new BuildingProfileStore(_directory).Exists);

        var saveOutcome = await BuildingConfigurationCommand.ExecuteAsync(
            CreateContext(["buildings", "import-save"]),
            cucm);

        var save = Assert.IsType<ModuleCommandResult>(saveOutcome);
        Assert.Equal(0, save.ExitCode);
        var stored = await new BuildingProfileStore(_directory).LoadAsync();
        Assert.Equal("HS-Rooms", Assert.Single(stored).Value.RoutePartitionName);
    }

    [Theory]
    [InlineData("edit-mask", "Loading external call mask for 'PHS'")]
    [InlineData("edit-allcall", "Loading All Call number for 'PHS'")]
    public void LoadingStatusDescribesOptionalBuildingValue(string route, string expected)
    {
        var arguments = route == "edit-mask"
            ? new[] { "buildings", route, "PHS", "HS-Rooms", "HighSchool", "HighSchool", "Template" }
            : new[] { "buildings", route, "PHS", "HS-Rooms", "HighSchool", "HighSchool", "Template", "231347XXXX" };

        Assert.Equal(expected, BuildingConfigurationCommand.LoadingStatus(arguments));
    }

    [Theory]
    [InlineData("edit-partition", "Loading CUCM route partitions")]
    [InlineData("edit-css", "Loading CUCM calling search spaces")]
    [InlineData("edit-voicemail", "Loading CUCM voicemail profiles")]
    [InlineData("edit-forward-css", "Loading CUCM forward calling search spaces")]
    [InlineData("edit-classroom-room-line-template", "Loading room line templates")]
    [InlineData("edit-classroom-user-line-template", "Loading user line templates")]
    public void DefaultsLoadingStatusDescribesSelectedResource(string route, string expected)
    {
        Assert.Equal(expected, ModuleDefaultsConfigurationCommand.LoadingStatus([route]));
    }

    private ModuleContext CreateContext(IReadOnlyList<string> arguments) =>
        new(
            arguments,
            TextWriter.Null,
            TextWriter.Null,
            CancellationToken.None,
            new Dictionary<string, string>
            {
                ["building-patterns"] = LegacyConfiguration,
            },
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            _directory);

    private static CucmService CreateCucm() =>
        new(new CucmServiceConfig(
            "https://cucm.invalid/axl/",
            "14.0",
            "test-user",
            "test-password"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}