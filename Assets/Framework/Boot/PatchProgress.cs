namespace Game.Framework.Boot
{
    /// <summary>
    /// 补丁下载进度快照。总量为 0（零文件更新）时也能表示完成，UI 不得除零。
    /// </summary>
    public struct PatchProgress
    {
        /// <summary>已下载字节数（来自 YooAsset Downloader 实际计数）。</summary>
        public long DownloadedBytes;

        /// <summary>计划下载总字节；零文件更新时为 0。</summary>
        public long TotalBytes;

        /// <summary>已完成文件数。</summary>
        public int CompletedFiles;

        /// <summary>计划文件总数；零文件更新时为 0。</summary>
        public int TotalFiles;

        /// <summary>阶段名，例如 RequestVersion / LoadManifest / Download / Ready / Failed。</summary>
        public string Stage;
    }
}
