internal static class TrustedCertificateLoader
{
    internal static Task<string?> LoadAsync(
        string? configuredPath,
        CancellationToken cancellationToken) =>
        LoadAsync(
            configuredPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            cancellationToken);

    internal static async Task<string?> LoadAsync(
        string? configuredPath,
        string? homeDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var resolvedPath = ResolvePath(configuredPath, homeDirectory);
        try
        {
            return await File.ReadAllTextAsync(resolvedPath, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"CUCM trusted certificate file was not found at '{resolvedPath}'.",
                exception);
        }
    }

    internal static string ResolvePath(string configuredPath, string? homeDirectory)
    {
        if (!configuredPath.StartsWith('~'))
        {
            return configuredPath;
        }

        if (configuredPath.Length > 1 &&
            configuredPath[1] != Path.DirectorySeparatorChar &&
            configuredPath[1] != Path.AltDirectorySeparatorChar)
        {
            throw new InvalidOperationException(
                "CUCM setting 'trusted-certificate' supports only a standalone '~' home-directory prefix; " +
                "forms such as '~user' are not supported.");
        }

        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new InvalidOperationException(
                "CUCM setting 'trusted-certificate' uses '~', but the runtime user's home directory is unavailable.");
        }

        var relativePath = configuredPath[1..].TrimStart(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return relativePath.Length == 0
            ? homeDirectory
            : Path.Combine(homeDirectory, relativePath);
    }
}