namespace FileSync.DOL
{
    public enum eStatus : byte
    {
        /// <summary>
        /// 開始
        /// </summary>
        Start,
        /// <summary>
        /// 比對
        /// </summary>
        Compare,
        /// <summary>
        /// 繼續
        /// </summary>
        Continue,
        /// <summary>
        /// 分割
        /// </summary>
        Split,
        /// <summary>
        /// 中斷
        /// </summary>
        Break,
        /// <summary>
        /// 完成
        /// </summary>
        Complete,
        /// <summary>
        /// 刪除
        /// </summary>
        Delete
    }
}