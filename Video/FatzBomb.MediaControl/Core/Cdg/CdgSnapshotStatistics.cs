namespace FatzBomb.MediaControl.Core.Cdg;

/// <summary>
/// Allocation and copy counters for <see cref="CdgStateMachine.CreateSnapshot(bool)"/>.
/// </summary>
internal struct CdgSnapshotStatistics
{
    /// <summary>Gets or sets the number of snapshot objects created.</summary>
    public long SnapshotsCreated { get; set; }

    /// <summary>Gets or sets the number of snapshots that copied the indexed frame.</summary>
    public long ColorIndexCopies { get; set; }

    /// <summary>Gets or sets the number of requests answered with the previous snapshot instance.</summary>
    public long SnapshotsReused { get; set; }

    /// <summary>Gets or sets the number of palette arrays copied (unchanged palettes are shared).</summary>
    public long PaletteCopies { get; set; }

    /// <summary>Gets or sets the total color-index bytes copied into snapshots.</summary>
    public long ColorIndexBytesCopied { get; set; }
}
