using System;
using System.IO;
using System.Threading.Tasks;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;

namespace Irukandji.ImageOptimizer.Middleware
{
    public class ImageOptimizerMiddleware
    {
        private readonly RequestDelegate _next;

        // In-memory bounded cache limit of 256 MB
        private static readonly MemoryCache _responseCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 256 * 1024 * 1024 
        });

        private class CachedImage
        {
            public byte[] Data { get; set; }
            public string ContentType { get; set; }
        }

        public ImageOptimizerMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            // Intercept incoming User Avatar uploads
            if ((method == "POST" || method == "PUT") &&
                path.Contains("/Users/", StringComparison.OrdinalIgnoreCase) &&
                path.Contains("/Images", StringComparison.OrdinalIgnoreCase))
            {
                var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
                if (config.EnableMetadataAvatarTranscoding)
                {
                    try
                    {
                        await InterceptUploadStream(context);
                    }
                    catch (Exception ex)
                    {
                        PluginLogger.LogError("Failed optimizing incoming avatar upload", ex);
                    }
                }
                await _next(context);
                return;
            }

            // Check if this is an image GET request
            bool isClientImageGet = method == "GET" &&
                                   (path.Contains("/Items/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Users/", StringComparison.OrdinalIgnoreCase)) &&
                                   path.Contains("/Images", StringComparison.OrdinalIgnoreCase);

            bool isTrickplayGet = method == "GET" && path.Contains("/Trickplay/", StringComparison.OrdinalIgnoreCase);

            if (isClientImageGet || isTrickplayGet)
            {
                var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
                
                // Bypass entirely if real-time engine transcoding is disabled
                if (!config.EnableOnTheFlyTranscoding)
                {
                    await _next(context);
                    return;
                }

                // Support benchmark-driven test bypass queries
                bool bypassOptimizer = context.Request.Query.TryGetValue("bypassOptimizer", out var bypassVal) && 
                                       bypassVal.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

                if (bypassOptimizer)
                {
                    await _next(context);
                    return;
                }

                // Check if this request is a cache-busting benchmarking test
                bool isTestRequest = context.Request.Query.ContainsKey("test_cb");

                // Device negotiation: check accepted content types
                bool acceptsWebp = false;
                if (context.Request.Headers.TryGetValue("Accept", out var acceptValue))
                {
                    string accept = acceptValue.ToString();
                    acceptsWebp = accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase);
                }

                // Device profiling: check User-Agent patterns or forced test mobile queries
                bool isMobile = false;
                if (config.EnableClientProfiling)
                {
                    bool forceMobile = context.Request.Query.TryGetValue("forceMobileProfile", out var fmVal) && 
                                       fmVal.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

                    if (forceMobile)
                    {
                        isMobile = true;
                    }
                    else if (context.Request.Headers.TryGetValue("User-Agent", out var uaValue))
                    {
                        string ua = uaValue.ToString();
                        isMobile = ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
                                   ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                                   ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                                   ua.Contains("iPad", StringComparison.OrdinalIgnoreCase);
                    }
                }

                string tag = context.Request.Query.TryGetValue("tag", out var tagVal) ? tagVal.ToString() : string.Empty;
                string cacheKey = GenerateNormalizedCacheKey(path, tag, acceptsWebp, isMobile);

                // 1. Cache Check (Fast-path: bypassed if executing a benchmark test)
                if (!isTestRequest && _responseCache.TryGetValue(cacheKey, out CachedImage cached))
                {
                    context.Response.ContentType = cached.ContentType;
                    context.Response.ContentLength = cached.Data.Length;
                    context.Response.Headers["Cache-Control"] = "public, max-age=31536000";
                    await context.Response.Body.WriteAsync(cached.Data, 0, cached.Data.Length);
                    return;
                }

                // 2. Direct Disk Bypass (Bypassed if executing a benchmark test)
                if (!isTestRequest && isClientImageGet)
                {
                    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (await TryDirectBypass(context, path, segments, cacheKey, acceptsWebp, isMobile, config))
                    {
                        return; 
                    }
                }

                // 3. Downstream Interception Fallback
                var originalBodyStream = context.Response.Body;
                long? contentLength = context.Response.ContentLength;
                using var responseBody = contentLength.HasValue 
                    ? new MemoryStream((int)contentLength.Value) 
                    : new MemoryStream();
                    
                context.Response.Body = responseBody;

                try
                {
                    await _next(context);
                }
                catch
                {
                    responseBody.Position = 0;
                    await responseBody.CopyToAsync(originalBodyStream);
                    throw;
                }

                if (context.Response.StatusCode == 200)
                {
                    var contentType = context.Response.ContentType;
                    if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] originalBytes = responseBody.ToArray();
                        var optimizer = new ImageOptimizerService();

                        if (isTrickplayGet)
                        {
                            string newContentType;
                            using SKData optimizedData = optimizer.OptimizeTrickplayImage(originalBytes, contentType, acceptsWebp, out newContentType);
                            if (optimizedData != null)
                            {
                                context.Response.ContentType = newContentType;
                                context.Response.ContentLength = optimizedData.Size;
                                context.Response.Headers["Cache-Control"] = "public, max-age=31536000";
                                
                                byte[] optimizedBytes = optimizedData.ToArray();
                                if (!isTestRequest)
                                {
                                    PopulateCacheDirect(cacheKey, optimizedBytes, newContentType);
                                }

                                await originalBodyStream.WriteAsync(optimizedBytes, 0, optimizedBytes.Length);
                                return;
                            }
                        }
                        else if (isClientImageGet)
                        {
                            string newContentTypeScope;
                            using SKData optimizedData = isMobile 
                                ? optimizer.OptimizeMobileFastPath(originalBytes, contentType, acceptsWebp, out newContentTypeScope)
                                : optimizer.OptimizeClientImage(originalBytes, contentType, acceptsWebp, out newContentTypeScope);

                            if (optimizedData != null)
                            {
                                context.Response.ContentType = newContentTypeScope;
                                context.Response.ContentLength = optimizedData.Size;
                                context.Response.Headers["Cache-Control"] = "public, max-age=31536000";
                                
                                byte[] optimizedBytes = optimizedData.ToArray();
                                if (!isTestRequest)
                                {
                                    PopulateCacheDirect(cacheKey, optimizedBytes, newContentTypeScope);
                                }

                                await originalBodyStream.WriteAsync(optimizedBytes, 0, optimizedBytes.Length);
                                return;
                            }
                        }
                    }
                }

                responseBody.Position = 0;
                await responseBody.CopyToAsync(originalBodyStream);
                return;
            }

            await _next(context);
        }

        private async Task<bool> TryDirectBypass(
            HttpContext context, 
            string path, 
            string[] segments, 
            string cacheKey, 
            bool acceptsWebp, 
            bool isMobile, 
            Configuration.PluginConfiguration config)
        {
            try
            {
                var libraryManager = context.RequestServices.GetService(typeof(ILibraryManager)) as ILibraryManager;
                if (libraryManager == null) return false;

                int itemsIdx = Array.FindIndex(segments, x => x.Equals("Items", StringComparison.OrdinalIgnoreCase));
                if (itemsIdx >= 0 && itemsIdx + 1 < segments.Length)
                {
                    string itemIdStr = segments[itemsIdx + 1];
                    if (Guid.TryParse(itemIdStr, out Guid itemId))
                    {
                        var item = libraryManager.GetItemById(itemId);
                        if (item != null)
                        {
                            int imagesIdx = Array.FindIndex(segments, x => x.Equals("Images", StringComparison.OrdinalIgnoreCase));
                            if (imagesIdx >= 0 && imagesIdx + 1 < segments.Length)
                            {
                                string imageTypeStr = segments[imagesIdx + 1];
                                if (Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(imageTypeStr, true, out var imageType))
                                {
                                    int imageIndex = 0;
                                    if (imagesIdx + 2 < segments.Length && int.TryParse(segments[imagesIdx + 2], out int parsedIndex))
                                    {
                                        imageIndex = parsedIndex;
                                    }

                                    string physicalPath = item.GetImagePath(imageType, imageIndex);
                                    if (string.IsNullOrEmpty(physicalPath))
                                    {
                                        physicalPath = item.GetImagePath(imageType);
                                    }

                                    if (!string.IsNullOrEmpty(physicalPath) && System.IO.File.Exists(physicalPath))
                                    {
                                        int maxDim = 0;
                                        if (isMobile)
                                        {
                                            maxDim = config.MobileMaxDimension;
                                        }
                                        else if (context.Request.Query.TryGetValue("maxWidth", out var mwVal) && int.TryParse(mwVal, out int parsedMw))
                                        {
                                            maxDim = parsedMw;
                                        }

                                        await ProcessAndServeDirect(context, physicalPath, cacheKey, acceptsWebp, isMobile, maxDim, config);
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                int usersIdx = Array.FindIndex(segments, x => x.Equals("Users", StringComparison.OrdinalIgnoreCase));
                if (usersIdx >= 0 && usersIdx + 1 < segments.Length)
                {
                    string userIdStr = segments[usersIdx + 1];
                    if (Guid.TryParse(userIdStr, out Guid userId))
                    {
                        var userManager = context.RequestServices.GetService(typeof(IUserManager)) as IUserManager;
                        if (userManager != null)
                        {
                            var user = userManager.GetUserById(userId);
                            if (user != null)
                            {
                                var appPaths = context.RequestServices.GetService(typeof(MediaBrowser.Common.Configuration.IApplicationPaths)) as MediaBrowser.Common.Configuration.IApplicationPaths;
                                if (appPaths != null)
                                {
                                    string userFolder = System.IO.Path.Combine(appPaths.ConfigurationDirectoryPath, "users", userId.ToString());
                                    if (System.IO.Directory.Exists(userFolder))
                                    {
                                        string[] possibleNames = { "poster.jpg", "poster.png", "poster.webp", "poster.gif" };
                                        foreach (var name in possibleNames)
                                        {
                                            string fullPath = System.IO.Path.Combine(userFolder, name);
                                            if (System.IO.File.Exists(fullPath))
                                            {
                                                int maxDim = isMobile ? config.MobileMaxDimension : config.MaxAvatarDimension;
                                                await ProcessAndServeDirect(context, fullPath, cacheKey, acceptsWebp, isMobile, maxDim, config);
                                                return true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Direct bypass optimization path failed; falling back to response interceptor", ex);
            }

            return false;
        }

        private async Task ProcessAndServeDirect(
            HttpContext context, 
            string physicalPath, 
            string cacheKey, 
            bool acceptsWebp, 
            bool isMobile, 
            int maxDim, 
            Configuration.PluginConfiguration config)
        {
            byte[] originalBytes;
            using (var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: false))
            {
                originalBytes = new byte[fs.Length];
                fs.ReadExactly(originalBytes);
            }

            string ext = System.IO.Path.GetExtension(physicalPath).ToLowerInvariant();
            string contentType = ext == ".png" ? "image/png" : (ext == ".gif" ? "image/gif" : (ext == ".webp" ? "image/webp" : "image/jpeg"));

            var optimizer = new ImageOptimizerService();
            string newContentType;
            
            using var optimizedData = isMobile 
                ? optimizer.OptimizeMobileFastPath(originalBytes, contentType, acceptsWebp, out newContentType)
                : optimizer.OptimizeClientImage(originalBytes, contentType, acceptsWebp, out newContentType);

            if (optimizedData != null)
            {
                context.Response.ContentType = newContentType;
                context.Response.ContentLength = optimizedData.Size;
                context.Response.Headers["Cache-Control"] = "public, max-age=31536000";

                byte[] optimizedBytes = optimizedData.ToArray();
                PopulateCacheDirect(cacheKey, optimizedBytes, newContentType);

                await context.Response.Body.WriteAsync(optimizedBytes, 0, optimizedBytes.Length);
            }
            else
            {
                context.Response.ContentType = contentType;
                context.Response.ContentLength = originalBytes.Length;
                context.Response.Headers["Cache-Control"] = "public, max-age=31536000";
                await context.Response.Body.WriteAsync(originalBytes, 0, originalBytes.Length);
            }
        }

        private async Task InterceptUploadStream(HttpContext context)
        {
            context.Request.EnableBuffering();
            
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            
            byte[] originalBytes = ms.ToArray();
            string contentType = context.Request.ContentType ?? "image/jpeg";
            var optimizer = new ImageOptimizerService();
            string newContentType;
            
            using SKData optimizedData = optimizer.OptimizeAvatarImage(originalBytes, contentType, out newContentType);

            if (optimizedData != null) {
                context.Request.Body = new MemoryStream(optimizedData.ToArray());
                context.Request.ContentType = newContentType;
                context.Request.ContentLength = optimizedData.Size;
            } else {
                context.Request.Body.Position = 0;
            }
        }

        public static string GenerateNormalizedCacheKey(string path, string tag, bool acceptsWebp, bool isMobile)
        {
            return $"imgcache:{path.ToLowerInvariant()}:tag={tag}:webp={acceptsWebp}:mobile={isMobile}";
        }

        public static void PopulateCacheDirect(string key, byte[] data, string contentType)
        {
            try
            {
                var cachedImage = new CachedImage
                {
                    Data = data,
                    ContentType = contentType
                };

                var entryOptions = new MemoryCacheEntryOptions()
                    .SetSize(data.Length)
                    .SetSlidingExpiration(TimeSpan.FromDays(7))
                    .SetAbsoluteExpiration(TimeSpan.FromDays(30));

                _responseCache.Set(key, cachedImage, entryOptions);
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed to insert pre-seeded entry into response cache", ex);
            }
        }
    }
}
