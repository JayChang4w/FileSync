using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSync.DOL.Setting
{
    public record TransferSetting
    {
        /// <summary>
        /// 監看的路徑
        /// </summary>
        public string Path { get; init; }

        /// <summary>
        /// 過濾
        /// </summary>
        public string Filter { get; init; }

        /// <summary>
        /// Buffer大小(單位:byte)
        /// </summary>
        public int BufferSize { get; init; }

        /// <summary>
        /// 最多同時傳輸幾筆
        /// </summary>
        public int MaxTaskCount { get; init; }

        /// <summary>
        /// 接收的路徑
        /// </summary>
        public string ReceiveDirectory { get; init; }
    }
}
