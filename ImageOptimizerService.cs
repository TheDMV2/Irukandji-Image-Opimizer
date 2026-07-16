using System;
using System.IO;
using Irukandji.ImageOptimizer.Logging;
using SkiaSharp;

namespace Irukandji.ImageOptimizer.Services
{
    public class ImageOptimizerService
    {
        private static string _initError;
        private static bool _initSuccess = false;

        public static void SetInitializationError(string error) => _initError = error;
        public static void SetInitializationSuccess() => _initSuccess = true;
        public static object GetInitializationStatus() => new { Success = _initSuccess, ErrorMessage = _initError };

        private Configuration.PluginConfiguration GetConfig() =>
            Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        public SKData OptimizeMetadataImage(byte[] inputBytes, string contentType, out string finalContentType)
        {
            finalContentType = contentType;
            var config = GetConfig();
            if (!config.EnableMetadataAvatarTranscoding) return null;
            return ProcessImage(inputBytes, contentType, config.MaxMetadataDimension, config, out finalContentType, isMetadata: true);
        }

        public SKData OptimizeAvatarImage(byte[] inputBytes, string contentType, out string finalContentType)
        {
            finalContentType = contentType;
            var config = GetConfig();
            if (!config.EnableMetadataAvatarTranscoding) return null;
            return ProcessImage(inputBytes, contentType, config.MaxAvatarDimension, config, out finalContentType, isMetadata: true);
        }

        public SKData OptimizeClientImage(byte[] inputBytes, string contentType, bool acceptsWebp, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.JpegQuality, config.WebpQuality, 0, acceptsWebp, config, out finalContentType);
        }

        public SKData OptimizeMobileFastPath(byte[] inputBytes, string contentType, bool acceptsWebp, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.MobileJpegQuality, config.MobileWebpQuality, config.MobileMaxDimension, acceptsWebp, config, out finalContentType);
        }

        public SKData OptimizeTrickplayImage(byte[] inputBytes, string contentType, bool acceptsWebp, out string finalContentType)
        {
            var config = GetConfig();
            return ProcessFastPath(inputBytes, contentType, config.JpegQuality, config.WebpQuality, 0, acceptsWebp, config, out finalContentType);
        }

        // Sub-sampling Decoder: Decodes images using direct Zero-Copy native stream layouts to minimize memory footprint
        private SKBitmap DecodeAndScale(byte[] inputBytes, int maxDim, out int targetWidth, out int targetHeight)
        {
            targetWidth = 0;
            targetHeight = 0;
            try
            {
                // Optimization: Copy the managed byte array directly to unmanaged native memory.
                // This prevents Skia's decoder from triggering slow managed stream read P/Invoke crossings.
                using var data = SKData.CreateCopy(inputBytes);
                using var codec = SKCodec.Create(data);
                if (codec == null) return null;

                int width = codec.Info.Width;
                int height = codec.Info.Height;
                targetWidth = width;
                targetHeight = height;

                float scale = 1.0f;
                if (maxDim > 0 && (width > maxDim || height > maxDim))
                {
                    float ratio = (float)width / height;
                    if (ratio > 1f)
                    {
                        targetWidth = maxDim;
                        targetHeight = (int)(maxDim / ratio);
                    }
                    else
                    {
                        targetHeight = maxDim;
                        targetWidth = (int)(maxDim * ratio);
                    }
                    scale = (float)targetWidth / width;
                }

                SKBitmap decoded;
                if (scale < 1.0f)
                {
                    // Codec-level scaling: decompress only the needed downsampled macroblocks natively in libjpeg-turbo.
                    // This bypasses decompressing up to 90% of pixel data, yielding significant performance gains.
                    var scaledDims = codec.GetScaledDimensions(scale);
                    var scaledInfo = new SKImageInfo(scaledDims.Width, scaledDims.Height, codec.Info.ColorType, codec.Info.AlphaType);
                    
                    decoded = new SKBitmap(scaledInfo);
                    var result = codec.GetPixels(scaledInfo, decoded.GetPixels());
                    if (result != SKCodecResult.Success)
                    {
                        decoded.Dispose();
                        decoded = SKBitmap.Decode(codec);
                    }
                }
                else
                {
                    decoded = SKBitmap.Decode(codec);
                }

                if (decoded == null) return null;

                // Fine-grained linear scale adjustment if native codec downsampling has slight offsets
                if (scale < 1.0f && (decoded.Width != targetWidth || decoded.Height != targetHeight))
                {
                    var finalInfo = new SKImageInfo(targetWidth, targetHeight, decoded.ColorType, decoded.AlphaType);
                    var finalBitmap = new SKBitmap(finalInfo);
                    decoded.ScalePixels(finalBitmap, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    decoded.Dispose();
                    decoded = finalBitmap;
                }

                return decoded;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Failed decoding and scaling image dynamically", ex);
                return null;
            }
        }

        // Defensive Clamp Helper: Safely targets older .NET runtimes without framework mismatches
        private static double Clamp(double val, double min, double max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        // Automatic Smart Guard: Automatically limits quality boundaries on the server side to protect CPU
        private int ClampQualityForSpeed(int quality, SKEncodedImageFormat format)
        {
            if (format == SKEncodedImageFormat.Jpeg)
            {
                // Quality above 95 consumes extreme CPU and disk space without visual differences
                return Math.Min(quality, 95);
            }
            if (format == SKEncodedImageFormat.Webp)
            {
                // Quality 80 is the native sweet-spot for libwebp. 
                // Settings above 80 cause non-linear encoding slowdowns.
                return Math.Min(quality, 80);
            }
            return quality;
        }

        // Optimized WebP Encoder: Clamps visual quality to bypass libwebp's exhaustive multi-pass search
        private SKData EncodeWebpOptimized(SKImage image, int quality)
        {
            int targetQuality = ClampQualityForSpeed(quality, SKEncodedImageFormat.Webp);
            return image.Encode(SKEncodedImageFormat.Webp, targetQuality);
        }

        // Fast-Path: Specifically optimized for on-the-fly execution (Returns native unmanaged SKData)
        private SKData ProcessFastPath(
            byte[] inputBytes, 
            string contentType, 
            int jpegQual, 
            int webpQual, 
            int maxDim, 
            bool acceptsWebp, 
            Configuration.PluginConfiguration config, 
            out string finalContentType)
        {
            finalContentType = contentType;
            try
            {
                bool isPngOrGif = contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                                  contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase);

                // Optimization: Enforce a defensive ceiling for on-the-fly PNG/GIF conversions if no maxDim is defined
                int targetMaxDim = maxDim;
                if (targetMaxDim <= 0 && isPngOrGif)
                {
                    targetMaxDim = 4096;
                }

                using var original = DecodeAndScale(inputBytes, targetMaxDim, out int _, out int _);
                if (original == null) return null;

                // Optimization: Lazy transparency evaluation prevents pixel grid scans when not required
                bool? hasTransparency = null;
                bool GetHasTransparency()
                {
                    if (!hasTransparency.HasValue)
                    {
                        hasTransparency = HasTransparency(original);
                    }
                    return hasTransparency.Value;
                }

                using var image = SKImage.FromBitmap(original);
                SKEncodedImageFormat outputFormat;
                int quality = 100;

                // Format Negotiation: Convert to WebP / JPEG / PNG
                if (acceptsWebp && (config.ConvertToWebpForTransparent || config.ConvertPngToJpg || (isPngOrGif && GetHasTransparency())))
                {
                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = ClampQualityForSpeed(webpQual, SKEncodedImageFormat.Webp);
                    finalContentType = "image/webp";
                }
                else if (isPngOrGif)
                {
                    if (GetHasTransparency())
                    {
                        if (config.ConvertToWebpForTransparent && acceptsWebp)
                        {
                            outputFormat = SKEncodedImageFormat.Webp;
                            quality = ClampQualityForSpeed(webpQual, SKEncodedImageFormat.Webp);
                            finalContentType = "image/webp";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                    else
                    {
                        if (config.ConvertPngToJpg)
                        {
                            outputFormat = SKEncodedImageFormat.Jpeg;
                            quality = ClampQualityForSpeed(jpegQual, SKEncodedImageFormat.Jpeg);
                            finalContentType = "image/jpeg";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                }
                else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Jpeg;
                    quality = ClampQualityForSpeed(jpegQual, SKEncodedImageFormat.Jpeg);
                    finalContentType = "image/jpeg";
                }
                else if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = ClampQualityForSpeed(webpQual, SKEncodedImageFormat.Webp);
                    finalContentType = "image/webp";
                }
                else
                {
                    return null;
                }

                // Optimization: Route WebP files through the fast, clamped encoder path
                if (outputFormat == SKEncodedImageFormat.Webp)
                {
                    SKData encodedWebp = EncodeWebpOptimized(image, quality);
                    if (encodedWebp != null)
                    {
                        return encodedWebp;
                    }
                }

                SKData encoded = image.Encode(outputFormat, quality);
                if (encoded != null)
                {
                    return encoded;
                }

                return null;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Fast path on-the-fly execution crashed", ex);
                return null;
            }
        }

        // High-Effort Metadata Path (Returns native unmanaged SKData)
        public SKData ProcessImage(byte[] inputBytes, string contentType, int maxDimension, Configuration.PluginConfiguration config, out string finalContentType, bool isMetadata)
        {
            finalContentType = contentType;
            try
            {
                using var original = DecodeAndScale(inputBytes, maxDimension, out int _, out int _);
                if (original == null) return null;

                bool isPngOrGif = contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
                                  contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase);

                // Optimization: Lazy transparency evaluation prevents pixel grid scans when not required
                bool? hasTransparency = null;
                bool GetHasTransparency()
                {
                    if (!hasTransparency.HasValue)
                    {
                        hasTransparency = HasTransparency(original);
                    }
                    return hasTransparency.Value;
                }

                using var image = SKImage.FromBitmap(original);
                SKEncodedImageFormat outputFormat;
                int quality = 100;

                if (isPngOrGif)
                {
                    if (GetHasTransparency())
                    {
                        if (config.ConvertToWebpForTransparent)
                        {
                            outputFormat = SKEncodedImageFormat.Webp;
                            quality = ClampQualityForSpeed(config.WebpQuality, SKEncodedImageFormat.Webp);
                            finalContentType = "image/webp";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                    else
                    {
                        if (config.ConvertPngToJpg)
                        {
                            outputFormat = SKEncodedImageFormat.Jpeg;
                            quality = ClampQualityForSpeed(config.JpegQuality, SKEncodedImageFormat.Jpeg);
                            finalContentType = "image/jpeg";
                        }
                        else
                        {
                            outputFormat = SKEncodedImageFormat.Png;
                            finalContentType = "image/png";
                        }
                    }
                }
                else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Jpeg;
                    quality = ClampQualityForSpeed(config.JpegQuality, SKEncodedImageFormat.Jpeg);
                    finalContentType = "image/jpeg";
                }
                else if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
                {
                    outputFormat = SKEncodedImageFormat.Webp;
                    quality = ClampQualityForSpeed(config.WebpQuality, SKEncodedImageFormat.Webp);
                    finalContentType = "image/webp";
                }
                else
                {
                    return null;
                }

                // Optimization: Route WebP files through the fast, clamped encoder path
                if (outputFormat == SKEncodedImageFormat.Webp)
                {
                    SKData encodedWebp = EncodeWebpOptimized(image, quality);
                    if (encodedWebp != null)
                    {
                        return encodedWebp;
                    }
                }

                SKData encoded = image.Encode(outputFormat, quality);
                if (encoded != null)
                {
                    return encoded;
                }

                return null;
            }
            catch (Exception ex)
            {
                PluginLogger.LogError("Metadata path image optimization failed", ex);
                return null;
            }
        }

        private bool HasTransparency(SKBitmap bitmap)
        {
            if (bitmap.AlphaType == SKAlphaType.Opaque) return false;

            var pixels = bitmap.GetPixelSpan();
            int bytesPerPixel = bitmap.BytesPerPixel;

            if (bytesPerPixel == 4)
            {
                // Verify transparency channel bytes sequentially
                for (int i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] < 255)
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Fallback for non-standard index/16-bit textures
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++) 
                    { 
                        if (bitmap.GetPixel(x, y).Alpha < 255) return true; 
                    }
                }
            }

            return false;
        }

        // Low-Allocation Pixel Difference Visual Fidelity Scorer
        public static double CalculateFidelityScore(SKBitmap bmp1, SKBitmap bmp2)
        {
            SKBitmap tempBmp = null;
            SKBitmap b1 = bmp1;
            SKBitmap b2 = bmp2;

            if (bmp1.Width != bmp2.Width || bmp1.Height != bmp2.Height)
            {
                tempBmp = new SKBitmap(new SKImageInfo(bmp2.Width, bmp2.Height, bmp1.ColorType, bmp1.AlphaType));
                bmp1.ScalePixels(tempBmp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                b1 = tempBmp;
            }

            try
            {
                var span1 = b1.GetPixelSpan();
                var span2 = b2.GetPixelSpan();

                if (span1.Length != span2.Length) return 1.0;

                double sumSquaredError = 0;
                long totalCheckedBytes = 0;

                for (int i = 0; i < span1.Length; i++) 
                {
                    int diff = span1[i] - span2[i]; 
                    sumSquaredError += diff * diff; 
                    totalCheckedBytes++;
                }

                if (totalCheckedBytes == 0) return 100.0;

                double mse = sumSquaredError / totalCheckedBytes;
                if (mse == 0) return 100.0;

                double psnr = 10.0 * Math.Log10((255.0 * 255.0) / mse);
                double score = (psnr - 20.0) / (50.0 - 20.0) * 100.0;
                score = Clamp(score, 1.0, 100.0);

                return Math.Round(score, 1);
            }
            finally
            {
                tempBmp?.Dispose();
            }
        }
    }
}