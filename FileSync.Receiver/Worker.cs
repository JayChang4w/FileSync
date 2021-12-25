using FileSync.BLL;
using FileSync.BLL.Interface;
using FileSync.DOL;
using FileSync.DOL.Dto;
using FileSync.DOL.Setting;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileSync.Receiver
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IFileHashService _fileHashService;
        private readonly IPEndPointSetting _endPointSetting;
        private readonly ReceiverSetting _receiverSetting;

        /// <summary>
        /// 限制執行緒數目
        /// https://docs.microsoft.com/zh-tw/dotnet/api/system.threading.semaphoreslim?view=net-5.0
        /// </summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>
        /// TCP監聽
        /// </summary>
        private readonly TcpListener _listener;

        /// <summary>
        /// Client隊列
        /// </summary>
        private readonly ConcurrentQueue<TcpClient> ClientQueue = new ConcurrentQueue<TcpClient>();

        public Worker(ILogger<Worker> logger,
                                     IFileHashService fileHashService,
                                     IPEndPointSetting endPointSetting,
                                     ReceiverSetting receiverSetting)
        {
            this._logger = logger;
            this._endPointSetting = endPointSetting;
            this._receiverSetting = receiverSetting;
            this._fileHashService = fileHashService;

            this._semaphore = new SemaphoreSlim(this._receiverSetting.MaxTaskCount);
            this._listener = new TcpListener(IPAddress.Any, this._endPointSetting.Port);
        }

        /// <summary>
        /// 服務啟動時執行
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            this._logger.LogInformation(MyEventId.START, "service start.");

            _ = this.AcceptClientsAsync(cancellationToken);

            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 服務主要執行序
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                //如果隊列不是空的，取出一筆Client做處理
                if (!this.ClientQueue.IsEmpty && this.ClientQueue.TryDequeue(out TcpClient client))
                {
                    //排隊
                    await this._semaphore.WaitAsync(stoppingToken);

                    // _ = Task.Run(() => this.HandleClient(client), stoppingToken);

                    //Task處理Client(不等候)
                    _ = this.HandleClientAsync(client, stoppingToken)
                        .ContinueWith((_) => client.Close())
                        .ContinueWith(_ => this._semaphore.Release());
                }
                else
                {
                    await Task.Delay(50);
                }
            }
        }

        /// <summary>
        /// 服務停止時執行
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            this._listener.Stop();

            this._logger.LogInformation(MyEventId.STOP, "service stop.");

            //如果隊列裡面有尚未處理的
            while (!this.ClientQueue.IsEmpty)
            {
                try
                {
                    //從隊列取出Client
                    this.ClientQueue.TryDequeue(out TcpClient client);

                    //取得網路流
                    await using NetworkStream networkStream = client.GetStream();

                    //送出中斷訊息給傳送端
                    networkStream.SendBreakPacket();

                    //關閉
                    client.Close();
                }
                catch { }
            }

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// 接受連線、將Client加入隊列
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();

            this._listener.Start();

            this._logger.LogInformation(MyEventId.START, "start listening tcp port {0}.", this._endPointSetting.Port);

            while (!cancellationToken.IsCancellationRequested)
            {
                //等候連線
                TcpClient client = await this._listener.AcceptTcpClientAsync();

                //設定接收BufferSize
                client.ReceiveBufferSize = this._receiverSetting.BufferSize;

                //將Client加入隊列
                this.ClientQueue.Enqueue(client);
            }
        }

        /// <summary>
        /// 處理Client(同步版本)
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        [Obsolete]
        private void HandleClient(TcpClient client)
        {
            using NetworkStream networkStream = client.GetStream();
            string filePath = null;
            try
            {
                PacketDto firstPacket = networkStream.GetPacket();

                //從資料流讀取傳送端送來的資料
                filePath = Path.Combine(this._receiverSetting.SavePath, Encoding.UTF8.GetString(firstPacket.Buffer));

                if (firstPacket.Status is eStatus.Start)
                {
                    FileInfo fileInfo = new FileInfo(filePath);

                    if (!Directory.Exists(fileInfo.DirectoryName))
                        Directory.CreateDirectory(fileInfo.DirectoryName);

                    using FileStream fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

                    //如果檔案已經存在且檔案大小一樣
                    if (firstPacket.Status == eStatus.Start && fileInfo.Exists && fileInfo.Length == firstPacket.FileLength)
                    {
                        //計算檔案哈希值
                        long fileHash = this._fileHashService.ComputeHash(fileStream);

                        //送出比對訊息至傳送端
                        networkStream.SendComparePacket(fileHash);
                    }
                    else
                    {
                        //送出繼續訊息給至傳送端
                        networkStream.SendContinuePacket(0L);
                    }

                    bool isBreakLoop = false;
                    long recvevieLength = 0L;
                    while (!isBreakLoop)
                    {
                        PacketDto receivePacket = networkStream.GetPacket();

                        switch (receivePacket.Status)
                        {
                            case eStatus.Split:
                                //如果已接收的長度不等於目前檔案流的指標
                                if (recvevieLength != fileStream.Position)
                                {
                                    //將指標移動到當前傳輸檔案流位置
                                    fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                }

                                //從資料流獲取內容儲存至檔案流中
                                fileStream.Write(receivePacket.Buffer);

                                //計算目前已接收的長度
                                recvevieLength += receivePacket.Buffer.Length;

                                //傳送目前已接收的長度
                                networkStream.SendContinuePacket(recvevieLength);
                                break;

                            case eStatus.Complete:
                                isBreakLoop = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：complete.", filePath);
                                break;

                            case eStatus.Break:
                                isBreakLoop = true;
                                this._logger.LogInformation(MyEventId.WARNING, "file：\"{0}\"：canceled.", filePath);
                                break;

                            default:
                                isBreakLoop = true;
                                this._logger.LogError(MyEventId.STOP, "file：\"{0}\"：unknown status：{1}.", filePath, receivePacket.Status);
                                break;
                        }
                    }
                }
                else if (firstPacket.Status is eStatus.Delete)
                {
                    if (TryDeleteFile(filePath))
                        this._logger.LogInformation(MyEventId.DELETE, "file：\"{0}\"：deleted.", filePath);

                    if (TryDeleteFolder(filePath))
                        this._logger.LogInformation(MyEventId.DELETE, "folder：\"{0}\"：deleted.", filePath);
                }
                else
                {
                    throw new Exception($"file：\"{filePath}\"：receive unknown status：{firstPacket.Status}.");
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(MyEventId.EXCEPTION, ex, "file：\"{0}\"：exception.", filePath);

                networkStream.SendBreakPacket();
            }
            finally
            {
                client.Close();
                this._semaphore.Release();
            }
        }

        /// <summary>
        /// 處理Client(非同步版本)
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken = default)
        {
            await using NetworkStream networkStream = client.GetStream();

            string relativePath, absolutePath = null;
            //是否刪除未接收完成的檔案
            bool isInComplete = false;
            try
            {
                //啟動接收封包的工作
                Task<PacketDto> getPacketTask = networkStream.GetPacketAsync(stoppingToken);

                //如果服務停止
                if (stoppingToken.IsCancellationRequested)
                {
                    //送出中斷訊息給傳送端
                    networkStream.SendBreakPacket();
                }
                else
                {
                    //等候接收接收封包的工作完成
                    await getPacketTask;

                    //相對路徑
                    relativePath = Encoding.UTF8.GetString(getPacketTask.Result.Buffer);

                    //絕對路徑
                    absolutePath = Path.Combine(this._receiverSetting.SavePath, relativePath);

                    if (getPacketTask.Result.Status is eStatus.Start)
                    {
                        DateTime? lastWriteTime = null;

                        FileInfo fileInfo = new FileInfo(absolutePath);

                        if (!Directory.Exists(fileInfo.DirectoryName))
                            Directory.CreateDirectory(fileInfo.DirectoryName);

                        await using (FileStream fileStream = new FileStream(absolutePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                        {

                            isInComplete = true;

                            if (getPacketTask.Result.Status == eStatus.Start
                                && fileInfo.Exists //檔案已經存在
                                && fileInfo.Length == getPacketTask.Result.FileLength //且檔案大小一樣
                                )
                            {
                                lastWriteTime = getPacketTask.Result.LastWriteTime;

                                //檔案修改時間一樣 => 跳過
                                if (fileInfo.LastWriteTime == lastWriteTime)
                                {
                                    networkStream.SendCompletePacket();
                                    isInComplete = false;
                                }
                                else
                                {
                                    //計算檔案哈希值
                                    long fileHash = await this._fileHashService.ComputeHashAsync(fileStream, stoppingToken).ConfigureAwait(false);

                                    //送出比對訊息至傳送端
                                    networkStream.SendComparePacket(fileHash);
                                }
                            }
                            else
                            {
                                //送出繼續訊息給至傳送端
                                networkStream.SendContinuePacket(0L);
                            }

                            if (isInComplete)
                            {
                                //是否跳出迴圈
                                bool isBreakLoop = false;
                                //已接收的長度
                                long recvevieLength = 0L;
                                //啟動接收下一包封包的工作
                                getPacketTask = networkStream.GetPacketAsync(stoppingToken);

                                while (!isBreakLoop)
                                {
                                    //等候接收接收封包的工作完成
                                    await getPacketTask;

                                    if (stoppingToken.IsCancellationRequested)
                                    {
                                        //送出中斷訊息給傳送端
                                        networkStream.SendBreakPacket();
                                        break;
                                    }

                                    switch (getPacketTask.Result.Status)
                                    {
                                        case eStatus.Split: //分割
                                            if (fileStream.Position != recvevieLength)
                                            {
                                                //將檔案指標移動到目前接收的長度
                                                fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                            }

                                            //開始寫入檔案流工作
                                            var writeFileTask = fileStream.WriteAsync(getPacketTask.Result.Buffer, stoppingToken);

                                            //加總目前已接收的長度
                                            recvevieLength += getPacketTask.Result.Buffer.Length;

                                            //傳送目前已接收的長度
                                            networkStream.SendContinuePacket(recvevieLength);

                                            //啟動接收下一包封包的工作
                                            getPacketTask = networkStream.GetPacketAsync(stoppingToken: stoppingToken);

                                            //等候寫入檔案流工作完成
                                            await writeFileTask;
                                            break;

                                        case eStatus.Complete: //接收完成
                                            isBreakLoop = true;
                                            this._logger.LogInformation(MyEventId.SUCCESS, "file：\"{0}\"：complete.", absolutePath);
                                            isInComplete = false;
                                            break;

                                        case eStatus.Break: //中斷
                                            isBreakLoop = true;
                                            this._logger.LogInformation(MyEventId.WARNING, "file：\"{0}\"：canceled.", absolutePath);
                                            break;

                                        default:
                                            throw new ArgumentException($"file：\"{absolutePath}\"：unknown status：{getPacketTask.Result.Status}."
                                                , nameof(getPacketTask.Result.Status));
                                    }
                                }
                            }
                        }

                        if (lastWriteTime.HasValue && fileInfo.LastWriteTime != lastWriteTime)
                            fileInfo.LastWriteTime = lastWriteTime.Value;
                    }
                    else if (getPacketTask.Result.Status is eStatus.Delete) //刪除
                    {
                        isInComplete = false;

                        if (TryDeleteFile(absolutePath))
                            this._logger.LogInformation(MyEventId.DELETE, "file：\"{0}\"：deleted.", absolutePath);

                        if (TryDeleteFolder(absolutePath))
                            this._logger.LogInformation(MyEventId.DELETE, "folder：\"{0}\"：deleted.", absolutePath);
                    }
                    else
                    {
                        throw new ArgumentException($"file：\"{absolutePath}\"：unknown status：{getPacketTask.Result.Status}."
                            , nameof(getPacketTask.Result.Status));
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(MyEventId.EXCEPTION, ex, "file：\"{0}\"：exception.", absolutePath);

                networkStream.SendBreakPacket();
            }

            //如果尚未傳輸完成，刪除檔案
            if (isInComplete)
            {
                if (TryDeleteFile(absolutePath))
                    this._logger.LogInformation(MyEventId.DELETE, "file：\"{0}\"：deleted due to exception.", absolutePath);
            }
        }

        /// <summary>
        /// 刪除檔案
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>是否成功刪除</returns>
        private bool TryDeleteFile(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);

                    return true;
                }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// 刪除目錄
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>是否成功刪除</returns>
        private bool TryDeleteFolder(string filePath)
        {
            try
            {
                if (Directory.Exists(filePath))
                {
                    /*
                        recursive: true to remove directories, subdirectories, and files in path; otherwise, false.
                     */
                    Directory.Delete(filePath, true);

                    return true;
                }
            }
            catch { }

            return false;
        }
    }
}