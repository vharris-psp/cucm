public sealed class UserDidStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task MarkAssignedAllowsExactRetryAndRejectsConflictingAssignment()
    {
        var store = new UserDidStore(_directory);
        await store.AddAsync(["1234"], "Users-PT", null, null, null);

        await store.MarkAssignedAsync("1234", "Users-PT", "SEP001", 1, "alice", "Users-PT");
        await store.MarkAssignedAsync("1234", "Users-PT", "sep001", 1, "ALICE", "Users-PT");

        var did = Assert.Single(await store.LoadAsync());
        Assert.Equal("SEP001", did.Assignment?.PhoneName, ignoreCase: true);
        Assert.Equal("alice", did.Assignment?.UserId, ignoreCase: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.MarkAssignedAsync("1234", "Users-PT", "SEP002", 1, "alice", "Users-PT"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}