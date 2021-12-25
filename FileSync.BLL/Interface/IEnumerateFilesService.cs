using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSync.BLL.Interface
{
    public interface IEnumerateFilesService
    {
        IEnumerable<FileInfo> EnumerateFileInfo(DirectoryInfo dir, string searchPattern = "");
        IEnumerable<string> EnumerateFiles(string path, string searchPattern = "");
    }
}
