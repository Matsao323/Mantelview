using System.IO;
using System.Linq;
using Mantelview.Models;

namespace Mantelview.Services;

public static class FolderSelectionEvaluator
{
    public const string BlockedFolderMessage = "This folder cannot be used.";
    public const string FolderNotAccessibleMessage = "Folder not found or not accessible.";

    public static FolderSelectionEvaluation Evaluate(SlideshowConfig config)
    {
        var folderPath = config.FolderPath;
        if (SlideshowConfig.IsBlockedPath(folderPath))
        {
            return new FolderSelectionEvaluation(IsValid: false, Message: BlockedFolderMessage, IsError: true);
        }

        if (!Directory.Exists(folderPath))
        {
            return new FolderSelectionEvaluation(IsValid: false, Message: FolderNotAccessibleMessage, IsError: true);
        }

        if (!ImageCatalog.TryCountSupportedImages(
                folderPath,
                out var supportedImageCount,
                out var catalogLimitReached,
                config.UnsafeFormats,
                config.MaxCatalogImages))
        {
            return new FolderSelectionEvaluation(IsValid: false, Message: FolderNotAccessibleMessage, IsError: true);
        }

        if (catalogLimitReached && config.MaxCatalogImages is int maxCatalogImages)
        {
            return new FolderSelectionEvaluation(IsValid: true, Message: ImageCatalog.FormatCatalogLimitMessage(maxCatalogImages), IsError: false);
        }

        return supportedImageCount == 0
            ? new FolderSelectionEvaluation(IsValid: false, Message: GetNoSupportedImagesMessage(config.UnsafeFormats), IsError: true)
            : new FolderSelectionEvaluation(IsValid: true, Message: FormatImageCount(supportedImageCount), IsError: false);
    }

    private static string FormatImageCount(int imageCount)
    {
        return imageCount == 1
            ? "1 image"
            : $"{imageCount} images";
    }

    private static string GetNoSupportedImagesMessage(string[]? unsafeFormats)
    {
        var extensionLabels = ImageCatalog.GetSupportedImageExtensions(unsafeFormats)
            .Select(static extension => extension.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "JPEG",
                ".png" => "PNG",
                ".webp" => "WebP",
                ".bmp" => "BMP",
                ".gif" => "GIF",
                _ => extension.TrimStart('.').ToUpperInvariant(),
            })
            .Distinct();

        return $"No supported images found ({string.Join(", ", extensionLabels)}).";
    }
}

public readonly record struct FolderSelectionEvaluation(bool IsValid, string Message, bool IsError);
