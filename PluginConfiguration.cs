using MediaBrowser.Model.Plugins;

namespace Irukandji.ImageOptimizer.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Core Engine Controls
        public bool EnableOnTheFlyTranscoding { get; set; } = true;
        public bool EnableMetadataAvatarTranscoding { get; set; } = true;

        // General Controls
        public bool LogCodecWarnings { get; set; } = true;
        public bool ConvertPngToJpg { get; set; } = true;
        public bool ConvertToWebpForTransparent { get; set; } = true;
        public bool EnableInSidebar { get; set; } = false;

        // JPEG Config
        public int JpegQuality { get; set; } = 75;
        public bool JpegProgressive { get; set; } = true;

        // WebP Config
        public int WebpQuality { get; set; } = 60;
        public bool WebpLossless { get; set; } = false;

        // PNG Config
        public int PngCompressionLevel { get; set; } = 6; // 0-9

        // Dimensional limits
        public int MaxMetadataDimension { get; set; } = 1920;
        public int MaxAvatarDimension { get; set; } = 512;

        // Client Profiling Settings
        public bool EnableClientProfiling { get; set; } = true;
        public int MobileJpegQuality { get; set; } = 20;
        public int MobileWebpQuality { get; set; } = 20;
        public int MobileMaxDimension { get; set; } = 720;

        // Sweeper Backups
        public bool BackupBeforeSweep { get; set; } = true;
        public int MinImprovementPercentage { get; set; } = 5;
        public string BackupFolderPath { get; set; } = string.Empty;

        // Exclusion matching parameters
        public string BlacklistWords { get; set; } = string.Empty;
        public bool BlacklistCaseSensitive { get; set; } = false;
        public string BlacklistMatchLocation { get; set; } = "anywhere"; // "anywhere", "start", "end"
        public bool BlacklistUseAndOperator { get; set; } = false;

        // Sweeper Areas to Process
        public bool ProcessMetadata { get; set; } = true;
        public bool ProcessCollections { get; set; } = true;
        public bool ProcessPlaylists { get; set; } = true;
        public bool ProcessSmartlists { get; set; } = false; // Evaluated dynamically on load
        public bool ProcessLibraryCovers { get; set; } = false;
        public bool ProcessUserProfiles { get; set; } = false;
        public bool ProcessTrickplay { get; set; } = false;

        // Purge unnecessary trickplay folders
        public bool PurgeTrickplay { get; set; } = false;

        // Library covers resize configs
        public bool ResizeLibraryCovers { get; set; } = false;
        public int LibraryCoversWidth { get; set; } = 500;

        // Media libraries included paths
        public string IncludedLibraries { get; set; } = string.Empty;
    }
}