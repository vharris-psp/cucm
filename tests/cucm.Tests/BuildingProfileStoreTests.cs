public sealed class BuildingProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-building-profiles-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAsyncPersistsNormalizedProfile()
    {
        var store = new BuildingProfileStore(_directory);

        await store.SaveAsync(
            " CE ",
            new BuildingPattern(
                " CE-Rooms ",
                [" CentralElem ", "CentralElem_SRST", "centralElem"],
                " Standard 7841 SIP 1DN-1SdBLF-2DN ",
                " CentralElem_SRST "));

        var profiles = await store.LoadAsync();
        var profile = Assert.Single(profiles).Value;
        Assert.Equal("CE-Rooms", profile.RoutePartitionName);
        Assert.Equal("CentralElem_SRST", profile.DevicePoolName);
        Assert.Equal(["CentralElem", "CentralElem_SRST"], profile.DevicePoolNames);
        Assert.Equal("Standard 7841 SIP 1DN-1SdBLF-2DN", profile.PhoneTemplateName);
        Assert.True(store.Exists);
    }

    [Fact]
    public async Task ReplaceAsyncRejectsPoolAssignedToMultipleBuildings()
    {
        var store = new BuildingProfileStore(_directory);
        var profiles = new Dictionary<string, BuildingPattern>(StringComparer.OrdinalIgnoreCase)
        {
            ["CE"] = new("CE-Rooms", ["SharedPool"], DevicePoolName: "SharedPool"),
            ["LE"] = new("LE-Rooms", ["SharedPool"], DevicePoolName: "SharedPool"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReplaceAsync(profiles));

        Assert.Contains("already assigned", exception.Message);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task LoadEffectiveAsyncUsesLegacyUntilLocalStoreIsCreated()
    {
        var store = new BuildingProfileStore(_directory);
        const string legacy =
            "{\"PHS\":{\"routePartitionName\":\"HS-Rooms\",\"devicePoolName\":\"HighSchool\"," +
            "\"devicePools\":[\"HighSchool\"]}}";

        var beforeImport = await store.LoadEffectiveAsync(legacy);
        Assert.Equal("HS-Rooms", Assert.Single(beforeImport).Value.RoutePartitionName);

        await store.ReplaceAsync(new Dictionary<string, BuildingPattern>
        {
            ["CE"] = new("CE-Rooms", ["CentralElem_SRST"], DevicePoolName: "CentralElem_SRST"),
        });

        var afterImport = await store.LoadEffectiveAsync(legacy);
        Assert.False(afterImport.ContainsKey("PHS"));
        Assert.Equal("CE-Rooms", Assert.Single(afterImport).Value.RoutePartitionName);
    }

    [Fact]
    public async Task DeleteAsyncKeepsEmptyLocalStoreAuthoritative()
    {
        var store = new BuildingProfileStore(_directory);
        await store.SaveAsync(
            "CE",
            new BuildingPattern("CE-Rooms", ["CentralElem_SRST"], DevicePoolName: "CentralElem_SRST"));

        Assert.True(await store.DeleteAsync("CE"));

        Assert.True(store.Exists);
        Assert.Empty(await store.LoadEffectiveAsync(
            "{\"PHS\":{\"routePartitionName\":\"HS-Rooms\"}}"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}