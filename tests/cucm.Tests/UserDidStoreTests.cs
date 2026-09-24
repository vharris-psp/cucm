public sealed class UserDidStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SelectorUsesUsersAssignedDnBeforeUnusedInventory()
    {
        var selection = UserDidSelector.Select(
            [
                new UserDid("1000", "Users-PT", null, null, null),
                new UserDid("1205", "Users-PT", "Victor Harris", null, "Default"),
            ],
            null,
            "vharris",
            primaryExtensionPattern: "1205",
            primaryExtensionRoutePartitionName: "AllPhones");

        Assert.Equal("1205", selection.Did.Pattern);
        Assert.Equal("AllPhones", selection.Did.RoutePartitionName);
        Assert.False(selection.UsesInventoryFallback);
    }

    [Fact]
    public void SelectorPrefersPrimaryExtensionOverLdapTelephoneNumber()
    {
        var selection = UserDidSelector.Select(
            [],
            "2112",
            "vharris",
            primaryExtensionPattern: "1205",
            primaryExtensionRoutePartitionName: "AllPhones");

        Assert.Equal("1205", selection.Did.Pattern);
        Assert.Equal("AllPhones", selection.Did.RoutePartitionName);
        Assert.False(selection.UsesInventoryFallback);
    }

    [Fact]
    public void SelectorUsesUnusedInventoryOnlyWhenUserHasNoAssignedDn()
    {
        var selection = UserDidSelector.Select(
            [
                new UserDid(
                    "1000",
                    "Users-PT",
                    null,
                    null,
                    null,
                    new UserDidAssignment("SEPOTHER", 1, "other", "Users-PT", DateTimeOffset.UnixEpoch)),
                new UserDid("1205", "Users-PT", null, null, "Default"),
            ],
            null,
            "vharris");

        Assert.Equal("1205", selection.Did.Pattern);
        Assert.True(selection.UsesInventoryFallback);
    }

    [Fact]
    public void SelectorCanRequireAnAssignedUserDnWithoutAllocatingInventory()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => UserDidSelector.Select(
            [new UserDid("1205", "Users-PT", null, null, "Default")],
            null,
            "vharris",
            allowInventoryFallback: false));

        Assert.Contains("primary extension or LDAP telephone number", exception.Message);
    }

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

    [Fact]
    public async Task ClearAssignmentByPatternFreesAStaleAssignmentRegardlessOfCurrentPhoneState()
    {
        var store = new UserDidStore(_directory);
        await store.AddAsync(["1234"], "Users-PT", null, null, null);
        await store.MarkAssignedAsync("1234", "Users-PT", "SEP001", 1, "alice", "Users-PT");

        var cleared = await store.ClearAssignmentByPatternAsync("1234", "Users-PT");

        Assert.NotNull(cleared);
        Assert.Equal("SEP001", cleared!.PhoneName, ignoreCase: true);
        var did = Assert.Single(await store.LoadAsync());
        Assert.Null(did.Assignment);
    }

    [Fact]
    public async Task ClearAssignmentByPatternReturnsNullWhenAlreadyUnassigned()
    {
        var store = new UserDidStore(_directory);
        await store.AddAsync(["1234"], "Users-PT", null, null, null);

        var cleared = await store.ClearAssignmentByPatternAsync("1234", "Users-PT");

        Assert.Null(cleared);
    }

    [Fact]
    public async Task ClearAssignmentByPatternThrowsWhenPatternIsNotTracked()
    {
        var store = new UserDidStore(_directory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ClearAssignmentByPatternAsync("9999", "Users-PT"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}