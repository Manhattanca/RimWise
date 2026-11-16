using RimWise.Integration.Marketplace;
using RimWise.Integration.Safety;

// Run this trusted tool from the base branch in CI. Author branches supply JSON
// data only; never compile their source or execute their workflows during review.
try
{
    if (args.Length is < 2 or > 3 || args[0] is not ("build" or "validate-submission"))
        throw new ArgumentException("Usage: build <repository-root> | validate-submission <base-root> <candidate-root>");
    var root = ScopedPaths.Ordinary(Path.GetFullPath(args[1]), true);
    if (args[0] == "build")
    {
        var manifests = ReadManifests(root);
        _ = ReadImages(root, manifests);
        var catalogue = Catalogue(manifests);
        var index = ScopedPaths.Ordinary(Path.Combine(root, "marketplace", "index.json"));
        var bytes = MarketplaceJson.SerializeCatalogue(catalogue);
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        var temporary = index + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, index, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine($"Validated and indexed {catalogue.Entries.Count} marketplace versions.");
    }
    else
    {
        if (args.Length != 3) throw new ArgumentException("Candidate repository root required.");
        var candidateRoot = ScopedPaths.Ordinary(Path.GetFullPath(args[2]), true);
        var before = ReadManifests(root);
        var after = ReadManifests(candidateRoot);
        var beforeImages = ReadImages(root, before);
        var afterImages = ReadImages(candidateRoot, after);
        foreach (var (path, bytes) in beforeImages)
            if (afterImages.TryGetValue(path, out var next) && bytes != next)
                throw new InvalidDataException("Published images are immutable.");
        foreach (var (path, record) in before)
            if (after.TryGetValue(path, out var next) && !record.Bytes.AsSpan().SequenceEqual(next.Bytes))
                throw new InvalidDataException("Published manifests are immutable; publish a new version instead.");
        var additions = after.Where(pair => !before.ContainsKey(pair.Key)).ToArray();
        var deletions = before.Where(pair => !after.ContainsKey(pair.Key)).ToArray();
        if (additions.Length == 0 && deletions.Length == 0 || additions.Length > 0 && deletions.Length > 0)
            throw new InvalidDataException("Submit new versions or request removal separately.");
        var actor = Environment.GetEnvironmentVariable("MARKETPLACE_AUTHOR");
        if (string.IsNullOrWhiteSpace(actor)) throw new InvalidDataException("Trusted PR author identity is required.");
        var changes = additions.Concat(deletions).ToArray();
        foreach (var change in changes)
            if (!change.Value.Manifest.Author.Equals(actor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest author must match the submitting GitHub account.");
        if (deletions.Length > 0)
        {
            var id = deletions[0].Value.Manifest.Id;
            if (deletions.Any(pair => pair.Value.Manifest.Id != id))
                throw new InvalidDataException("Removal must cover versions of exactly one order.");
        }
        // A trusted list of every changed repository path prevents disguised source,
        // workflow or catalogue edits. Both author identity and exact scope are required.
        if (!ChangedPaths().ToHashSet(StringComparer.Ordinal).SetEquals(changes.Select(pair => pair.Key).Concat(afterImages.Keys.Except(beforeImages.Keys)).Concat(beforeImages.Keys.Except(afterImages.Keys))))
            throw new InvalidDataException("A marketplace submission may only change its declared manifest files.");
        _ = Catalogue(after);
        Console.WriteLine($"Validated {additions.Length} new versions and {deletions.Length} removals; awaiting maintainer review.");
    }
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or MarketplaceException or InvalidOperationException)
{
    // Do not print author-supplied content or paths in workflow output.
    Console.Error.WriteLine("Marketplace validation failed: " + ex.GetType().Name + ". Inspect the submitted JSON and schema.");
    return 1;
}

static Dictionary<string, (MarketplaceManifest Manifest, byte[] Bytes)> ReadManifests(string root)
{
    var result = new Dictionary<string, (MarketplaceManifest, byte[])>(StringComparer.Ordinal);
    var folder = ScopedPaths.Ordinary(Path.Combine(root, "marketplace", "manifests"));
    if (!Directory.Exists(folder)) return result;
    var pending = new Stack<string>();
    pending.Push(folder);
    while (pending.TryPop(out var current))
    {
        ScopedPaths.Ordinary(current, true);
        foreach (var directory in Directory.EnumerateDirectories(current))
        {
            ScopedPaths.Ordinary(directory, true);
            if (Path.GetRelativePath(folder, directory).Split(Path.DirectorySeparatorChar).Length > 2)
                throw new InvalidDataException("Manifest directory depth exceeds the schema.");
            pending.Push(directory);
        }
        foreach (var path in Directory.EnumerateFiles(current))
        {
            ScopedPaths.Ordinary(path, true);
            if (!path.EndsWith(".json", StringComparison.Ordinal) || new FileInfo(path).Length > MarketplaceJson.MaximumManifestBytes)
                throw new InvalidDataException("Unexpected marketplace file type or size.");
            if (result.Count >= MarketplaceJson.MaximumEntries) throw new InvalidDataException("Too many versions.");
            var bytes = File.ReadAllBytes(path);
            var manifest = MarketplaceJson.ParseManifest(bytes);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative != MarketplaceJson.GetManifestPath(manifest.Author, manifest.Id, manifest.Version))
                throw new InvalidDataException("Manifest path does not match its author and version.");
            result.Add(relative, (manifest, bytes));
        }
    }
    return result;
}

static Dictionary<string, string> ReadImages(string root, Dictionary<string, (MarketplaceManifest Manifest, byte[] Bytes)> manifests)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var record in manifests.Values)
        foreach (var image in record.Manifest.Images)
        {
            var file = ScopedPaths.Child(root, image.Path.Replace('/', Path.DirectorySeparatorChar));
            ScopedPaths.Ordinary(file, true);
            if (new FileInfo(file).Length != image.Bytes) throw new InvalidDataException("Image length mismatch.");
            var bytes = File.ReadAllBytes(file); MarketplaceImages.Verify(image, bytes); result.Add(image.Path, image.Sha256);
        }
    var folder = ScopedPaths.Ordinary(Path.Combine(root, "marketplace", "images"));
    if (Directory.Exists(folder))
    {
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out var current))
        {
            ScopedPaths.Ordinary(current, true);
            if (Path.GetRelativePath(folder, current).Split(Path.DirectorySeparatorChar).Length > 3) throw new InvalidDataException("Image directory too deep.");
            foreach (var child in Directory.EnumerateDirectories(current)) { ScopedPaths.Ordinary(child, true); pending.Push(child); }
            foreach (var file in Directory.EnumerateFiles(current))
            { ScopedPaths.Ordinary(file, true); if (!result.ContainsKey(Path.GetRelativePath(root, file).Replace('\\', '/'))) throw new InvalidDataException("Undeclared image."); }
        }
    }
    return result;
}

static MarketplaceCatalogue Catalogue(Dictionary<string, (MarketplaceManifest Manifest, byte[] Bytes)> files)
{
    // Hash exact bytes, allowing harmless author JSON whitespace without changing content.
    var entries = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
    {
        var m = pair.Value.Manifest;
        return new MarketplaceEntry(m.Id, m.Version, m.Title, m.Author, m.Description, m.GameVersion,
            m.RequiredDlc, m.WorkshopCollectionId, pair.Key, MarketplaceJson.ComputeSha256(pair.Value.Bytes))
            { ModCount = m.Mods.Count, ReleaseNotes = m.ReleaseNotes, Images = m.Images };
    }).ToArray();
    var result = new MarketplaceCatalogue(1, entries);
    _ = MarketplaceJson.SerializeCatalogue(result);
    return result;
}

static IEnumerable<string> ChangedPaths()
{
    // The candidate is a checkout of the same PR base, avoiding .git comparison.
    // Use the PR's changed path list provided by the trusted workflow instead.
    var changed = Environment.GetEnvironmentVariable("MARKETPLACE_CHANGED_PATHS_FILE");
    if (string.IsNullOrEmpty(changed)) throw new InvalidDataException("Trusted changed path list required.");
    ScopedPaths.Ordinary(changed, true);
    return File.ReadAllText(changed).Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.TrimEnd('\r', '\n')).ToArray();
}
