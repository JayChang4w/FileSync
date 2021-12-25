using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSync.DOL.Setting
{
    public record ReceiverSetting
    {
        /// <summary>
        /// 儲存的路徑
        /// </summary>
        public string SavePath { get; init; }

        /// <summary>
        /// Buffer大小(單位:byte)
        /// </summary>
        public int BufferSize { get; init; }

        /// <summary>
        /// 最多同時傳輸幾筆
        /// </summary>
        public int MaxTaskCount { get; init; }
    }
}
