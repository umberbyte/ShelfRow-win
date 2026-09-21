using System.Collections.Generic;

namespace ShelfRow.Core.Models;

/// <summary>
/// Rows edited on this machine that iCloud has not taken yet.
/// </summary>
public record PendingUploads(
    IReadOnlyList<Item> Items,
    IReadOnlyList<Shelf> Shelves,
    IReadOnlyList<Volume> Volumes)
{
    public int Count => Items.Count + Shelves.Count + Volumes.Count;
}
