using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;

namespace Mantelview.Models;

public sealed class ImageCatalog
{
    private static readonly string[] DefaultSupportedImageExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
    ];

    private readonly object _sync = new();
    private readonly List<string> _paths;
    private readonly PlaybackMode _playbackMode;
    private readonly List<int> _randomOrder;
    private readonly int? _catalogLimit;
    private int _nextIndex;

    private ImageCatalog(string[] paths, PlaybackMode playbackMode, int? catalogLimit, bool isTruncated)
    {
        _paths = [.. paths];
        _playbackMode = playbackMode;
        _randomOrder = [.. Enumerable.Range(0, paths.Length)];
        _catalogLimit = catalogLimit;
        IsTruncated = isTruncated;

        if (_playbackMode == PlaybackMode.Random && _randomOrder.Count > 1)
        {
            ReshuffleRandomOrderLocked();
        }
    }

    public string FirstPath
    {
        get
        {
            lock (_sync)
            {
                return _paths[0];
            }
        }
    }

    public bool IsTruncated { get; }

    public int? CatalogLimit => _catalogLimit;

    public event EventHandler? CycleCompleted;

    public static bool ContainsSupportedImages(string folderPath)
    {
        return TryCountSupportedImages(folderPath, out var supportedImageCount, out _, unsafeFormats: null)
            && supportedImageCount > 0;
    }

    public static string[] GetSupportedImageExtensions(IReadOnlyList<string>? unsafeFormats = null)
    {
        var normalizedUnsafeFormats = SlideshowConfig.NormalizeUnsafeFormats(unsafeFormats);
        if (normalizedUnsafeFormats is null)
        {
            return [.. DefaultSupportedImageExtensions];
        }

        return
        [
            .. DefaultSupportedImageExtensions,
            .. normalizedUnsafeFormats.Select(static format => "." + format),
        ];
    }

    public bool TryGetNextPath([NotNullWhen(true)] out string? path)
    {
        EventHandler? cycleCompletedHandler = null;

        lock (_sync)
        {
            if (_paths.Count == 0)
            {
                path = null;
                return false;
            }

            path = _paths[ResolveCurrentPathIndexLocked()];
            _nextIndex++;

            if (_nextIndex >= _paths.Count)
            {
                _nextIndex = 0;

                if (_playbackMode == PlaybackMode.Random && _randomOrder.Count > 1)
                {
                    ReshuffleRandomOrderLocked();
                }

                cycleCompletedHandler = CycleCompleted;
            }
        }

        cycleCompletedHandler?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRetirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        EventHandler? cycleCompletedHandler = null;

        lock (_sync)
        {
            var pathIndex = _paths.FindIndex(candidate => string.Equals(candidate, path, StringComparison.Ordinal));
            if (pathIndex < 0)
            {
                return false;
            }

            _paths.RemoveAt(pathIndex);

            if (_playbackMode == PlaybackMode.Random)
            {
                RemoveRetiredPathFromRandomOrderLocked(pathIndex);
            }
            else if (pathIndex < _nextIndex)
            {
                _nextIndex--;
            }

            if (_paths.Count == 0)
            {
                _nextIndex = 0;
                return true;
            }

            if (_nextIndex >= _paths.Count)
            {
                _nextIndex = 0;

                if (_playbackMode == PlaybackMode.Random && _randomOrder.Count > 1)
                {
                    ReshuffleRandomOrderLocked();
                }

                cycleCompletedHandler = CycleCompleted;
            }
        }

        cycleCompletedHandler?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public static bool TryLoad(
        string folderPath,
        PlaybackMode playbackMode,
        [NotNullWhen(true)] out ImageCatalog? catalog,
        IReadOnlyList<string>? unsafeFormats = null,
        int? maxCatalogImages = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedLimit = SlideshowConfig.NormalizeMaxCatalogImages(maxCatalogImages);
            var isTruncated = false;
            var paths = normalizedLimit is int limit
                ? LoadPathsWithLimit(folderPath, unsafeFormats, limit, out isTruncated, cancellationToken)
                : LoadAllPaths(folderPath, unsafeFormats, cancellationToken);

            if (paths.Length == 0)
            {
                catalog = null;
                return false;
            }

            catalog = new ImageCatalog(
                paths,
                playbackMode,
                normalizedLimit,
                isTruncated);
            return true;
        }
        catch (IOException)
        {
            catalog = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            catalog = null;
            return false;
        }
    }

    public static bool TryCountSupportedImages(
        string folderPath,
        out int supportedImageCount,
        out bool catalogLimitReached,
        IReadOnlyList<string>? unsafeFormats = null,
        int? maxCatalogImages = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            supportedImageCount = 0;
            catalogLimitReached = false;
            var normalizedLimit = SlideshowConfig.NormalizeMaxCatalogImages(maxCatalogImages);

            foreach (var _ in EnumerateSupportedImagePaths(folderPath, unsafeFormats, cancellationToken))
            {
                supportedImageCount++;

                if (normalizedLimit is int limit && supportedImageCount > limit)
                {
                    supportedImageCount = limit;
                    catalogLimitReached = true;
                    return true;
                }
            }

            return true;
        }
        catch (IOException)
        {
            supportedImageCount = 0;
            catalogLimitReached = false;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            supportedImageCount = 0;
            catalogLimitReached = false;
            return false;
        }
    }

    public static string FormatCatalogLimitMessage(int catalogLimit)
    {
        return $"Folder contains more than {catalogLimit:N0} supported images. Playback will use the first {catalogLimit:N0} in Mantelview's filename order.";
    }

    private static bool IsSupportedImagePath(string path, IReadOnlySet<string> supportedExtensions)
    {
        return supportedExtensions.Contains(Path.GetExtension(path));
    }

    private static string[] LoadAllPaths(
        string folderPath,
        IReadOnlyList<string>? unsafeFormats,
        CancellationToken cancellationToken)
    {
        return EnumerateSupportedImagePaths(folderPath, unsafeFormats, cancellationToken)
            .Select(path => new OrderedImagePath(path, Path.GetRelativePath(folderPath, path)))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => entry.Path)
            .ToArray();
    }

    private static string[] LoadPathsWithLimit(
        string folderPath,
        IReadOnlyList<string>? unsafeFormats,
        int catalogLimit,
        out bool isTruncated,
        CancellationToken cancellationToken)
    {
        var priorityQueue = new PriorityQueue<OrderedImagePath, OrderedImagePath>(CatalogLimitComparer.Instance);
        var queueCapacity = catalogLimit == int.MaxValue
            ? int.MaxValue
            : catalogLimit + 1;

        foreach (var path in EnumerateSupportedImagePaths(folderPath, unsafeFormats, cancellationToken))
        {
            var orderedPath = new OrderedImagePath(path, Path.GetRelativePath(folderPath, path));

            if (priorityQueue.Count < queueCapacity)
            {
                priorityQueue.Enqueue(orderedPath, orderedPath);
                continue;
            }

            if (CatalogOrderComparer.Instance.Compare(orderedPath, priorityQueue.Peek()) >= 0)
            {
                continue;
            }

            _ = priorityQueue.Dequeue();
            priorityQueue.Enqueue(orderedPath, orderedPath);
        }

        var orderedPaths = new OrderedImagePath[priorityQueue.Count];
        for (var index = 0; index < orderedPaths.Length; index++)
        {
            orderedPaths[index] = priorityQueue.Dequeue();
        }

        Array.Sort(orderedPaths, CatalogOrderComparer.Instance);

        isTruncated = orderedPaths.Length > catalogLimit;
        if (isTruncated)
        {
            Array.Resize(ref orderedPaths, catalogLimit);
        }

        return orderedPaths
            .Select(static entry => entry.Path)
            .ToArray();
    }

    private int ResolveCurrentPathIndexLocked()
    {
        return _playbackMode == PlaybackMode.Random
            ? _randomOrder[_nextIndex]
            : _nextIndex;
    }

    private void RemoveRetiredPathFromRandomOrderLocked(int retiredPathIndex)
    {
        var retiredOrderIndex = _randomOrder.IndexOf(retiredPathIndex);
        if (retiredOrderIndex >= 0)
        {
            _randomOrder.RemoveAt(retiredOrderIndex);

            if (retiredOrderIndex < _nextIndex)
            {
                _nextIndex--;
            }
        }

        for (var index = 0; index < _randomOrder.Count; index++)
        {
            if (_randomOrder[index] > retiredPathIndex)
            {
                _randomOrder[index]--;
            }
        }
    }

    private void ReshuffleRandomOrderLocked()
    {
        if (_randomOrder.Count <= 1)
        {
            return;
        }

        var previousOrder = _randomOrder.ToArray();

        do
        {
            for (var index = _randomOrder.Count - 1; index > 0; index--)
            {
                var swapIndex = Random.Shared.Next(index + 1);
                (_randomOrder[index], _randomOrder[swapIndex]) = (_randomOrder[swapIndex], _randomOrder[index]);
            }
        }
        while (OrdersMatch(_randomOrder, previousOrder));
    }

    private static bool OrdersMatch(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> EnumerateSupportedImagePaths(
        string folderPath,
        IReadOnlyList<string>? unsafeFormats,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var supportedExtensions = new HashSet<string>(
            GetSupportedImageExtensions(unsafeFormats),
            StringComparer.OrdinalIgnoreCase);

        var childEntries = Directory.EnumerateDirectories(folderPath)
                .Where(ShouldTraverseDirectory)
                .Select(path => new DirectoryEntry(path, IsDirectory: true))
            .Concat(
                Directory.EnumerateFiles(folderPath)
                    .Where(path => IsSupportedImagePath(path, supportedExtensions))
                    .Select(path => new DirectoryEntry(path, IsDirectory: false)))
            .OrderBy(entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => Path.GetFileName(entry.Path), StringComparer.Ordinal);

        foreach (var childEntry in childEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (childEntry.IsDirectory)
            {
                string[] nestedPaths;
                try
                {
                    nestedPaths = EnumerateSupportedImagePaths(childEntry.Path, unsafeFormats, cancellationToken).ToArray();
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var nestedPath in nestedPaths)
                {
                    yield return nestedPath;
                }

                continue;
            }

            yield return childEntry.Path;
        }
    }

    private static bool ShouldTraverseDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct DirectoryEntry(string Path, bool IsDirectory);

    private readonly record struct OrderedImagePath(string Path, string RelativePath);

    private sealed class CatalogOrderComparer : IComparer<OrderedImagePath>
    {
        public static CatalogOrderComparer Instance { get; } = new();

        public int Compare(OrderedImagePath left, OrderedImagePath right)
        {
            var caseInsensitiveComparison = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
            return caseInsensitiveComparison != 0
                ? caseInsensitiveComparison
                : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        }
    }

    private sealed class CatalogLimitComparer : IComparer<OrderedImagePath>
    {
        public static CatalogLimitComparer Instance { get; } = new();

        public int Compare(OrderedImagePath left, OrderedImagePath right)
        {
            return CatalogOrderComparer.Instance.Compare(right, left);
        }
    }
}
