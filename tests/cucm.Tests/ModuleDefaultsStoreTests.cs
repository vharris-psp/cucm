public sealed class ModuleDefaultsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-module-defaults-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadEffectiveAsyncFallsBackToLegacyConfigurationWhenStoreDoesNotExist()
    {
        var store = new ModuleDefaultsStore(_directory);
        var configuration = new Dictionary<string, string>
        {
            ["user-did-partition"] = "AllPhones",
            ["user-did-css"] = "Subscribe",
            ["user-did-scan-partitions"] = "AllPhones,Reserved",
        };

        var defaults = await store.LoadEffectiveAsync(configuration);

        Assert.Equal("AllPhones", defaults.Partition);
        Assert.Equal("Subscribe", defaults.CallingSearchSpace);
        Assert.Equal(["AllPhones", "Reserved"], defaults.ScanPartitions);
        Assert.False(store.Exists);
    }

    [Fact]
    public async Task SaveFieldAsyncSeedsFromLegacyConfigurationOnFirstEditThenPreservesOtherFields()
    {
        var store = new ModuleDefaultsStore(_directory);
        var configuration = new Dictionary<string, string>
        {
            ["user-did-partition"] = "AllPhones",
            ["user-did-css"] = "Subscribe",
            ["user-did-voicemail-profile"] = "Default",
        };

        await store.SaveFieldAsync(ModuleDefaultsField.CallingSearchSpace, "NewCss", configuration);

        var stored = await store.LoadAsync();
        Assert.Equal("AllPhones", stored.Partition);
        Assert.Equal("NewCss", stored.CallingSearchSpace);
        Assert.Equal("Default", stored.VoiceMailProfile);
        Assert.True(store.Exists);
    }

    [Fact]
    public async Task SaveFieldAsyncWithNullValueClearsThatFieldOnly()
    {
        var store = new ModuleDefaultsStore(_directory);
        await store.SaveAsync(new ModuleDefaults(Partition: "AllPhones", CallingSearchSpace: "Subscribe"));

        await store.SaveFieldAsync(ModuleDefaultsField.Partition, null, new Dictionary<string, string>());

        var stored = await store.LoadAsync();
        Assert.Null(stored.Partition);
        Assert.Equal("Subscribe", stored.CallingSearchSpace);
    }

    [Fact]
    public async Task SaveFieldAsyncParsesScanPartitionsAndRingDuration()
    {
        var store = new ModuleDefaultsStore(_directory);

        await store.SaveFieldAsync(
            ModuleDefaultsField.ScanPartitions, "AllPhones, Reserved", new Dictionary<string, string>());
        await store.SaveFieldAsync(
            ModuleDefaultsField.NoAnswerRingDurationSeconds, "20", new Dictionary<string, string>());

        var stored = await store.LoadAsync();
        Assert.Equal(["AllPhones", "Reserved"], stored.ScanPartitions);
        Assert.Equal(20, stored.NoAnswerRingDurationSeconds);
    }

    [Fact]
    public async Task LoadEffectiveAsyncFallsBackToLegacyClassroomLineTemplateSettings()
    {
        var store = new ModuleDefaultsStore(_directory);
        var configuration = new Dictionary<string, string>
        {
            ["classroom-room-line-template"] = "PHS Classroom",
            ["classroom-user-line-template"] = "UserDN",
        };

        var defaults = await store.LoadEffectiveAsync(configuration);

        Assert.Equal("PHS Classroom", defaults.ClassroomRoomLineTemplate);
        Assert.Equal("UserDN", defaults.ClassroomUserLineTemplate);
    }

    [Fact]
    public async Task SaveFieldAsyncEditsClassroomLineTemplateFieldsIndependently()
    {
        var store = new ModuleDefaultsStore(_directory);
        var configuration = new Dictionary<string, string>
        {
            ["classroom-room-line-template"] = "PHS Classroom",
            ["classroom-user-line-template"] = "UserDN",
        };

        await store.SaveFieldAsync(
            ModuleDefaultsField.ClassroomRoomLineTemplate, "HS Classroom", configuration);

        var stored = await store.LoadAsync();
        Assert.Equal("HS Classroom", stored.ClassroomRoomLineTemplate);
        Assert.Equal("UserDN", stored.ClassroomUserLineTemplate);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
