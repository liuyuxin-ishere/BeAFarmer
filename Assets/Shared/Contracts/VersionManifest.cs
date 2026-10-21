namespace CloudIsle.Contracts
{
    /// <summary>
    /// 一次启动要核对的版本数据。放在共享层，AOT 与服务端都能看见。
    /// </summary>
    public sealed class VersionManifest
    {
        /// <summary>允许进入游戏的最低安装包版本，例如 0.1.0。低于它就要重装，不能靠热更。</summary>
        public string MinAppVersion;

        /// <summary>资源与热更代码版本。M1 验收基包为 100，补丁为 101。</summary>
        public int ResVersion;

        /// <summary>协议版本。登录时与服务端比对，不兼容则不能进游戏。</summary>
        public int ProtoVersion;

        /// <summary>配置表版本。与资源版本一起标识这次补丁的配置。</summary>
        public int ConfigVersion;

        /// <summary>YooAsset 包版本字符串，给资源系统定位清单。</summary>
        public string PackageVersion;

        /// <summary>补丁文件的 HTTP 根地址。</summary>
        public string BaseUrl;
    }
}
