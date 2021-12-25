using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HamaSync.BLL
{
    public abstract class FileWatchBase : BackgroundService
    {
        [Flags]
        public enum eFileWatchEvent
        {
            Created,
            Changed,
            Renamed,
            Deleted,
            All = Created | Changed | Renamed | Deleted
        }

        protected FileSystemWatcher Watcher { get; set; }

        /// <summary>
        /// 當所監看的資料夾新增檔案時觸發
        /// </summary>
        protected abstract void OnCreate(object sender, FileSystemEventArgs e);

        /// <summary>
        /// 當所監看的資料夾異動時觸發
        /// </summary>
        protected abstract void OnChanged(object sender, FileSystemEventArgs e);

        /// <summary>
        /// 當所監看的資料夾有檔案重新命名時觸發
        /// </summary>
        protected abstract void OnRenamed(object sender, RenamedEventArgs e);

        /// <summary>
        /// 當所監看的資料夾有檔案有被刪除時觸發
        /// </summary>
        protected abstract void OnDeleted(object sender, FileSystemEventArgs e);

        /// <summary>
        /// 設定監看的事件
        /// </summary>
        /// <param name="watchEvent"></param>
        public void WatchEvents(eFileWatchEvent watchEvent)
        {
            if (Watcher is null)
                throw new ArgumentNullException(nameof(Watcher));

            if ((watchEvent & eFileWatchEvent.Created) == eFileWatchEvent.Created)
                this.WatchCreated();

            if ((watchEvent & eFileWatchEvent.Changed) == eFileWatchEvent.Changed)
                this.WatchChanged();

            if ((watchEvent & eFileWatchEvent.Renamed) == eFileWatchEvent.Renamed)
                this.WatchRenamed();

            if ((watchEvent & eFileWatchEvent.Deleted) == eFileWatchEvent.Deleted)
                this.WatchDeleted();
        }

        /// <summary>
        /// 設定監看新增檔案的觸發事件
        /// </summary>
        public FileWatchBase WatchCreated()
        {
            Watcher.Created += new FileSystemEventHandler(OnCreate);
            return this;
        }

        /// <summary>
        /// 設定監看修改檔案的觸發事件
        /// </summary>
        public FileWatchBase WatchChanged()
        {
            Watcher.Changed += new FileSystemEventHandler(OnChanged);
            return this;
        }

        /// <summary>
        /// 設定監看重新命名的觸發事件
        /// </summary>
        public FileWatchBase WatchRenamed()
        {
            Watcher.Renamed += new RenamedEventHandler(OnRenamed);
            return this;
        }

        /// <summary>
        /// 設定監看刪除檔案的觸發事件
        /// </summary>
        public FileWatchBase WatchDeleted()
        {
            Watcher.Deleted += new FileSystemEventHandler(OnDeleted);
            return this;
        }
    }
}
