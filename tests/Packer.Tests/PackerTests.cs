using System.IO.Compression;
using System.Text.Json;
using Packer;

namespace Packer.Tests;

public sealed class PackerTests
{
    [Fact]
    public async Task BuildAsync_WritesOnlyHighestVersionAndModInfo()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/carryon/1.0.0/carryon/lang/zh-cn.json",
            """
            {
              "item.old": "旧版本"
            }
            """);
        workspace.WriteText(
            "projects/assets/carryon/1.2.0/carryon/lang/zh-cn.json",
            """
            {
              "item.new": "新版本"
            }
            """);
        workspace.WriteText(
            "projects/assets/carryon/1.2.0/carryon/lang/en.json",
            """
            {
              "item.new": "new version"
            }
            """);

        var result = await TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath);

        Assert.Equal(1, result.SelectedTranslationCount);
        Assert.True(File.Exists(result.OutputZipPath));

        using var archive = ZipFile.OpenRead(result.OutputZipPath);
        var entryNames = archive.Entries
            .Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["assets/carryon/lang/zh-cn.json", "modinfo.json"],
            entryNames);

        using var translationReader = new StreamReader(archive.GetEntry("assets/carryon/lang/zh-cn.json")!.Open());
        var translationContent = await translationReader.ReadToEndAsync();
        Assert.Contains("\"item.new\": \"新版本\"", translationContent, StringComparison.Ordinal);
        Assert.DoesNotContain("new version", translationContent, StringComparison.Ordinal);

        using var modInfoReader = new StreamReader(archive.GetEntry("modinfo.json")!.Open());
        using var modInfoDocument = JsonDocument.Parse(await modInfoReader.ReadToEndAsync());
        var root = modInfoDocument.RootElement;
        Assert.Equal("content", root.GetProperty("type").GetString());
        Assert.Equal("client", root.GetProperty("side").GetString());
        Assert.Equal("vscnlangpack", root.GetProperty("modid").GetString());
        Assert.True(root.GetProperty("requiredOnClient").GetBoolean());
        Assert.False(root.GetProperty("requiredOnServer").GetBoolean());
    }

    [Fact]
    public async Task BuildAsync_IgnoresDirectoriesWithoutTargetLanguage()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/en.json",
            """
            {
              "item.name": "Only source"
            }
            """);
        workspace.WriteText(
            "projects/assets/example/1.1.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "可打包"
            }
            """);

        var result = await TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath);

        Assert.Equal(1, result.SelectedTranslationCount);
        Assert.Equal(1, result.SkippedDirectoryCount);
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenVersionCannotBeParsed()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "合法"
            }
            """);
        workspace.WriteText(
            "projects/assets/example/not-a-version/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "非法版本"
            }
            """);

        var ex = await Assert.ThrowsAsync<PackerException>(
            () => TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath));

        Assert.Contains("not valid NuGet/SemVer versions", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not-a-version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenNormalizedVersionsDuplicate()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.2.3/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "标准版本"
            }
            """);
        workspace.WriteText(
            "projects/assets/example/v1.2.3/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "带前缀版本"
            }
            """);

        var ex = await Assert.ThrowsAsync<PackerException>(
            () => TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath));

        Assert.Contains("normalized version '1.2.3'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenOutputPathConflictsByModIdCasing()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "小写"
            }
            """);
        workspace.WriteText(
            "projects/assets/example/2.0.0/ExampleMod/lang/zh-cn.json",
            """
            {
              "item.name": "大小写冲突"
            }
            """);

        var ex = await Assert.ThrowsAsync<PackerException>(
            () => TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath));

        Assert.Contains("same output path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenJsonIsInvalid()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "坏掉了",
            }
            """);

        var ex = await Assert.ThrowsAsync<PackerException>(
            () => TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath));

        Assert.Contains("Invalid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ThrowsWhenJsonRootIsNotObject()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            [
              "not an object"
            ]
            """);

        var ex = await Assert.ThrowsAsync<PackerException>(
            () => TranslationPackBuilder.BuildAsync(workspace.CreateConfig(), workspace.RootPath));

        Assert.Contains("JSON root must be an object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_ReturnsNonZeroForInvalidArguments()
    {
        using var workspace = new TestWorkspace();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(Array.Empty<string>(), stdout, stderr, workspace.RootPath);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_PackCommand_CreatesZip()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "CLI"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["pack", "--config", configPath],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Packed 1 translation file(s)", stdout.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(workspace.RootPath, "build", "VintageStory-Chinese-Language-Package-0.0.0.zip")));
    }

    [Fact]
    public async Task CliRunner_PackCommand_AllowsOverridingPackageVersion()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "CLI"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var version = "0.0.12-dev.1";

        var exitCode = await CliRunner.RunAsync(
            ["pack", "--config", configPath, "--package-version", version],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        var zipPath = Path.Combine(workspace.RootPath, "build", $"VintageStory-Chinese-Language-Package-{version}.zip");
        Assert.True(File.Exists(zipPath));

        using var archive = ZipFile.OpenRead(zipPath);
        using var modInfoReader = new StreamReader(archive.GetEntry("modinfo.json")!.Open());
        using var modInfoDocument = JsonDocument.Parse(await modInfoReader.ReadToEndAsync());
        Assert.Equal(version, modInfoDocument.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task CliRunner_PackCommand_ReturnsNonZeroForInvalidPackageVersion()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "CLI"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["pack", "--config", configPath, "--package-version", "0.0.1-pr.1"],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(1, exitCode);
        Assert.Contains("Vintage Story mod site version format", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_InspectCommand_ReturnsRecommendedReleaseVersionFromSelectedTranslations()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/example/1.0.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "v1"
            }
            """);
        workspace.WriteText(
            "projects/assets/example/1.1.0/examplemod/lang/zh-cn.json",
            """
            {
              "item.name": "v2"
            }
            """);

        for (var i = 0; i < 10; i++)
        {
            workspace.WriteText(
                $"projects/assets/mod{i}/1.0.0/mod{i}/lang/zh-cn.json",
                $$"""
                {
                  "item.name": "mod{{i}}"
                }
                """);
        }

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["inspect", "--config", configPath],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("selected_translation_count=11", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("release_milestone_count=10", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("recommended_package_version=0.0.1", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_DescribeReleaseCommand_WritesFullTable()
    {
        using var workspace = new TestWorkspace();

        for (var i = 0; i < 10; i++)
        {
            workspace.WriteText(
                $"projects/assets/mod{i}/1.0.{i}/mod{i}/lang/zh-cn.json",
                $$"""
                {
                  "item.name": "mod{{i}}"
                }
                """);
        }

        workspace.WriteText(
            "projects/assets/zmod/9.9.9/zmod/lang/zh-cn.json",
            """
            {
              "item.name": "zmod"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["describe-release", "--config", configPath, "--package-version", "0.0.1", "--release-kind", "release"],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("# VSCN Vintage Story 汉化包", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("语言包版本：0.0.1", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("发布类型：release", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("| 模组中文名称 | 模组英文名称 | 模组ID | 模组最新版本 | 模组贡献者 |", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("| mod0 | mod0 | mod0 | 1.0.0 |  |", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("zmod", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_DescribePackageCommand_WritesFullTable()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/betterloot/2.0.2/betterloot/lang/zh-cn.json",
            """
            {
              "item-gearpart": "更好的战利品"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["describe-package", "--config", configPath, "--package-version", "0.0.1"],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("# VSCN Vintage Story 汉化包", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("## 模组清单", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("betterloot", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliRunner_DescribePackageCommand_UsesIndexMetadata()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteText(
            "projects/assets/index.json",
            """
            {
              "betterloot": {
                "name": "Better Loot",
                "translation": "更好的战利品",
                "authors": [
                  "DejFidOFF"
                ],
                "homepage": "https://mods.vintagestory.at/betterloot",
                "latestVersion": "2.0.3"
              }
            }
            """);
        workspace.WriteText(
            "projects/assets/betterloot/2.0.2/betterloot/lang/zh-cn.json",
            """
            {
              "item-gearpart": "更好的战利品"
            }
            """);

        var configPath = workspace.WriteConfigFile();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["describe-package", "--config", configPath, "--package-version", "0.0.1"],
            stdout,
            stderr,
            workspace.RootPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains(
            "| 更好的战利品 | Better Loot | betterloot | 2.0.3 |  |",
            stdout.ToString(),
            StringComparison.Ordinal);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "vscn-packer-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(RootPath, "projects", "assets"));
        }

        public string RootPath { get; }

        public PackerConfig CreateConfig() => new();

        public string WriteConfigFile()
        {
            var path = Path.Combine(RootPath, "config", "packer", "default.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                """
                {
                  "packageName": "VSCN Vintage Story Chinese Language Pack",
                  "packageVersion": "0.0.0",
                  "description": "聚合简体中文语言包，覆盖已安装的受支持 Vintage Story 模组。",
                  "authors": ["VSCN-Studio"],
                  "modId": "vscnlangpack",
                  "targetLanguage": "zh-cn",
                  "contentRoot": "projects/assets",
                  "outputDirectory": "build",
                  "outputFileNameTemplate": "VintageStory-Chinese-Language-Package-{version}.zip",
                  "excludedProjects": [],
                  "excludedModIds": [],
                  "excludedVersions": [],
                  "versionSelectionStrategy": "highest-semver"
                }
                """);
            return path;
        }

        public void WriteText(string relativePath, string content)
        {
            var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
