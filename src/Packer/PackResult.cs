namespace Packer;

public sealed record PackResult(
    string OutputZipPath,
    int SelectedTranslationCount,
    int SkippedDirectoryCount);
