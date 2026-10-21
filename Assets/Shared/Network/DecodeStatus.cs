namespace CloudIsle.Net
{
    /// <summary>TryDecode 的三种结果：数据不够、成功解出一帧、长度非法。</summary>
    public enum DecodeStatus
    {
        /// <summary>缓冲里的字节还不够一整帧。consumedBytes 为 0。</summary>
        NeedMoreData = 0,

        /// <summary>最前面的一帧已经完整。consumedBytes 等于该帧字节数。</summary>
        Success = 1,

        /// <summary>len 小于 6 或大于 65542。consumedBytes 为 0。</summary>
        Invalid = 2
    }
}
