using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Api
{
    internal class SweeperFile
    {
        public string FilePath { get; set; }
        public int MaxDimension { get; set; }
    }

    [ApiController]
    [Route("ImageOptimizer")]
    public class ImageOptimizerController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly IProviderManager _providerManager;

        private static readonly object _manifestLock = new object();
        private static readonly object _registryLock = new object();

        private int _sweeperScanLimit = 200; // Dynamic scale target limit to resolve premature 200 file cap

        private static readonly string[] UnnecessaryTrickplayParentFolders = new[]
        {
            "behind the scenes", "deleted scenes", "interviews", "scenes", 
            "samples", "shorts", "featurettes", "clips", "other", "extras", "trailers"
        };

        public ImageOptimizerController(ILibraryManager libraryManager, IApplicationPaths appPaths, IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _providerManager = providerManager;
        }

        [HttpGet("Status")]
        public IActionResult GetStatus()
        {
            try
            {
                var status = ImageOptimizerService.GetInitializationStatus();
                return Ok(status);
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed retrieving plugin status", ex);
                return StatusCode(500, new { Success = false, ErrorMessage = ex.Message });
            }
        }

        [HttpGet("SmartlistsExists")]
        public IActionResult GetSmartlistsExists()
        {
            try
            {
                var smartlistsPath = ResolveDirectoryPath("data", "smartlists");
                return Ok(new { Exists = System.IO.Directory.Exists(smartlistsPath) });
            }
            catch
            {
                return Ok(new { Exists = false });
            }
        }

        private string ResolveDirectoryPath(params string[] segments)
        {
            var path = System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, System.IO.Path.Combine(segments));
            if (System.IO.Directory.Exists(path))
            {
                return path;
            }

            var parent = System.IO.Path.GetDirectoryName(_appPaths.ConfigurationDirectoryPath);
            if (!string.IsNullOrEmpty(parent) && !parent.Equals("/", StringComparison.Ordinal) && !parent.Equals("\\", StringComparison.Ordinal))
            {
                var parentPath = System.IO.Path.Combine(parent, System.IO.Path.Combine(segments));
                if (System.IO.Directory.Exists(parentPath))
                {
                    return parentPath;
                }
            }

            var progDataPath = System.IO.Path.Combine(_appPaths.ProgramDataPath, System.IO.Path.Combine(segments));
            if (System.IO.Directory.Exists(progDataPath))
            {
                return progDataPath;
            }

            return path;
        }

        [HttpGet("Libraries")]
        public IActionResult GetLibraries()
        {
            try
            {
                var folders = _libraryManager.GetVirtualFolders();
                var result = folders.Select(f => new { f.Name }).ToList();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("Metadata/Search")]
        public async Task<IActionResult> SearchMetadata([FromQuery] string query, [FromQuery] string imdbId = null)
        {
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(imdbId))
            {
                return BadRequest("Search query or IMDb ID must be provided.");
            }

            try
            {
                var searchInfo = new MovieInfo { Name = query ?? string.Empty };
                if (!string.IsNullOrWhiteSpace(imdbId))
                {
                    searchInfo.ProviderIds["Imdb"] = imdbId;
                }

                var searchQuery = new RemoteSearchQuery<MovieInfo> { SearchInfo = searchInfo };
                var results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(searchQuery, CancellationToken.None);
                
                var list = new System.Collections.Generic.List<object>();
                foreach (var r in results)
                {
                    if (!string.IsNullOrEmpty(r.ImageUrl))
                    {
                        list.Add(new
                        {
                            Name = r.Name,
                            Year = r.ProductionYear,
                            ImageUrl = r.ImageUrl,
                            ProviderIds = r.ProviderIds
                        });
                    }
                }

                return Ok(list);
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Metadata provider search failed", ex);
                return StatusCode(500, "Search failed: " + ex.Message);
            }
        }

        [HttpPost("Metadata/Test")]
        public async Task<IActionResult> TestMetadataOptimization([FromQuery] string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return BadRequest("Image URL cannot be empty.");
            }

            try
            {
                using var client = new HttpClient();
                byte[] originalBytes = await client.GetByteArrayAsync(imageUrl);

                string contentType = "image/jpeg";
                if (imageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase)) contentType = "image/png";
                else if (imageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase)) contentType = "image/webp";
                else if (imageUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase)) contentType = "image/gif";

                int originalWidth = 0;
                int originalHeight = 0;
                try
                {
                    // Correction: Replaced custom SKData structures with universally compliant SKMemoryStream
                    using var stream = new SKMemoryStream(originalBytes);
                    using var codec = SKCodec.Create(stream);
                    if (codec != null)
                    {
                        originalWidth = codec.Info.Width;
                        originalHeight = codec.Info.Height;
                    }
                }
                catch { }

                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;
                
                var stopwatch = Stopwatch.StartNew();
                using var optimizedData = optimizer.OptimizeMetadataImage(originalBytes, contentType, out string newContentType);
                stopwatch.Stop();
                long processingTimeMs = stopwatch.ElapsedMilliseconds;

                byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                int optimizedWidth = 0;
                int optimizedHeight = 0;
                double fidelityScore = 100.0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    using var optimized = SKBitmap.Decode(optimizedBytes);
                    if (original != null && optimized != null)
                    {
                        optimizedWidth = optimized.Width;
                        optimizedHeight = optimized.Height;
                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(original, optimized);
                    }
                }
                catch { }

                string originalBase64 = Convert.ToBase64String(originalBytes);
                string optimizedBase64 = Convert.ToBase64String(optimizedBytes);

                return Ok(new
                {
                    OriginalSize = originalBytes.Length,
                    OptimizedSize = optimizedBytes.Length,
                    OriginalType = contentType,
                    OptimizedType = newContentType,
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    OptimizedWidth = optimizedWidth,
                    OptimizedHeight = optimizedHeight,
                    OriginalBase64 = "data:" + contentType + ";base64," + originalBase64,
                    OptimizedBase64 = "data:" + newContentType + ";base64," + optimizedBase64,
                    
                    JpegQuality = config.JpegQuality,
                    JpegProgressive = config.JpegProgressive,
                    WebpQuality = config.WebpQuality,
                    WebpLossless = config.WebpLossless,
                    PngCompressionLevel = config.PngCompressionLevel,
                    
                    ProcessingTimeMs = processingTimeMs,
                    FidelityScore = fidelityScore
                });
            }
            catch (Exception ex) {
                PluginLogger.LogError("Metadata sandbox execution failure", ex);
                return StatusCode(500, "Operation failed: " + ex.Message);
            }
        }

        [HttpPost("Test")]
        public async Task<IActionResult> TestOptimization([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No test file supplied.");
            }

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                byte[] originalBytes = ms.ToArray();
                string contentType = file.ContentType;

                int originalWidth = 0;
                int originalHeight = 0;
                try
                {
                    // Correction: Replaced custom SKData structures with universally compliant SKMemoryStream
                    using var stream = new SKMemoryStream(originalBytes);
                    using var codec = SKCodec.Create(stream);
                    if (codec != null)
                    {
                        originalWidth = codec.Info.Width;
                        originalHeight = codec.Info.Height;
                    }
                }
                catch { }

                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;
                
                var stopwatch = Stopwatch.StartNew();
                using var optimizedData = optimizer.OptimizeMetadataImage(originalBytes, contentType, out string newContentType);
                stopwatch.Stop();
                long processingTimeMs = stopwatch.ElapsedMilliseconds;

                byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                int optimizedWidth = 0;
                int optimizedHeight = 0;
                double fidelityScore = 100.0;
                try
                {
                    using var original = SKBitmap.Decode(originalBytes);
                    using var optimized = SKBitmap.Decode(optimizedBytes);
                    if (original != null && optimized != null)
                    {
                        optimizedWidth = optimized.Width;
                        optimizedHeight = optimized.Height;
                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(original, optimized);
                    }
                }
                catch { }

                string originalBase64 = Convert.ToBase64String(originalBytes);
                string optimizedBase64 = Convert.ToBase64String(optimizedBytes);

                return Ok(new
                {
                    OriginalSize = originalBytes.Length,
                    OptimizedSize = optimizedBytes.Length,
                    OriginalType = contentType,
                    OptimizedType = newContentType,
                    OriginalWidth = originalWidth,
                    OriginalHeight = originalHeight,
                    OptimizedWidth = optimizedWidth,
                    OptimizedHeight = optimizedHeight,
                    OriginalBase64 = "data:" + contentType + ";base64," + originalBase64,
                    OptimizedBase64 = "data:" + newContentType + ";base64," + optimizedBase64,
                    
                    JpegQuality = config.JpegQuality,
                    JpegProgressive = config.JpegProgressive,
                    WebpQuality = config.WebpQuality,
                    WebpLossless = config.WebpLossless,
                    PngCompressionLevel = config.PngCompressionLevel,
                    
                    ProcessingTimeMs = processingTimeMs,
                    FidelityScore = fidelityScore
                });
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Manual sandbox optimization pipeline failure", ex);
                return StatusCode(500, "Operation failed: " + ex.Message);
            }
        }

        private string GetActiveBackupRoot()
        {
            var config = Plugin.Instance.Configuration;
            if (!string.IsNullOrWhiteSpace(config.BackupFolderPath) && System.IO.Directory.Exists(config.BackupFolderPath))
            {
                return config.BackupFolderPath;
            }
            return System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, "ImageOptimizerBackups");
        }

        [HttpPost("Path/Test")]
        public IActionResult TestPathAccess([FromQuery] string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest("Path cannot be empty.");
            }

            try
            {
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }

                string testFile = System.IO.Path.Combine(path, "irukandji_test_" + Guid.NewGuid().ToString() + ".tmp");
                System.IO.File.WriteAllText(testFile, "test");
                System.IO.File.Delete(testFile);

                return Ok(new { Success = true });
            }
            catch (Exception ex)
            {
                return Ok(new { Success = false, ErrorMessage = ex.Message });
            }
        }

        private string SanitizeInput(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (char c in input)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == ' ' || c == ',' || c == '.' || c == '/' || c == '\\')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        [HttpPost("Sweeper/Run")]
        public IActionResult RunSweeper([FromQuery] bool dryRun, [FromQuery] bool backup, [FromQuery] int startIndex = 0)
        {
            try
            {
                var optimizer = new ImageOptimizerService();
                var config = Plugin.Instance.Configuration;

                if (!config.EnableMetadataAvatarTranscoding && !dryRun)
                {
                    return BadRequest("Live write sweeps are disabled because Metadata/Avatar Transcoding is currently turned off in your configuration.");
                }

                _sweeperScanLimit = dryRun ? (startIndex + 200) : 200;

                var results = new ConcurrentBag<object>();

                long totalOriginal = 0;
                long totalOptimized = 0;

                string registryRoot = GetActiveBackupRoot();
                string registryPath = System.IO.Path.Combine(registryRoot, "optimized_registry.txt");
                var optimizedFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (System.IO.File.Exists(registryPath))
                {
                    try
                    {
                        var lines = System.IO.File.ReadAllLines(registryPath);
                        foreach (var line in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                optimizedFiles.Add(line.Trim());
                            }
                        }
                    }
                    catch { }
                }

                string currentBackupDir = null;
                string manifestPath = null;
                if (!dryRun && backup)
                {
                    System.IO.Directory.CreateDirectory(registryRoot);
                    string dateStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    currentBackupDir = System.IO.Path.Combine(registryRoot, "sweep_" + dateStr);
                    System.IO.Directory.CreateDirectory(currentBackupDir);
                    manifestPath = System.IO.Path.Combine(currentBackupDir, "manifest.txt");
                }

                var imageFiles = new System.Collections.Generic.List<SweeperFile>();
                long totalPurgedBytes = 0;
                var purgedFoldersWithSizes = new System.Collections.Generic.List<(string Path, long Size)>();

                // #1: Metadata
                if (config.ProcessMetadata)
                {
                    string path = ResolveDirectoryPath("metadata");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #2: Collections
                if (config.ProcessCollections)
                {
                    string path = ResolveDirectoryPath("data", "collections");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #3: Playlists
                if (config.ProcessPlaylists)
                {
                    string path = ResolveDirectoryPath("data", "playlists");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #4: Smartlists
                if (config.ProcessSmartlists)
                {
                    string path = ResolveDirectoryPath("data", "smartlists");
                    if (System.IO.Directory.Exists(path))
                    {
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #5: Library covers
                if (config.ProcessLibraryCovers)
                {
                    string path = ResolveDirectoryPath("root", "default");
                    if (System.IO.Directory.Exists(path))
                    {
                        int coverDim = config.ResizeLibraryCovers ? config.LibraryCoversWidth : config.MaxMetadataDimension;
                        AddImagesFromDirectory(path, imageFiles, optimizedFiles, coverDim);
                    }
                }

                // #6: User profile pics
                if (config.ProcessUserProfiles)
                {
                    var path1 = System.IO.Path.Combine(_appPaths.ConfigurationDirectoryPath, "users");
                    if (System.IO.Directory.Exists(path1))
                    {
                        AddImagesFromDirectory(path1, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }

                    var parent = System.IO.Path.GetDirectoryName(_appPaths.ConfigurationDirectoryPath);
                    if (!string.IsNullOrEmpty(parent) && !parent.Equals("/") && !parent.Equals("\\"))
                    {
                        var path2 = System.IO.Path.Combine(parent, "users");
                        if (System.IO.Directory.Exists(path2) && !path2.Equals(path1, StringComparison.OrdinalIgnoreCase))
                        {
                            AddImagesFromDirectory(path2, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                        }
                    }

                    var pathAlt1 = "/config/config/users";
                    if (System.IO.Directory.Exists(pathAlt1) && !pathAlt1.Equals(path1, StringComparison.OrdinalIgnoreCase))
                    {
                        AddImagesFromDirectory(pathAlt1, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }
                    var pathAlt2 = "/config/users";
                    if (System.IO.Directory.Exists(pathAlt2) && !pathAlt2.Equals(path1, StringComparison.OrdinalIgnoreCase) && !pathAlt2.Equals(pathAlt1, StringComparison.OrdinalIgnoreCase))
                    {
                        AddImagesFromDirectory(pathAlt2, imageFiles, optimizedFiles, config.MaxAvatarDimension);
                    }
                }

                // #7: Trickplay
                if (config.ProcessTrickplay)
                {
                    string internalTrickplayPath = ResolveDirectoryPath("data", "trickplay");
                    if (System.IO.Directory.Exists(internalTrickplayPath))
                    {
                        AddImagesFromDirectory(internalTrickplayPath, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }

                    string legacyTrickplayPath = ResolveDirectoryPath("metadata", "library");
                    if (System.IO.Directory.Exists(legacyTrickplayPath))
                    {
                        ScanLegacyInternalTrickplay(legacyTrickplayPath, imageFiles, optimizedFiles, config.MaxMetadataDimension);
                    }
                }

                // #8: Media Libraries
                var virtualFolders = _libraryManager.GetVirtualFolders();
                var includedLibs = (config.IncludedLibraries ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .ToList();

                foreach (var folder in virtualFolders)
                {
                    bool isLibrarySelectedForMediaFiles = includedLibs.Contains(folder.Name, StringComparer.OrdinalIgnoreCase);
                    bool isTrickplaySelected = config.ProcessTrickplay;

                    if (!isLibrarySelectedForMediaFiles && !isTrickplaySelected)
                    {
                        continue;
                    }

                    foreach (var path in folder.Locations)
                    {
                        if (System.IO.Directory.Exists(path))
                        {
                            ScanMediaLibrary(path, imageFiles, optimizedFiles, isLibrarySelectedForMediaFiles, isTrickplaySelected, config.PurgeTrickplay, ref totalPurgedBytes, purgedFoldersWithSizes, dryRun, config.MaxMetadataDimension);
                        }
                    }
                }

                int skipOffset = dryRun ? startIndex : 0;
                var filesToProcess = imageFiles.Skip(skipOffset).Take(50).ToList();
                int processLimit = filesToProcess.Count;

                Parallel.For(0, processLimit, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                {
                    var sweeperFile = filesToProcess[i];
                    string filePath = sweeperFile.FilePath;
                    try
                    {
                        byte[] originalBytes;
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: false))
                        {
                            originalBytes = new byte[fs.Length];
                            fs.ReadExactly(originalBytes);
                        }

                        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                        string contentType = ext == ".png" ? "image/png" : (ext == ".gif" ? "image/gif" : (ext == ".webp" ? "image/webp" : "image/jpeg"));
                        
                        int origW = 0, origH = 0;
                        
                        // Correction: Replaced custom SKData structures with universally compliant SKMemoryStream
                        using (var stream = new SKMemoryStream(originalBytes))
                        using (var codec = SKCodec.Create(stream))
                        {
                            if (codec != null)
                            {
                                origW = codec.Info.Width;
                                origH = codec.Info.Height;
                            }
                        }

                        string newContentType;
                        using var optimizedData = optimizer.ProcessImage(originalBytes, contentType, sweeperFile.MaxDimension, config, out newContentType, isMetadata: true);
                        byte[] optimizedBytes = optimizedData != null ? optimizedData.ToArray() : originalBytes;

                        int optW = 0, optH = 0;
                        double fidelityScore = 100.0;
                        
                        long sizeDifferenceThreshold = (long)(originalBytes.Length * (config.MinImprovementPercentage / 100.0));
                        bool meetsImprovementThreshold = (originalBytes.Length - optimizedBytes.Length) >= sizeDifferenceThreshold;

                        if (meetsImprovementThreshold)
                        {
                            try
                            {
                                using (var origBitmap = SKBitmap.Decode(originalBytes))
                                using (var optBitmap = SKBitmap.Decode(optimizedBytes))
                                {
                                    if (origBitmap != null && optBitmap != null)
                                    {
                                        optW = optBitmap.Width;
                                        optH = optBitmap.Height;
                                        fidelityScore = ImageOptimizerService.CalculateFidelityScore(origBitmap, optBitmap);
                                    }
                                }
                            }
                            catch { }
                        }

                        bool isSaved = false;
                        if (!dryRun && meetsImprovementThreshold)
                        {
                            if (backup && currentBackupDir != null && manifestPath != null)
                            {
                                try
                                {
                                    string guidName = Guid.NewGuid().ToString() + ext;
                                    string backupFilePath = System.IO.Path.Combine(currentBackupDir, guidName);
                                    System.IO.File.WriteAllBytes(backupFilePath, originalBytes);

                                    lock (_manifestLock)
                                    {
                                        System.IO.File.AppendAllText(manifestPath, guidName + "|" + filePath + Environment.NewLine);
                                    }
                                }
                                catch (Exception bEx)
                                {
                                    PluginLogger.LogError("Failed to backup original file: " + filePath, bEx);
                                }
                            }

                            System.IO.File.WriteAllBytes(filePath, optimizedBytes);
                            isSaved = true;
                        }

                        if (!dryRun)
                        {
                            try
                            {
                                System.IO.Directory.CreateDirectory(registryRoot);
                                lock (_registryLock)
                                {
                                    System.IO.File.AppendAllText(registryPath, filePath + Environment.NewLine);
                                }
                            }
                            catch { }
                        }

                        if (meetsImprovementThreshold)
                        {
                            Interlocked.Add(ref totalOriginal, originalBytes.Length);
                            Interlocked.Add(ref totalOptimized, optimizedBytes.Length);
                        }

                        results.Add(new
                        {
                            Path = filePath,
                            OriginalSize = originalBytes.Length,
                            OptimizedSize = meetsImprovementThreshold ? optimizedBytes.Length : originalBytes.Length,
                            OriginalDim = origW + "x" + origH,
                            OptimizedDim = meetsImprovementThreshold ? (optW + "x" + optH) : (origW + "x" + origH),
                            ContentType = contentType,
                            NewContentType = meetsImprovementThreshold ? newContentType : contentType,
                            FidelityScore = meetsImprovementThreshold ? fidelityScore : 0.0,
                            SavedStatus = isSaved ? "Saved" : (meetsImprovementThreshold ? "DryRun" : "NotImprovedEnough"),
                            ReductionPercent = meetsImprovementThreshold ? Math.Round(((double)(originalBytes.Length - optimizedBytes.Length) / originalBytes.Length) * 100.0, 1) : 0.0
                        });
                    }
                    catch (Exception ex)
                    {
                        PluginLogger.LogError("Sweeper failed to process file sequentially: " + filePath, ex);
                    }
                });

                foreach (var pf in purgedFoldersWithSizes)
                {
                    results.Add(new
                    {
                        Path = pf.Path,
                        OriginalSize = pf.Size,
                        OptimizedSize = 0L,
                        OriginalDim = "N/A",
                        OptimizedDim = "Purged",
                        ContentType = "Folder (Trickplay)",
                        NewContentType = "None",
                        FidelityScore = 100.0,
                        SavedStatus = dryRun ? "DryRunPurged" : "Purged",
                        ReductionPercent = 100.0
                    });
                }

                totalOriginal += totalPurgedBytes;

                return Ok(new { Results = results, TotalOriginal = totalOriginal, TotalOptimized = totalOptimized, IsDryRun = dryRun });
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Sweeper execution failed", ex);
                return StatusCode(500, "Sweeper failed: " + ex.Message);
            }
        }

        private void AddImagesFromDirectory(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= _sweeperScanLimit) return;

            string[] files = null;
            try
            {
                files = System.IO.Directory.GetFiles(path, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch { }

            var config = Plugin.Instance.Configuration;
            string sw = SanitizeInput(config.BlacklistWords);
            var blacklist = !string.IsNullOrWhiteSpace(sw) ? sw.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (optimizedFiles.Contains(f)) continue;

                    if (blacklist.Length > 0)
                    {
                        bool blocked = false;
                        if (config.BlacklistUseAndOperator)
                        {
                            bool allMatched = true;
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (!m) { allMatched = false; break; }
                            }
                            blocked = allMatched;
                        }
                        else
                        {
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (m) { blocked = true; break; }
                            }
                        }

                        if (blocked)
                        {
                            try
                            {
                                lock (_registryLock)
                                {
                                    System.IO.File.AppendAllText(System.IO.Path.Combine(GetActiveBackupRoot(), "optimized_registry.txt"), f + Environment.NewLine);
                                }
                            }
                            catch { }
                            continue;
                        }
                    }

                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
                    {
                        fileList.Add(new SweeperFile { FilePath = f, MaxDimension = maxDim });
                        if (fileList.Count >= _sweeperScanLimit) return;
                    }
                }
            }

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim);
                    if (fileList.Count >= _sweeperScanLimit) return;
                }
            }
        }

        private void ScanLegacyInternalTrickplay(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= _sweeperScanLimit) return;

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(path, "*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    var dirName = System.IO.Path.GetFileName(sub);
                    if (dirName.Equals("trickplay", StringComparison.OrdinalIgnoreCase))
                    {
                        AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim);
                    }
                    else
                    {
                        ScanLegacyInternalTrickplay(sub, fileList, optimizedFiles, maxDim);
                    }
                }
            }
        }

        private void ScanMediaLibrary(
            string currentPath, 
            System.Collections.Generic.List<SweeperFile> fileList, 
            System.Collections.Generic.HashSet<string> optimizedFiles,
            bool isLibrarySelectedForMediaFiles,
            bool isTrickplaySelected,
            bool purgeTrickplay,
            ref long totalPurgedBytes,
            System.Collections.Generic.List<(string Path, long Size)> purgedFoldersWithSizes,
            bool dryRun,
            int maxDim)
        {
            if (fileList.Count >= _sweeperScanLimit) return;

            string[] subDirs = null;
            try
            {
                subDirs = System.IO.Directory.GetDirectories(currentPath, "*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            if (subDirs != null)
            {
                foreach (var sub in subDirs)
                {
                    var dirName = System.IO.Path.GetFileName(sub);
                    bool isTrickplayDir = dirName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || 
                                         dirName.Equals("trickplay", StringComparison.OrdinalIgnoreCase);

                    if (isTrickplayDir)
                    {
                        if (isTrickplaySelected)
                        {
                            if (purgeTrickplay && IsUnnecessaryTrickplayFolder(sub))
                            {
                                long folderSize = GetDirectorySize(sub);
                                totalPurgedBytes += folderSize;
                                purgedFoldersWithSizes.Add((sub, folderSize));

                                if (!dryRun)
                                {
                                    try
                                    {
                                        System.IO.Directory.Delete(sub, recursive: true);
                                    }
                                    catch (Exception ex)
                                    {
                                        PluginLogger.LogError("Failed to purge trickplay folder: " + sub, ex);
                                    }
                                }
                                continue;
                            }

                            AddImagesFromDirectory(sub, fileList, optimizedFiles, maxDim);
                        }
                    }
                    else
                    {
                        ScanMediaLibrary(sub, fileList, optimizedFiles, isLibrarySelectedForMediaFiles, isTrickplaySelected, purgeTrickplay, ref totalPurgedBytes, purgedFoldersWithSizes, dryRun, maxDim);
                    }
                }
            }

            if (isLibrarySelectedForMediaFiles)
            {
                ScanLibraryFiles(currentPath, fileList, optimizedFiles, maxDim);
            }
        }

        private long GetDirectorySize(string folderPath)
        {
            long size = 0;
            try
            {
                var files = System.IO.Directory.GetFiles(folderPath, "*.*", System.IO.SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    size += new System.IO.FileInfo(f).Length;
                }
            }
            catch { }
            return size;
        }

        private bool IsUnnecessaryTrickplayFolder(string folderPath)
        {
            var folderName = System.IO.Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(folderName)) return false;

            bool isTrickplay = folderName.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || 
                               folderName.Equals("trickplay", StringComparison.OrdinalIgnoreCase);

            if (!isTrickplay) return false;

            if (folderName.EndsWith("-trailer.trickplay", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var segments = folderPath.Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                if (UnnecessaryTrickplayParentFolders.Contains(seg, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void ScanLibraryFiles(string path, System.Collections.Generic.List<SweeperFile> fileList, System.Collections.Generic.HashSet<string> optimizedFiles, int maxDim)
        {
            if (fileList.Count >= _sweeperScanLimit) return;

            string[] files = null;
            try
            {
                files = System.IO.Directory.GetFiles(path, "*.*", System.IO.SearchOption.TopDirectoryOnly);
            }
            catch { }

            var config = Plugin.Instance.Configuration;
            string sw = SanitizeInput(config.BlacklistWords);
            var blacklist = !string.IsNullOrWhiteSpace(sw) ? sw.Split(',', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();

            if (files != null)
            {
                foreach (var f in files)
                {
                    if (optimizedFiles.Contains(f)) continue;

                    if (blacklist.Length > 0)
                    {
                        bool blocked = false;
                        if (config.BlacklistUseAndOperator)
                        {
                            bool allMatched = true;
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (!m) { allMatched = false; break; }
                            }
                            blocked = allMatched;
                        }
                        else
                        {
                            foreach (var w in blacklist)
                            {
                                var tw = w.Trim(); if (string.IsNullOrEmpty(tw)) continue;
                                var comp = config.BlacklistCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                                bool m = config.BlacklistMatchLocation == "start" ? f.StartsWith(tw, comp) : (config.BlacklistMatchLocation == "end" ? f.EndsWith(tw, comp) : f.Contains(tw, comp));
                                if (m) { blocked = true; break; }
                            }
                        }

                        if (blocked)
                        {
                            try
                            {
                                lock (_registryLock)
                                {
                                    System.IO.File.AppendAllText(System.IO.Path.Combine(GetActiveBackupRoot(), "optimized_registry.txt"), f + Environment.NewLine);
                                }
                            }
                            catch { }
                            continue;
                        }
                    }

                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp")
                    {
                        fileList.Add(new SweeperFile { FilePath = f, MaxDimension = maxDim });
                        if (fileList.Count >= _sweeperScanLimit) return;
                    }
                }
            }
        }
    }
}