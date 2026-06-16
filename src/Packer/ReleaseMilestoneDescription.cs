namespace Packer;

public sealed record ReleaseMilestoneDescription(
    int MilestoneCount,
    int BatchStartIndex,
    int BatchEndIndex,
    int SelectedTranslationCount,
    int SkippedDirectoryCount,
    string PackageVersion,
    IReadOnlyList<ReleaseMilestoneEntry> Entries);

public sealed record ReleasePackageDescription(
    int SelectedTranslationCount,
    int SkippedDirectoryCount,
    string PackageVersion,
    IReadOnlyList<ReleaseMilestoneEntry> Entries);

public sealed record ReleaseMilestoneEntry(
    string ProjectSlug,
    string TargetModVersion,
    string RealModId,
    string SourceDirectory,
    string DestinationPath);
