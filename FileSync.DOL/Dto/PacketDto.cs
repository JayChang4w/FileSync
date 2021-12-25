using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSync.DOL.Dto
{
    public class PacketDto
    {
        public eStatus Status { get; set; }

        public byte[] Buffer { get; set; }

        public long FileLength { get; set; }

        public long FileHash { get; set; }

        public DateTime? LastWriteTime { get; set; }
    }
}
