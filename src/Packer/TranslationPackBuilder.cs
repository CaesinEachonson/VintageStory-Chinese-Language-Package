using System.IO.Compression;
using System.Text.Json;
using NuGet.Versioning;

namespace Packer;

public static class TranslationPackBuilder
{
    public static PackInspection Inspect(
        PackerConfig config,
        string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var prepared = PrepareBuild(config, repositoryRoot);
        var selectedTranslationCount = prepared.SelectedTranslations.Count;

        return new PackInspection(
            selectedTranslationCount,
            prepared.ScanResult.SkippedDirectoryCount,
            selectedTranslationCount / 10 * 10,
            PackageVersionCalculator.GetReleasePackageVersion(selectedTranslationCount));
    }

    public static async Task<PackResult> BuildAsync(
        PackerConfig config,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var prepared = PrepareBuild(config, repositoryRoot);
        var validated = await ValidateSelectedTranslationsAsync(prepared.SelectedTranslations, cancellationToken);

        var outputDirectory = PackerConfigLoader.ResolvePath(config.OutputDirectory, repositoryRoot);
        Directory.CreateDirectory(outputDirectory);

        var outputZipPath = Path.Combine(outputDirectory, config.ResolveOutputFileName());
        var tempZipPath = outputZipPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await CreateZipAsync(tempZipPath, config, validated, cancellationToken);
            ReplaceOutputAtomically(tempZipPath, outputZipPath);
        }
        catch
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            throw;
        }

        return new PackResult(outputZipPath, validated.Count, prepared.ScanResult.SkippedDirectoryCount);
    }

    public static ReleasePackageDescription DescribeReleasePackage(
        PackerConfig config,
        string repositoryRoot,
        string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);

        var prepared = PrepareBuild(config, repositoryRoot);
        var ordered = prepared.SelectedTranslations
            .OrderBy(item => item.SourceDirectory, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = ordered
            .Select(item => new ReleaseMilestoneEntry(
                item.ProjectSlug,
                item.TargetModVersion,
                item.RealModId,
                item.SourceDirectory,
                item.SourceFilePath,
                item.DestinationPath))
            .ToArray();

        return new ReleasePackageDescription(
            entries.Length,
            prepared.ScanResult.SkippedDirectoryCount,
            packageVersion.Trim(),
            entries);
    }

    private static PreparedBuild PrepareBuild(PackerConfig config, string repositoryRoot)
    {
        config.ApplyDefaults();
        config.Validate();

        var contentRoot = PackerConfigLoader.ResolvePath(config.ContentRoot, repositoryRoot);
        if (!Directory.Exists(contentRoot))
        {
            throw new PackerException($"Content root does not exist: {contentRoot}");
        }

        var scanResult = ScanCandidates(contentRoot, config);
        var selected = SelectCandidates(scanResult.Candidates, config);
        return new PreparedBuild(scanResult, selected);
    }

    private static void ReplaceOutputAtomically(string tempZipPath, string outputZipPath)
    {
        if (File.Exists(outputZipPath))
        {
            File.Replace(tempZipPath, outputZipPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempZipPath, outputZipPath);
    }

    private static ScanResult ScanCandidates(string contentRoot, PackerConfig config)
    {
        var excludedProjects = config.GetExcludedProjectsSet();
        var excludedModIds = config.GetExcludedModIdsSet();
        var excludedVersions = config.GetExcludedVersionsSet();
        var candidates = new List<TranslationCandidate>();
        var skippedDirectoryCount = 0;

        foreach (var projectDirectory in Directory.EnumerateDirectories(contentRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var projectSlug = Path.GetFileName(projectDirectory);
            if (excludedProjects.Contains(projectSlug))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(projectDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var targetModVersion = Path.GetFileName(versionDirectory);
                if (excludedVersions.Contains(targetModVersion))
                {
                    continue;
                }

                foreach (var modIdDirectory in Directory.EnumerateDirectories(versionDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var realModId = Path.GetFileName(modIdDirectory);
                    if (excludedModIds.Contains(realModId))
                    {
                        continue;
                    }

                    var languageFilePath = Path.Combine(modIdDirectory, "lang", $"{config.TargetLanguage}.json");
                    if (!File.Exists(languageFilePath))
                    {
                        skippedDirectoryCount++;
                        continue;
                    }

                    candidates.Add(new TranslationCandidate(
                        projectSlug,
                        targetModVersion,
                        realModId,
                        modIdDirectory,
                        languageFilePath));
                }
            }
        }

        return new ScanResult(candidates, skippedDirectoryCount);
    }

    private static List<SelectedTranslation> SelectCandidates(
        IReadOnlyCollection<TranslationCandidate> candidates,
        PackerConfig config)
    {
        var selected = new List<SelectedTranslation>();

        foreach (var destinationGroup in candidates
                     .GroupBy(candidate => candidate.GetDestinationPath(config.TargetLanguage), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var groupCandidates = destinationGroup.ToList();
            var distinctRealModIds = groupCandidates
                .Select(candidate => candidate.RealModId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (distinctRealModIds.Length > 1)
            {
                throw new PackerException(
                    $"Conflicting real mod IDs would generate the same output path '{destinationGroup.Key}'. Sources:{Environment.NewLine}{FormatSources(groupCandidates)}");
            }

            var parsedCandidates = new List<VersionedCandidate>();
            foreach (var candidate in groupCandidates)
            {
                if (!TryParseVersion(candidate.TargetModVersion, out var parsedVersion))
                {
                    throw new PackerException(
                        $"Could not sort versions for output '{destinationGroup.Key}' because one or more version directories are not valid NuGet/SemVer versions. Sources:{Environment.NewLine}{FormatSources(groupCandidates)}");
                }

                parsedCandidates.Add(new VersionedCandidate(candidate, parsedVersion!));
            }

            var duplicateVersionGroup = parsedCandidates
                .GroupBy(item => item.Version.ToNormalizedString(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicateVersionGroup is not null)
            {
                throw new PackerException(
                    $"Found multiple translations for output '{destinationGroup.Key}' at normalized version '{duplicateVersionGroup.Key}'. Sources:{Environment.NewLine}{FormatSources(duplicateVersionGroup.Select(item => item.Candidate))}");
            }

            var winner = parsedCandidates
                .OrderByDescending(item => item.Version, VersionComparer.VersionReleaseMetadata)
                .First();

            selected.Add(new SelectedTranslation(
                winner.Candidate.ProjectSlug,
                winner.Candidate.TargetModVersion,
                winner.Candidate.RealModId,
                winner.Candidate.SourceDirectory,
                winner.Candidate.SourceFilePath,
                destinationGroup.Key));
        }

        return selected;
    }

    private static async Task<List<ValidatedTranslation>> ValidateSelectedTranslationsAsync(
        IEnumerable<SelectedTranslation> selected,
        CancellationToken cancellationToken)
    {
        var validated = new List<ValidatedTranslation>();

        foreach (var item in selected.OrderBy(entry => entry.DestinationPath, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(item.SourceFilePath, cancellationToken);
            var payload = StripUtf8Bom(bytes);

            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new PackerException($"JSON root must be an object: {item.SourceFilePath}");
                }
            }
            catch (JsonException ex)
            {
                throw new PackerException($"Invalid JSON in '{item.SourceFilePath}': {ex.Message}");
            }

            validated.Add(new ValidatedTranslation(item, bytes));
        }

        return validated;
    }

    private static async Task CreateZipAsync(
        string zipPath,
        PackerConfig config,
        IReadOnlyCollection<ValidatedTranslation> translations,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        var modInfoEntry = archive.CreateEntry("modinfo.json", CompressionLevel.Optimal);
        await using (var modInfoStream = modInfoEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                modInfoStream,
                new ModInfo(config),
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken);
        }

        foreach (var translation in translations)
        {
            var entry = archive.CreateEntry(translation.Selected.DestinationPath, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(translation.Content, cancellationToken);
        }
    }

    private static ReadOnlyMemory<byte> StripUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return bytes.AsMemory(3);
        }

        return bytes;
    }

    private static bool TryParseVersion(string rawVersion, out NuGetVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var normalized = rawVersion.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return NuGetVersion.TryParse(normalized, out version);
    }

    private static string FormatSources(IEnumerable<TranslationCandidate> candidates)
    {
        return string.Join(
            Environment.NewLine,
            candidates.Select(candidate => $"- {candidate.SourceDirectory}"));
    }

    private sealed record TranslationCandidate(
        string ProjectSlug,
        string TargetModVersion,
        string RealModId,
        string SourceDirectory,
        string SourceFilePath)
    {
        public string GetDestinationPath(string targetLanguage)
        {
            return $"assets/{RealModId}/lang/{targetLanguage}.json";
        }
    }

    private sealed record VersionedCandidate(TranslationCandidate Candidate, NuGetVersion Version);

    private sealed record SelectedTranslation(
        string ProjectSlug,
        string TargetModVersion,
        string RealModId,
        string SourceDirectory,
        string SourceFilePath,
        string DestinationPath);

    private sealed record ValidatedTranslation(
        SelectedTranslation Selected,
        byte[] Content);

    private sealed record ScanResult(
        IReadOnlyList<TranslationCandidate> Candidates,
        int SkippedDirectoryCount);

    private sealed record PreparedBuild(
        ScanResult ScanResult,
        List<SelectedTranslation> SelectedTranslations);

    private sealed class ModInfo
    {
        public ModInfo(PackerConfig config)
        {
            Type = "content";
            ModId = config.ModId;
            Name = config.PackageName;
            Description = config.Description;
            Version = config.PackageVersion;
            Authors = config.Authors;
            Side = "client";
            RequiredOnClient = true;
            RequiredOnServer = false;
        }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; }

        [System.Text.Json.Serialization.JsonPropertyName("modid")]
        public string ModId { get; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; }

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string Description { get; }

        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string Version { get; }

        [System.Text.Json.Serialization.JsonPropertyName("authors")]
        public string[] Authors { get; }

        [System.Text.Json.Serialization.JsonPropertyName("side")]
        public string Side { get; }

        [System.Text.Json.Serialization.JsonPropertyName("requiredOnClient")]
        public bool RequiredOnClient { get; }

        [System.Text.Json.Serialization.JsonPropertyName("requiredOnServer")]
        public bool RequiredOnServer { get; }
    }
}
