public sealed class TrustedCertificateLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"cucm-certificate-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingConfigurationUsesSystemTrust(string? configuredPath)
    {
        var certificate = await TrustedCertificateLoader.LoadAsync(
            configuredPath,
            homeDirectory: null,
            CancellationToken.None);

        Assert.Null(certificate);
    }

    [Fact]
    public void AbsolutePathIsPreserved()
    {
        const string path = "/etc/cucm/trusted-ca.pem";

        Assert.Equal(path, TrustedCertificateLoader.ResolvePath(path, homeDirectory: null));
    }

    [Fact]
    public void LeadingStandaloneTildeUsesRuntimeUserHomeDirectory()
    {
        var resolvedPath = TrustedCertificateLoader.ResolvePath(
            "~/.vt/modules/cucm/data/trusted-ca.pem",
            _directory);

        Assert.Equal(
            Path.Combine(_directory, ".vt", "modules", "cucm", "data", "trusted-ca.pem"),
            resolvedPath);
    }

    [Fact]
    public async Task CertificateIsLoadedThroughExpandedTildePath()
    {
        var certificateDirectory = Path.Combine(_directory, ".vt", "modules", "cucm", "data");
        Directory.CreateDirectory(certificateDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(certificateDirectory, "trusted-ca.pem"),
            "test certificate");

        var certificate = await TrustedCertificateLoader.LoadAsync(
            "~/.vt/modules/cucm/data/trusted-ca.pem",
            _directory,
            CancellationToken.None);

        Assert.Equal("test certificate", certificate);
    }

    [Theory]
    [InlineData("~other/trusted-ca.pem")]
    [InlineData("~other")]
    public void NamedUserTildeIsRejected(string configuredPath)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TrustedCertificateLoader.ResolvePath(configuredPath, _directory));

        Assert.Contains("~user", exception.Message);
        Assert.Contains("not supported", exception.Message);
    }

    [Fact]
    public void TildeFailsWhenRuntimeUserHomeDirectoryIsUnavailable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TrustedCertificateLoader.ResolvePath("~/trusted-ca.pem", homeDirectory: null));

        Assert.Contains("runtime user's home directory is unavailable", exception.Message);
    }

    [Theory]
    [InlineData("$HOME/trusted-ca.pem")]
    [InlineData("${HOME}/trusted-ca.pem")]
    [InlineData("%USERPROFILE%/trusted-ca.pem")]
    public void EnvironmentVariableSyntaxIsNotExpanded(string configuredPath)
    {
        Assert.Equal(
            configuredPath,
            TrustedCertificateLoader.ResolvePath(configuredPath, _directory));
    }

    [Fact]
    public async Task MissingCertificateFailsWithResolvedPath()
    {
        var expectedPath = Path.Combine(_directory, "missing.pem");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrustedCertificateLoader.LoadAsync(
                "~/missing.pem",
                _directory,
                CancellationToken.None));

        Assert.Contains("trusted certificate file was not found", exception.Message);
        Assert.Contains(expectedPath, exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}