using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packer;

public sealed record ModMetadata(
    string Name,
    string Translation,
    string[] Authors,
    string Homepage,
    string LatestVersion);

public sealed record ReleaseEntryMetadata(
    string ChineseName,
    string EnglishName,
    string ModId,
    string LatestVersion,
    string[] Contributors);

public static class ModMetadataProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<IReadOnlyDictionary<string, ModMetadata>> LoadAsync(
        string contentRoot,
        IEnumerable<string> projectSlugs,
        IReadOnlyDictionary<string, string> projectModIds,
        bool fetchApi,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentNullException.ThrowIfNull(projectSlugs);
        ArgumentNullException.ThrowIfNull(projectModIds);

        var index = await LoadIndexAsync(contentRoot, cancellationToken);
        var slugs = projectSlugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var metadata = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!fetchApi)
        {
            foreach (var slug in slugs)
            {
                metadata[slug] = MergeMetadata(slug, null, GetIndexMetadata(index, slug, projectModIds));
            }

            return metadata;
        }

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://mods.vintagestory.at/"),
            Timeout = TimeSpan.FromSeconds(20)
        };

        using var throttle = new SemaphoreSlim(6);
        var tasks = slugs.Select(async slug =>
        {
            var indexMetadata = GetIndexMetadata(index, slug, projectModIds);
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var apiMetadata = await FetchApiMetadataAsync(
                    httpClient,
                    BuildApiCandidates(slug, projectModIds, indexMetadata),
                    cancellationToken);
                return (Slug: slug, Metadata: MergeMetadata(slug, apiMetadata, indexMetadata));
            }
            finally
            {
                throttle.Release();
            }
        });

        foreach (var result in await Task.WhenAll(tasks))
        {
            metadata[result.Slug] = result.Metadata;
        }

        return metadata;
    }

    public static ReleaseEntryMetadata ResolveEntryMetadata(
        ReleaseMilestoneEntry entry,
        IReadOnlyDictionary<string, ModMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(metadata);

        metadata.TryGetValue(entry.ProjectSlug, out var item);
        if (item is null)
        {
            metadata.TryGetValue(entry.RealModId, out item);
        }

        var englishName = FirstNonWhiteSpace(item?.Name, entry.ProjectSlug);
        var latestVersion = FirstNonWhiteSpace(item?.LatestVersion, entry.TargetModVersion);

        return new ReleaseEntryMetadata(
            FirstNonWhiteSpace(item?.Translation, englishName),
            englishName,
            entry.RealModId,
            latestVersion,
            NormalizeAuthors(item?.Authors));
    }

    private static async Task<IReadOnlyDictionary<string, ModMetadata>> LoadIndexAsync(
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(contentRoot, "index.json");
        if (!File.Exists(indexPath))
        {
            return new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(indexPath);
            var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, ModMetadata>>(
                stream,
                JsonOptions,
                cancellationToken);

            return raw?
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    pair => pair.Key.Trim(),
                    pair => NormalizeMetadata(pair.Value),
                    StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new PackerException($"Invalid metadata index '{indexPath}': {ex.Message}");
        }
    }

    private static ModMetadata? GetIndexMetadata(
        IReadOnlyDictionary<string, ModMetadata> index,
        string slug,
        IReadOnlyDictionary<string, string> projectModIds)
    {
        if (index.TryGetValue(slug, out var metadata))
        {
            return metadata;
        }

        if (projectModIds.TryGetValue(slug, out var realModId) &&
            index.TryGetValue(realModId, out metadata))
        {
            return metadata;
        }

        return null;
    }

    private static async Task<ModMetadata?> FetchApiMetadataAsync(
        HttpClient httpClient,
        IEnumerable<string> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var escaped = Uri.EscapeDataString(candidate);
            try
            {
                using var response = await httpClient.GetAsync($"api/mod/{escaped}", cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var payload = await JsonSerializer.DeserializeAsync<ModApiResponse>(
                    stream,
                    JsonOptions,
                    cancellationToken);

                var metadata = ConvertApiMetadata(payload);
                if (metadata is not null)
                {
                    return metadata;
                }
            }
            catch (HttpRequestException)
            {
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return null;
    }

    private static ModMetadata? ConvertApiMetadata(ModApiResponse? response)
    {
        if (response?.Mod is null ||
            !string.Equals(response.StatusCode, "200", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mod = response.Mod;
        var latestRelease = mod.Releases?
            .Where(release => !string.IsNullOrWhiteSpace(release.ModVersion))
            .OrderByDescending(release => ParseDate(release.Created))
            .FirstOrDefault();

        return NormalizeMetadata(new ModMetadata(
            FirstNonWhiteSpace(mod.Name, string.Empty),
            string.Empty,
            NormalizeAuthors([mod.Author ?? string.Empty]),
            BuildHomepage(mod),
            FirstNonWhiteSpace(latestRelease?.ModVersion, string.Empty)));
    }

    private static IEnumerable<string> BuildApiCandidates(
        string slug,
        IReadOnlyDictionary<string, string> projectModIds,
        ModMetadata? indexMetadata)
    {
        var candidates = new List<string> { slug };

        if (projectModIds.TryGetValue(slug, out var realModId))
        {
            candidates.Add(realModId);
        }

        if (!string.IsNullOrWhiteSpace(indexMetadata?.Homepage))
        {
            candidates.AddRange(GetCandidatesFromHomepage(indexMetadata.Homepage));
        }

        return candidates
            .Select(candidate => candidate.Trim())
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCandidatesFromHomepage(string homepage)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("mods.vintagestory.at", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            yield break;
        }

        if (segments.Length >= 3 &&
            segments[0].Equals("show", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("mod", StringComparison.OrdinalIgnoreCase))
        {
            yield return segments[2];
            yield break;
        }

        yield return segments[^1];
    }

    private static ModMetadata MergeMetadata(
        string fallbackName,
        ModMetadata? apiMetadata,
        ModMetadata? indexMetadata)
    {
        return NormalizeMetadata(new ModMetadata(
            FirstNonWhiteSpace(indexMetadata?.Name, apiMetadata?.Name, fallbackName),
            FirstNonWhiteSpace(indexMetadata?.Translation, string.Empty),
            NormalizeAuthors(indexMetadata?.Authors).Length > 0
                ? NormalizeAuthors(indexMetadata?.Authors)
                : NormalizeAuthors(apiMetadata?.Authors),
            FirstNonWhiteSpace(indexMetadata?.Homepage, apiMetadata?.Homepage, string.Empty),
            FirstNonWhiteSpace(indexMetadata?.LatestVersion, apiMetadata?.LatestVersion, string.Empty)));
    }

    private static ModMetadata NormalizeMetadata(ModMetadata? metadata)
    {
        return new ModMetadata(
            FirstNonWhiteSpace(metadata?.Name, string.Empty),
            FirstNonWhiteSpace(metadata?.Translation, string.Empty),
            NormalizeAuthors(metadata?.Authors),
            FirstNonWhiteSpace(metadata?.Homepage, string.Empty),
            FirstNonWhiteSpace(metadata?.LatestVersion, string.Empty));
    }

    private static string[] NormalizeAuthors(IEnumerable<string>? authors)
    {
        return authors?
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Select(author => author.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];
    }

    private static string BuildHomepage(ModApiMod mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.UrlAlias))
        {
            return $"https://mods.vintagestory.at/{mod.UrlAlias.Trim().Trim('/')}";
        }

        return mod.ModId > 0
            ? $"https://mods.vintagestory.at/show/mod/{mod.ModId}"
            : string.Empty;
    }

    private static DateTimeOffset ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed class ModApiResponse
    {
        [JsonPropertyName("statuscode")]
        public string? StatusCode { get; set; }

        [JsonPropertyName("mod")]
        public ModApiMod? Mod { get; set; }
    }

    private sealed class ModApiMod
    {
        [JsonPropertyName("modid")]
        public int ModId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("urlalias")]
        public string? UrlAlias { get; set; }

        [JsonPropertyName("releases")]
        public ModApiRelease[]? Releases { get; set; }
    }

    private sealed class ModApiRelease
    {
        [JsonPropertyName("modversion")]
        public string? ModVersion { get; set; }

        [JsonPropertyName("created")]
        public string? Created { get; set; }
    }
}
