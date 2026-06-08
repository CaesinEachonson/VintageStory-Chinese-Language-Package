using System.Text.Json;

namespace Packer;

public static class PackerConfigLoader
{
    public static async Task<PackerConfig> LoadAsync(
        string configPath,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var absoluteConfigPath = ResolvePath(configPath, repositoryRoot);
        if (!File.Exists(absoluteConfigPath))
        {
            throw new PackerException($"Config file does not exist: {absoluteConfigPath}");
        }

        await using var stream = File.OpenRead(absoluteConfigPath);
        var config = await JsonSerializer.DeserializeAsync<PackerConfig>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            },
            cancellationToken);

        if (config is null)
        {
            throw new PackerException($"Failed to load config file: {absoluteConfigPath}");
        }

        config.ApplyDefaults();
        config.Validate();
        return config;
    }

    public static string ResolvePath(string path, string repositoryRoot)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(repositoryRoot, path));
    }
}
