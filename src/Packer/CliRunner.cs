using System.Text;

namespace Packer;

public static class CliRunner
{
    private const string Usage =
        "Usage: dotnet run --project src/Packer -- <pack|inspect|describe-release|describe-package> --config <path> [--package-version <value>] [--release-kind <release|pre-release>] [--fetch-api]";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var command, out var configPath, out var packageVersion, out var releaseKind, out var fetchApi, out var parseError))
        {
            await stderr.WriteLineAsync(parseError ?? Usage);
            return 1;
        }

        try
        {
            var config = await PackerConfigLoader.LoadAsync(configPath!, repositoryRoot, cancellationToken);
            if (string.Equals(command, "inspect", StringComparison.OrdinalIgnoreCase))
            {
                var inspection = TranslationPackBuilder.Inspect(config, repositoryRoot);
                await stdout.WriteLineAsync($"selected_translation_count={inspection.SelectedTranslationCount}");
                await stdout.WriteLineAsync($"skipped_directory_count={inspection.SkippedDirectoryCount}");
                await stdout.WriteLineAsync($"release_milestone_count={inspection.ReleaseMilestoneCount}");
                await stdout.WriteLineAsync($"recommended_package_version={inspection.RecommendedPackageVersion}");
                return 0;
            }

            if (string.Equals(command, "describe-release", StringComparison.OrdinalIgnoreCase))
            {
                var description = TranslationPackBuilder.DescribeReleasePackage(
                    config,
                    repositoryRoot,
                    packageVersion!);
                var metadata = await LoadMetadataAsync(config, repositoryRoot, description.Entries, fetchApi, cancellationToken);
                await stdout.WriteAsync(FormatReleasePackageDescription(description, metadata, repositoryRoot, releaseKind!));
                return 0;
            }

            if (string.Equals(command, "describe-package", StringComparison.OrdinalIgnoreCase))
            {
                var description = TranslationPackBuilder.DescribeReleasePackage(
                    config,
                    repositoryRoot,
                    packageVersion!);
                var metadata = await LoadMetadataAsync(config, repositoryRoot, description.Entries, fetchApi, cancellationToken);
                await stdout.WriteAsync(FormatReleasePackageDescription(description, metadata, repositoryRoot, "release"));
                return 0;
            }

            if (packageVersion is not null)
            {
                config.PackageVersion = packageVersion.Trim();
                config.Validate();
            }

            var result = await TranslationPackBuilder.BuildAsync(config, repositoryRoot, cancellationToken);
            var relativeOutputPath = Path.GetRelativePath(repositoryRoot, result.OutputZipPath);

            await stdout.WriteLineAsync(
                $"Packed {result.SelectedTranslationCount} translation file(s) to {relativeOutputPath}.");

            if (result.SkippedDirectoryCount > 0)
            {
                await stdout.WriteLineAsync(
                    $"Skipped {result.SkippedDirectoryCount} directory(s) without {config.TargetLanguage}.json.");
            }

            return 0;
        }
        catch (PackerException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Unexpected error: {ex.Message}");
            return 1;
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out string? command,
        out string? configPath,
        out string? packageVersion,
        out string? releaseKind,
        out bool fetchApi,
        out string? error)
    {
        command = null;
        configPath = null;
        packageVersion = null;
        releaseKind = null;
        fetchApi = false;
        error = null;

        if (args.Length == 0)
        {
            error = Usage;
            return false;
        }

        if (!string.Equals(args[0], "pack", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[0], "describe-release", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[0], "describe-package", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown command '{args[0]}'.{Environment.NewLine}{Usage}";
            return false;
        }

        command = args[0];

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for --config.{Environment.NewLine}{Usage}";
                    return false;
                }

                configPath = args[++i];
                continue;
            }

            if (string.Equals(arg, "--package-version", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for --package-version.{Environment.NewLine}{Usage}";
                    return false;
                }

                packageVersion = args[++i];
                continue;
            }

            if (string.Equals(arg, "--release-kind", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for --release-kind.{Environment.NewLine}{Usage}";
                    return false;
                }

                releaseKind = args[++i].Trim();
                if (!string.Equals(releaseKind, "release", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(releaseKind, "pre-release", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Invalid value for --release-kind.{Environment.NewLine}{Usage}";
                    return false;
                }
                continue;
            }

            if (string.Equals(arg, "--fetch-api", StringComparison.OrdinalIgnoreCase))
            {
                fetchApi = true;
                continue;
            }

            error = $"Unknown argument '{arg}'.{Environment.NewLine}{Usage}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            error = $"Missing required --config argument.{Environment.NewLine}{Usage}";
            return false;
        }

        if (string.Equals(command, "inspect", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(packageVersion))
        {
            error = $"--package-version is only supported by the pack command.{Environment.NewLine}{Usage}";
            return false;
        }

        if ((string.Equals(command, "pack", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(command, "inspect", StringComparison.OrdinalIgnoreCase)) &&
            fetchApi)
        {
            error = $"--fetch-api is only supported by the describe commands.{Environment.NewLine}{Usage}";
            return false;
        }

        if (string.Equals(command, "describe-release", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(releaseKind))
            {
                error = $"Missing required --release-kind argument.{Environment.NewLine}{Usage}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                error = $"Missing required --package-version argument.{Environment.NewLine}{Usage}";
                return false;
            }
        }

        if (string.Equals(command, "describe-package", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                error = $"Missing required --package-version argument.{Environment.NewLine}{Usage}";
                return false;
            }
        }

        return true;
    }

    private static async Task<IReadOnlyDictionary<string, ModMetadata>> LoadMetadataAsync(
        PackerConfig config,
        string repositoryRoot,
        IReadOnlyList<ReleaseMilestoneEntry> entries,
        bool fetchApi,
        CancellationToken cancellationToken)
    {
        var contentRoot = PackerConfigLoader.ResolvePath(config.ContentRoot, repositoryRoot);
        var projectSlugs = entries.Select(entry => entry.ProjectSlug);
        var projectModIds = entries
            .GroupBy(entry => entry.ProjectSlug, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().RealModId,
                StringComparer.OrdinalIgnoreCase);

        return await ModMetadataProvider.LoadAsync(contentRoot, projectSlugs, projectModIds, fetchApi, cancellationToken);
    }

    private static string FormatReleasePackageDescription(
        ReleasePackageDescription description,
        IReadOnlyDictionary<string, ModMetadata> metadata,
        string repositoryRoot,
        string releaseKind)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# VSCN Vintage Story 汉化包");
        builder.AppendLine();
        builder.AppendLine($"语言包版本：{description.PackageVersion}");
        builder.AppendLine($"发布类型：{releaseKind}");
        builder.AppendLine();
        builder.AppendLine($"入包翻译数量：{description.SelectedTranslationCount}");
        builder.AppendLine($"跳过缺少 zh-cn.json 的目录：{description.SkippedDirectoryCount}");
        builder.AppendLine();
        builder.AppendLine("## 模组清单");
        builder.AppendLine();
        AppendEntriesTable(builder, description.Entries, metadata, repositoryRoot);

        return builder.ToString();
    }

    private static void AppendEntriesTable(
        StringBuilder builder,
        IReadOnlyList<ReleaseMilestoneEntry> entries,
        IReadOnlyDictionary<string, ModMetadata> metadata,
        string repositoryRoot)
    {
        builder.AppendLine("| 模组中文名称 | 模组英文名称 | 模组ID | 模组最新版本 | 模组贡献者 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var entry in entries)
        {
            var item = ModMetadataProvider.ResolveEntryMetadata(entry, metadata, repositoryRoot);
            builder.AppendLine(
                $"| {EscapeMarkdownTableCell(item.ChineseName)} | {EscapeMarkdownTableCell(item.EnglishName)} | {EscapeMarkdownTableCell(item.ModId)} | {EscapeMarkdownTableCell(item.LatestVersion)} | {FormatTranslators(item.Translators)} |");
        }
    }

    private static string FormatTranslators(IReadOnlyList<string> translators)
    {
        if (translators.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            translators.Select(contributor =>
            {
                var escaped = EscapeMarkdownTableCell(contributor);
                return IsGitHubUserName(contributor)
                    ? $"[{escaped}](https://github.com/{Uri.EscapeDataString(contributor)})"
                    : escaped;
            }));
    }

    private static bool IsGitHubUserName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 39)
        {
            return false;
        }

        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
               && value[0] != '-'
               && value[^1] != '-'
               && !value.Contains("--", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownTableCell(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
