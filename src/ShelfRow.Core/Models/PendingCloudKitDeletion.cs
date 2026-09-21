namespace ShelfRow.Core.Models;

/// <summary>
/// A CloudKit record removed locally and retained until the server confirms deletion.
/// </summary>
public record PendingCloudKitDeletion(string RecordName, string RecordType);
