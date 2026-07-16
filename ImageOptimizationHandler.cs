using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Irukandji.ImageOptimizer.Logging;
using Irukandji.ImageOptimizer.Services;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Middleware
{
    public class ImageOptimizationHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);

            var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            if (!config.EnableMetadataAvatarTranscoding)
            {
                return response;
            }

            if (response.IsSuccessStatusCode && response.Content != null)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        byte[] originalBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        var optimizer = new ImageOptimizerService();
                        string newContentType;
                        
                        // Correction: Removed duplicate 'string' keyword inside out-parameter binding
                        using SKData optimizedData = optimizer.OptimizeMetadataImage(originalBytes, contentType, out newContentType);

                        if (optimizedData != null) {
                            var newContent = new ByteArrayContent(optimizedData.ToArray());
                            foreach (var header in response.Content.Headers) {
                                if (header.Key != "Content-Type" && header.Key != "Content-Length") {
                                    newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                                }
                            }
                            newContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(newContentType);
                            response.Content = newContent;
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLogger.LogError("Metadata outgoing HttpClient download hook failed", ex);
                    }
                }
            }

            return response;
        }
    }
}