namespace SearchFolderPro.Core.Models;

public sealed record FileSearchResult(
    string Name,
    string Path,
    string DirectoryPath,
    string Extension,
    long SizeBytes,
    DateTime ModifiedUtc);