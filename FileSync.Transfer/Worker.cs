using FileSync.BLL;
using FileSync.BLL.Interface;
using FileSync.BLL.Service;
using FileSync.DOL;
using FileSync.DOL.Dto;
using FileSync.DOL.Setting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FileSync.Transfer
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IFileHashService _fileHashService;
        private readonly IEnumerateFilesService _enumerateFileService;

        private readonly IPEndPoint _endPoint;
        private readonly TransferSetting _transferSetting;

        /// <summary>
        /// 限制執行緒數目
        /// https://docs.microsoft.com/zh-tw/dotnet/api/system.threading.semaphoreslim?view=net-5.0
        /// </summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>
        /// 檔案監控
        /// </summary>
        private readonly FileSystemWatcher _watcher;

        /// <summary>
        /// 記錄檔案錯誤次數Dictionary
        /// </summary>
        private readonly Dictionary<string, int> ExceptionCountDictionary = new Dictionary<string, int>();

        /// <summary>
        /// 檔案事件隊列
        /// </summary>
        private readonly UniqueQueue<FileEventDto> EventsQueue = new UniqueQueue<FileEventDto>();

        /// <summary>
        /// 待處理檔案隊列
        /// </summary>
        private readonly ConcurrentQueue<FileInfoDto> ProgressQueue = new ConcurrentQueue<FileInfoDto>();

        public Worker(ILogger<Worker> logger,
                                      IPEndPoint endPoint,
                                      TransferSetting transferSetting,
                                      IEnumerateFilesService enumerateFilesService,
                                      IFileHashService fileHashService)
        {
            this._logger = logger;
            this._endPoint = endPoint;
            this._transferSetting = transferSetting;
            this._fileHashService = fileHashService;
            this._enumerateFileService = enumerateFilesService;
            this._semaphore = new SemaphoreSlim(this._transferSetting.MaxTaskCount);

            this._watcher = new FileSystemWatcher
            {
                InternalBufferSize = 64 * 1024,
                // 設定要監看的資料夾
                Path = this._transferSetting.Path,
                // 設定要監看的變更類型
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                // 設定要監看的檔案類型
                Filter = this._transferSetting.Filter,
                // 設定是否監看子資料夾
                IncludeSubdirectories = true
            };
        }

        /// <summary>
        /// 爬目錄下面所有檔案
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private async Task CrawlFilesAsync(CancellationToken stoppingToken = default)
        {
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                DirectoryInfo directoryInfo = new DirectoryInfo(_transferSetting.Path);

                foreach (FileInfo fileInfo in this._enumerateFileService.EnumerateFileInfo(directoryInfo, _transferSetting.Filter))
                {
                    FileInfoDto dto = new FileInfoDto(fileInfo, eStatus.Start);

                    //排入任務至隊列
                    this.ProgressQueue.Enqueue(dto);
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }

        /// <summary>
        /// 處理檔案事件
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private async Task HandleEventsAsync(CancellationToken stoppingToken = default)
        {
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                //如果隊列不是空的，取出一筆事件做處理
                while (this.EventsQueue.TryDequeue(out FileEventDto eventDto))
                {
                    //如果是新增或變動事件
                    if (eventDto.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Changed)
                    {
                        FileInfo fileInfo = new FileInfo(eventDto.FullPath);

                        if (fileInfo?.Exists == true)
                        {
                            FileInfoDto dto = new FileInfoDto(fileInfo, eStatus.Start);

                            this.ProgressQueue.Enqueue(dto);
                        }
                    }
                    //如果是刪除事件
                    else if (eventDto.ChangeType is WatcherChangeTypes.Deleted)
                    {
                        FileInfoDto dto = new()
                        {
                            FullName = eventDto.FullPath,
                            Status = eStatus.Delete
                        };

                        this.ProgressQueue.Enqueue(dto);
                    }
                }
            }
        }

        /// <summary>
        /// 傳送檔案
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private async Task ProgressFileAsync(FileInfoDto dto, CancellationToken stoppingToken = default)
        {
            try
            {
                using TcpClient client = new()
                {
                    SendBufferSize = 64 * 1024 //傳送buffer大小
                };

                //連結至接收端
                client.Connect(_endPoint.Address, _endPoint.Port);

                //取得網路流
                await using NetworkStream networkStream = client.GetStream();

                //相對路徑
                string relativePath = Path.GetRelativePath(_transferSetting.Path, dto.FullName);

                string path = relativePath;

                if (!string.IsNullOrWhiteSpace(_transferSetting.ReceiveDirectory))
                {
                    path = Path.Combine(_transferSetting.ReceiveDirectory, path);
                }

                //刪除
                if (dto.Status == eStatus.Delete)
                {
                    //送出刪除訊息
                    networkStream.SendDeletePacket(path);

                    this._logger.LogInformation(MyEventId.DELETE, "file：\"{0}\"：delete.", dto.FullName);
                }
                else
                {
                    bool isDone = false;

                    //送出開始傳送封包(檔案路徑+檔案大小)
                    networkStream.SendStartPacket(path, dto.Length, dto.LastWriteTime);

                    await using FileStream fileStream = new FileStream(dto.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                    while (!isDone)
                    {
                        //取得接收端回應封包
                        PacketDto receivePacket = networkStream.GetPacket();

                        switch (receivePacket.Status)
                        {
                            //繼續
                            case eStatus.Continue:
                                //確認接收端收到資料長度
                                long recvevieLength = receivePacket.FileLength;

                                //如果資料長度小於傳送檔案長度，表示還沒傳送完，繼續接收檔案
                                if (recvevieLength < fileStream.Length)
                                {
                                    //如果已接收的長度不等於目前檔案流的指標
                                    if (recvevieLength != fileStream.Position)
                                    {
                                        //將指標移動到當前傳輸檔案流位置
                                        fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                    }

                                    //計算尚未傳輸的bytes長度
                                    long byteLeft = (fileStream.Length - recvevieLength);

                                    //計算buffer大小
                                    int chunkSize = (int)Math.Min(byteLeft, this._transferSetting.BufferSize);

                                    //建立對應大小的buffer
                                    byte[] buffer = new byte[chunkSize];

                                    //從檔案流讀取至buffer
                                    fileStream.Read(buffer);

                                    //發送封包給接收端
                                    networkStream.SendSplitPacket(buffer);

                                    //計算傳送進度
                                    //var progressPercent = (int)Math.Ceiling((double)recvevieLength / (double)fileStream.Length * 100);

                                    //if (progressPercent % 10 == 0)
                                    //{
                                    //    this._logger.LogInformation(EventIds.SUCCESS, "file：\"{0}\"：{1}%.", dto.FullName, progressPercent);
                                    //}
                                }
                                else
                                {
                                    //送出完成訊息給接收端
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：received.", dto.FullName);
                                }
                                break;
                            //比對
                            case eStatus.Compare:
                                //比對接收端傳送的哈希值是否相符
                                bool isEquals = this._fileHashService.CompareHash(receivePacket.FileHash, fileStream);
                                //如果相符
                                if (isEquals)
                                {
                                    //送出傳輸完成訊息
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：not changed.", dto.FullName);
                                }
                                else
                                {
                                    //如果已接收的長度不等於目前檔案流的指標
                                    if (fileStream.Position != 0L)
                                    {
                                        //將指標移動到當前傳輸檔案流位置
                                        fileStream.Seek(0L, SeekOrigin.Begin);
                                    }

                                    //計算chunk大小
                                    int chunkSize = (int)Math.Min(fileStream.Length, this._transferSetting.BufferSize);

                                    //建立對應大小的buffer
                                    byte[] buffer = new byte[chunkSize];

                                    //從檔案流讀取至buffer
                                    fileStream.Read(buffer);

                                    //發送封包給接收端
                                    networkStream.SendSplitPacket(buffer);
                                }
                                break;

                            case eStatus.Break:
                                dto.Status = (dto.Status != eStatus.Delete) ? eStatus.Start : dto.Status;
                                //重新加入待處理隊列
                                this.ProgressQueue.Enqueue(dto);
                                isDone = true;
                                break;

                            case eStatus.Complete: //接收端回覆接收完成(檔案已存在而且檔案大小及修改時間一樣)
                                dto.Status = eStatus.Complete;
                                isDone = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：exist.", dto.FullName);
                                break;

                            default:
                                throw new Exception($"file：\"{dto.FullName}\"：unknown status：{receivePacket.Status}.");
                        }
                    }

                    //移除該檔案的錯誤紀錄(如果有的話)
                    _ = this.ExceptionCountDictionary.Remove(dto.FullName);
                }
            }
            catch (Exception ex)
            {
                //如果該檔案的錯誤紀錄超過3次
                if (this.ExceptionCountDictionary.TryGetValue(dto.FullName, out int failCt) && failCt > 3)
                {
                    this._logger.LogError(MyEventId.ERROR, ex, "file：\"{0}\"：task canceled due to retry more than three times.", dto.FullName);
                }
                else //否則重新加入待處理隊列
                {
                    this.ProgressQueue.Enqueue(dto);
                }

                //該檔案錯誤紀錄 + 1
                this.ExceptionCountDictionary[dto.FullName] = failCt + 1;
            }
        }

        /// <summary>
        /// 傳送檔案
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private void ProgressFile(FileInfoDto dto)
        {
            try
            {
                using TcpClient client = new()
                {
                    SendBufferSize = 64 * 1024 //傳送buffer大小
                };

                //連結至接收端
                client.Connect(_endPoint.Address, _endPoint.Port);

                //取得網路流
                using NetworkStream networkStream = client.GetStream();

                //相對路徑
                string relativePath = Path.GetRelativePath(_transferSetting.Path, dto.FullName);

                string path = relativePath;

                if (!string.IsNullOrWhiteSpace(_transferSetting.ReceiveDirectory))
                {
                    path = Path.Combine(_transferSetting.ReceiveDirectory, path);
                }

                //刪除
                if (dto.Status == eStatus.Delete)
                {
                    //送出刪除訊息
                    networkStream.SendDeletePacket(path);

                    this._logger.LogInformation(MyEventId.DELETE, "file：\"{0}\"：delete.", dto.FullName);
                }
                else
                {
                    bool isDone = false;

                    //送出開始傳送封包(檔案路徑+檔案大小)
                    networkStream.SendStartPacket(path, dto.Length, dto.LastWriteTime);

                    using FileStream fileStream = new FileStream(dto.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                    while (!isDone)
                    {
                        //取得接收端回應封包
                        PacketDto receivePacket = networkStream.GetPacket();

                        switch (receivePacket.Status)
                        {
                            //繼續
                            case eStatus.Continue:
                                //確認接收端收到資料長度
                                long recvevieLength = receivePacket.FileLength;

                                //如果資料長度小於傳送檔案長度，表示還沒傳送完，繼續接收檔案
                                if (recvevieLength < fileStream.Length)
                                {
                                    //如果已接收的長度不等於目前檔案流的指標
                                    if (recvevieLength != fileStream.Position)
                                    {
                                        //將指標移動到當前傳輸檔案流位置
                                        fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                    }

                                    //計算尚未傳輸的bytes長度
                                    long byteLeft = (fileStream.Length - recvevieLength);

                                    //計算buffer大小
                                    int chunkSize = (int)Math.Min(byteLeft, this._transferSetting.BufferSize);

                                    //建立對應大小的buffer
                                    Span<byte> buffer = new byte[chunkSize];

                                    //從檔案流讀取至buffer
                                    fileStream.Read(buffer);

                                    //發送封包給接收端
                                    networkStream.SendSplitPacket(buffer);

                                    //計算傳送進度
                                    //var progressPercent = (int)Math.Ceiling((double)recvevieLength / (double)fileStream.Length * 100);

                                    //if (progressPercent % 10 == 0)
                                    //{
                                    //    this._logger.LogInformation(EventIds.SUCCESS, "file：\"{0}\"：{1}%.", dto.FullName, progressPercent);
                                    //}
                                }
                                else
                                {
                                    //送出完成訊息給接收端
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：received.", dto.FullName);
                                }
                                break;
                            //比對
                            case eStatus.Compare:
                                //比對接收端傳送的哈希值是否相符
                                bool isEquals = this._fileHashService.CompareHash(receivePacket.FileHash, fileStream);
                                //如果相符
                                if (isEquals)
                                {
                                    //送出傳輸完成訊息
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：not changed.", dto.FullName);
                                }
                                else
                                {
                                    //如果已接收的長度不等於目前檔案流的指標
                                    if (fileStream.Position != 0L)
                                    {
                                        //將指標移動到當前傳輸檔案流位置
                                        fileStream.Seek(0L, SeekOrigin.Begin);
                                    }

                                    //計算chunk大小
                                    int chunkSize = (int)Math.Min(fileStream.Length, this._transferSetting.BufferSize);

                                    //建立對應大小的buffer
                                    Span<byte> buffer = new byte[chunkSize];

                                    //從檔案流讀取至buffer
                                    fileStream.Read(buffer);

                                    //發送封包給接收端
                                    networkStream.SendSplitPacket(buffer);
                                }
                                break;

                            case eStatus.Break:
                                dto.Status = (dto.Status != eStatus.Delete) ? eStatus.Start : dto.Status;
                                //重新加入待處理隊列
                                this.ProgressQueue.Enqueue(dto);
                                isDone = true;
                                break;

                            case eStatus.Complete: //接收端回覆接收完成(檔案已存在而且檔案大小及修改時間一樣)
                                dto.Status = eStatus.Complete;
                                isDone = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：exist.", dto.FullName);
                                break;

                            default:
                                throw new Exception($"file：\"{dto.FullName}\"：unknown status：{receivePacket.Status}.");
                        }
                    }

                    //移除該檔案的錯誤紀錄(如果有的話)
                    _ = this.ExceptionCountDictionary.Remove(dto.FullName);
                }
            }
            catch (Exception ex)
            {
                //如果該檔案的錯誤紀錄超過3次
                if (this.ExceptionCountDictionary.TryGetValue(dto.FullName, out int failCt) && failCt > 3)
                {
                    this._logger.LogError(MyEventId.ERROR, ex, "file：\"{0}\"：task canceled due to retry more than three times.", dto.FullName);
                }
                else //否則重新加入待處理隊列
                {
                    this.ProgressQueue.Enqueue(dto);
                }

                //該檔案錯誤紀錄 + 1
                this.ExceptionCountDictionary[dto.FullName] = failCt + 1;
            }
            finally
            {
                //釋放
                this._semaphore.Release();
            }
        }

        /// <summary>
        /// 服務啟動時執行
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            //委派檔案新增事件
            this._watcher.Created += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            //委派檔案變動事件
            this._watcher.Changed += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            //委派檔案刪除事件
            this._watcher.Deleted += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            // 設定是否啟動元件，必須要設定為 true，否則事件不會被觸發
            this._watcher.EnableRaisingEvents = true;

            this._logger.LogInformation(MyEventId.START, "service start.");

            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 服務主要執行序
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //啟動爬檔案工作
            _ = this.CrawlFilesAsync(stoppingToken);

            //啟動處理檔案事件工作
            _ = this.HandleEventsAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                //如果等待序列不是空的，取出一筆檔案處理
                if (!this.ProgressQueue.IsEmpty && this.ProgressQueue.TryDequeue(out FileInfoDto dto))
                {
                    //排隊
                    await this._semaphore.WaitAsync(stoppingToken);

                    _ = Task.Run(() => this.ProgressFile(dto), stoppingToken);
                    //_ = this.ProgressFileAsync(dto, stoppingToken)
                    //    .ContinueWith(_ => this._semaphore.Release());
                }
            }
        }

        /// <summary>
        /// 服務停止時執行
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            this._logger.LogInformation(MyEventId.STOP, "service stop.");

            return base.StopAsync(cancellationToken);
        }
    }
}