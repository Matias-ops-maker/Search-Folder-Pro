namespace SearchFolderPro.Core.Models;

public sealed record FileSearchOptions(
    string SearchText,
    string? Extension = null,
    long? MinimumSizeBytes = null,
    long? MaximumSizeBytes = null,
    DateTime? ModifiedAfter = null,
    bool SearchContents = true);