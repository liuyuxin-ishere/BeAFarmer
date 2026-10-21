namespace Game.Demo.AvatarMerge
{
    /// <summary>对应 02§10.1 / Luban AvatarPart（Dress 为本 PoC 可选）。</summary>
    public enum AvatarPartType
    {
        Hair = 0,
        Top = 1,
        Bottom = 2,
        Shoes = 3,
        Hat = 4,
        Acc = 5,
        /// <summary>连衣裙：正式规则占用 Top+Bottom；PoC 可暂不实现。</summary>
        Dress = 6
    }
}
