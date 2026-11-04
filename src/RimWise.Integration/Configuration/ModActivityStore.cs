using RimWise.Core;

namespace RimWise.Integration.Configuration;

/// <summary>Tracks first-seen and observed update times in the application's own data directory.</summary>
public sealed class ModActivityStore(string applicationDirectory)
{
    private readonly AppJsonFile<StoredActivity> file = new(applicationDirectory, "mod-activity", 16 * 1024 * 1024);
    private const int MaximumRecords = 20_000;

    public Dictionary<string, ModActivityRecord> Load()
    {
        var stored = ReadStored();
        return Records(stored);
    }

    private StoredActivity? ReadStored()
    {
        var stored = file.Load();
        if (stored is not null && (stored.SchemaVersion is not (1 or 2) || stored.Records is null))
            throw new InvalidDataException("近期模组记录版本不受支持。");
        return stored;
    }

    private static Dictionary<string, ModActivityRecord> Records(StoredActivity? stored) =>
        (stored?.Records ?? new Dictionary<string, ModActivityRecord>(StringComparer.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && item.Value is not null)
            .OrderByDescending(item => item.Value.LastSeenUtc).Take(MaximumRecords)
            .ToDictionary(item => Normalize(item.Key), item => item.Value, StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ModActivityRecord> Observe(IEnumerable<ModInstallation> installations, DateTimeOffset now,
        IEnumerable<string>? scannedRoots = null)
    {
        var stored = ReadStored();
        var records = Records(stored);

        // Each newly configured library gets its own baseline. Existing/legacy records cannot
        // establish a player's addition sequence, so do not treat first launch as an install event.
        var baselineRoots = (stored?.BaselineRoots ?? records.Keys.Select(Root).ToArray())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = installations.Where(mod => mod.Source != ModSource.Game).ToArray();
        var observedRoots = observed.Select(mod => Root(mod.Directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        observedRoots.UnionWith((scannedRoots ?? []).Select(Normalize));

        foreach (var installation in observed)
        {
            var key = Normalize(installation.Directory);
            var observedWrite = installation.LastUpdatedUtc ?? DateTimeOffset.MinValue;
            if (!records.TryGetValue(key, out var current))
            {
                records[key] = new ModActivityRecord(key, now, now, observedWrite, observedWrite)
                { AddedUtc = baselineRoots.Contains(Root(key)) ? now : null };
                continue;
            }

            var lastUpdated = installation.LastUpdatedUtc ?? current.LastUpdatedUtc;
            records[key] = current with { LastSeenUtc = now, LastUpdatedUtc = lastUpdated, LastObservedWriteUtc = observedWrite };
        }

        if (records.Count > MaximumRecords)
            records = records.OrderByDescending(item => item.Value.LastSeenUtc).Take(MaximumRecords)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

        baselineRoots.UnionWith(observedRoots);
        file.Save(new StoredActivity(2, records, baselineRoots.ToArray()));
        return records;
    }

    private static string Normalize(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (ArgumentException) { return value.Trim(); }
    }

    private static string Root(string directory) => Path.GetDirectoryName(Normalize(directory)) ?? Normalize(directory);

    private sealed record StoredActivity(int SchemaVersion, Dictionary<string, ModActivityRecord> Records, string[]? BaselineRoots = null);
}

public sealed record ModActivityRecord(string Directory, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc,
    DateTimeOffset LastUpdatedUtc, DateTimeOffset LastObservedWriteUtc)
{
    public DateTimeOffset? AddedUtc { get; init; }
}
