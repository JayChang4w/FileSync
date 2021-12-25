using FileSync.BLL.Interface;

namespace FileSync.BLL.Service
{
    public class EnumerateFileService : IEnumerateFilesService
    {
        static readonly EnumerationOptions EnumerationOptions = new EnumerationOptions()
        {
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        public IEnumerable<FileInfo> EnumerateFileInfo(DirectoryInfo dir, string searchPattern = "")
        {
            return dir.EnumerateFiles(searchPattern, EnumerationOptions)
                .Union(dir.EnumerateDirectories()
                    .SelectMany(subDir =>
                    {
                        try
                        {
                            return this.EnumerateFileInfo(subDir, searchPattern);
                        }
                        catch
                        {
                            return Enumerable.Empty<FileInfo>();
                        }
                    }
                )
            );
        }

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "")
        {
            return Directory.EnumerateFiles(path, searchPattern, EnumerationOptions)
                .Union(Directory.EnumerateDirectories(path)
                    .SelectMany(subPath =>
                    {
                        try
                        {
                            return EnumerateFiles(subPath, searchPattern);
                        }
                        catch
                        {
                            return Enumerable.Empty<string>();
                        }
                    })
                );
        }
    }
}
