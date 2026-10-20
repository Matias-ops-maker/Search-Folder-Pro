namespace SearchFolderPro.Core.Models;

public sealed record IndexingProgress(
    string RootPath,
    string CurrentDirectory,
    long FilesIndexed,
    long Failures);

public sealed record IndexingResult(
    string RootPath,
    long FilesIndexed,
    long Failures,
    bool IsComplete);