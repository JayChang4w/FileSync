namespace FileSync.DOL.Dto
{
    public record FileEventDto
    {
        public WatcherChangeTypes ChangeType { get; init; }

        public string FullPath { get; init; }

        public FileEventDto(FileSystemEventArgs eventArgs)
        {
            this.ChangeType = eventArgs.ChangeType;
            this.FullPath = eventArgs.FullPath;
        }

        public FileEventDto(WatcherChangeTypes changeType, string fullPath)
        {
            this.ChangeType = changeType;
            this.FullPath = fullPath;
        }
    }
}
