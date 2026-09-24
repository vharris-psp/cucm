public sealed class ReservedExtensionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task AddPersistsNewEntriesAndSkipsDuplicates()
    {
        var store = new ReservedExtensionStore(_directory);

        var firstAdd = await store.AddAsync([("9999", "AllPhones"), ("911", "AllPhones")], "Known extensions.");
        var secondAdd = await store.AddAsync([("9999", "AllPhones")], "Duplicate.");

        Assert.Equal(2, firstAdd);
        Assert.Equal(0, secondAdd);
        var extensions = await store.LoadAsync();
        Assert.Equal(2, extensions.Count);
        Assert.Contains(extensions, extension => extension.Pattern == "9999" && extension.Note == "Known extensions.");
    }

    [Fact]
    public async Task RemoveDeletesAMatchingEntry()
    {
        var store = new ReservedExtensionStore(_directory);
        await store.AddAsync([("9999", "AllPhones")], null);

        var removed = await store.RemoveAsync("9999", "AllPhones");
        var removedAgain = await store.RemoveAsync("9999", "AllPhones");

        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task LoadReturnsEmptyWhenNoStoreFileExists()
    {
        var store = new ReservedExtensionStore(_directory);

        Assert.Empty(await store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
