using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSync.DOL.Setting
{
    public record IPEndPointSetting
    {
        public string HostNameOrAddress { get; init; }
        public int Port { get; init; }
    }
}
