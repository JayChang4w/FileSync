namespace FileSync.DOL.Dto
{
    public class FileInfoDto
    {
        public FileInfoDto()
        {
        }

        public FileInfoDto(FileInfo fileInfo)
        {
            this.SetMetaData(fileInfo);
        }

        public FileInfoDto(FileInfo fileInfo, eStatus status)
        {
            this.SetMetaData(fileInfo);
            this.Status = status;
        }

        public void SetMetaData(FileInfo fileInfo)
        {
            this.FullName = fileInfo.FullName;
            this.Length = fileInfo.Length;
            this.LastWriteTime = fileInfo.LastWriteTime;
        }

        /// <summary>
        /// Gets the full path of the directory or file.
        /// </summary>
        public string FullName { get; set; }

        /// <summary>
        ///   Gets the size, in bytes, of the current file.
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        ///  Gets or sets the time when the current file or directory was last written to.
        /// </summary>
        public DateTime LastWriteTime { get; set; }

        /// <summary>
        ///
        /// </summary>
        public eStatus Status { get; set; }
    }
}
