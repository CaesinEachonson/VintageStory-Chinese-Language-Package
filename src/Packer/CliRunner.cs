using System.Text;

namespace Packer;

public static class CliRunner
{
    private const string Usage =
        "Usage: dotnet run --project src/Packer -- <pack|inspect|describe-release|describe-package> --config <path> [--package-version <value>] [--milestone <value>] [--fetch-api]";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseArguments(args, out var command, out var configPath, out var packageVersion, out var milestone, out var fetchApi, out var parseError))
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
                var description = TranslationPackBuilder.DescribeReleaseMilestone(
                    config,
                    repositoryRoot,
                    milestone!.Value,
                    packageVersion!);
                var metadata = await LoadMetadataAsync(config, repositoryRoot, description.Entries, fetchApi, cancellationToken);
                await stdout.WriteAsync(FormatReleaseMilestoneDescription(description, metadata));
                return 0;
            }

            if (string.Equals(command, "describe-package", StringComparison.OrdinalIgnoreCase))
            {
                var description = TranslationPackBuilder.DescribeReleasePackage(
                    config,
                    repositoryRoot,
                    packageVersion!);
                var metadata = await LoadMetadataAsync(config, repositoryRoot, description.Entries, fetchApi, cancellationToken);
                await stdout.WriteAsync(FormatReleasePackageDescription(description, metadata));
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
        out int? milestone,
        out bool fetchApi,
        out string? error)
    {
        command = null;
        configPath = null;
        packageVersion = null;
        milestone = null;
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

            if (string.Equals(arg, "--milestone", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    error = $"Missing value for --milestone.{Environment.NewLine}{Usage}";
                    return false;
                }

                if (!int.TryParse(args[++i], out var parsedMilestone))
                {
                    error = $"Invalid value for --milestone.{Environment.NewLine}{Usage}";
                    return false;
                }

                milestone = parsedMilestone;
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

        if (string.Equals(command, "inspect", StringComparison.OrdinalIgnoreCase) &&
            milestone is not null)
        {
            error = $"--milestone is not supported by the inspect command.{Environment.NewLine}{Usage}";
            return false;
        }

        if ((string.Equals(command, "pack", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(command, "inspect", StringComparison.OrdinalIgnoreCase)) &&
            fetchApi)
        {
            error = $"--fetch-api is only supported by the describe commands.{Environment.NewLine}{Usage}";
            return false;
        }

        if (string.Equals(command, "pack", StringComparison.OrdinalIgnoreCase) &&
            milestone is not null)
        {
            error = $"--milestone is only supported by the describe-release command.{Environment.NewLine}{Usage}";
            return false;
        }

        if (string.Equals(command, "describe-release", StringComparison.OrdinalIgnoreCase))
        {
            if (milestone is null)
            {
                error = $"Missing required --milestone argument.{Environment.NewLine}{Usage}";
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
            if (milestone is not null)
            {
                error = $"--milestone is only supported by the describe-release command.{Environment.NewLine}{Usage}";
                return false;
            }

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

    private static string FormatReleaseMilestoneDescription(
        ReleaseMilestoneDescription description,
        IReadOnlyDictionary<string, ModMetadata> metadata)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"自动发布：已达到 {description.MilestoneCount} 个入包模组翻译。");
        builder.AppendLine();
        builder.AppendLine($"入包翻译数量：{description.SelectedTranslationCount}");
        builder.AppendLine($"跳过缺少 zh-cn.json 的目录：{description.SkippedDirectoryCount}");
        builder.AppendLine($"语言包版本：{description.PackageVersion}");
        builder.AppendLine();
        builder.AppendLine($"本次档位条目（{description.BatchStartIndex}-{description.BatchEndIndex}）：");
        builder.AppendLine();
        AppendEntriesTable(builder, description.Entries, metadata);

        return builder.ToString();
    }

    private static string FormatReleasePackageDescription(
        ReleasePackageDescription description,
        IReadOnlyDictionary<string, ModMetadata> metadata)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# VSCN Vintage Story 汉化包");
        builder.AppendLine();
        builder.AppendLine($"语言包版本：{description.PackageVersion}");
        builder.AppendLine();
        builder.AppendLine($"入包翻译数量：{description.SelectedTranslationCount}");
        builder.AppendLine($"跳过缺少 zh-cn.json 的目录：{description.SkippedDirectoryCount}");
        builder.AppendLine();
        builder.AppendLine("## 模组清单");
        builder.AppendLine();
        AppendEntriesTable(builder, description.Entries, metadata);

        return builder.ToString();
    }

    private static void AppendEntriesTable(
        StringBuilder builder,
        IReadOnlyList<ReleaseMilestoneEntry> entries,
        IReadOnlyDictionary<string, ModMetadata> metadata)
    {
        builder.AppendLine("| 模组中文名称 | 模组英文名称 | 模组ID | 模组最新版本 | 模组贡献者 |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var entry in entries)
        {
            var item = ModMetadataProvider.ResolveEntryMetadata(entry, metadata);
            builder.AppendLine(
                $"| {EscapeMarkdownTableCell(item.ChineseName)} | {EscapeMarkdownTableCell(item.EnglishName)} | {EscapeMarkdownTableCell(item.ModId)} | {EscapeMarkdownTableCell(item.LatestVersion)} | {FormatContributors(item.Contributors)} |");
        }
    }

    private static string FormatContributors(IReadOnlyList<string> contributors)
    {
        if (contributors.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            contributors.Select(contributor =>
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
