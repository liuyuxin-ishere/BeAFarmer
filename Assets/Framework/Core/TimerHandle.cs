namespace Game.Framework.Core
{
    /// <summary>
    /// 一次 Schedule 的取消凭证。Id 标出槽位，Generation 防止槽位复用后误取消。
    /// </summary>
    public readonly struct TimerHandle
    {
        /// <summary>槽位编号。0 表示还没有登记过的空句柄。</summary>
        public int Id { get; }

        /// <summary>该槽位当前这一轮登记的世代。取消时必须与槽位里的世代相同。</summary>
        public int Generation { get; }

        /// <summary>
        /// 记录一次成功的 Schedule。
        /// </summary>
        /// <param name="id">槽位编号，从 1 起。</param>
        /// <param name="generation">该槽位本次登记的世代，从 1 起，每次回收后加一。</param>
        public TimerHandle(int id, int generation)
        {
            Id = id;
            Generation = generation;
        }
    }
}
