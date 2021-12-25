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
        /// ���������ƥ�
        /// https://docs.microsoft.com/zh-tw/dotnet/api/system.threading.semaphoreslim?view=net-5.0
        /// </summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>
        /// TCP��ť
        /// </summary>
        private readonly TcpListener _listener;

        /// <summary>
        /// Client���C
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
        /// �A�ȱҰʮɰ���
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
        /// �A�ȥD�n�����
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                //�p�G���C���O�Ū��A���X�@��Client���B�z
                if (!this.ClientQueue.IsEmpty && this.ClientQueue.TryDequeue(out TcpClient client))
                {
                    //�ƶ�
                    await this._semaphore.WaitAsync(stoppingToken);

                    // _ = Task.Run(() => this.HandleClient(client), stoppingToken);

                    //Task�B�zClient(������)
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
        /// �A�Ȱ���ɰ���
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            this._listener.Stop();

            this._logger.LogInformation(MyEventId.STOP, "service stop.");

            //�p�G���C�̭����|���B�z��
            while (!this.ClientQueue.IsEmpty)
            {
                try
                {
                    //�q���C���XClient
                    this.ClientQueue.TryDequeue(out TcpClient client);

                    //���o�����y
                    await using NetworkStream networkStream = client.GetStream();

                    //�e�X���_�T�����ǰe��
                    networkStream.SendBreakPacket();

                    //����
                    client.Close();
                }
                catch { }
            }

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// �����s�u�B�NClient�[�J���C
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
                //���Գs�u
                TcpClient client = await this._listener.AcceptTcpClientAsync();

                //�]�w����BufferSize
                client.ReceiveBufferSize = this._receiverSetting.BufferSize;

                //�NClient�[�J���C
                this.ClientQueue.Enqueue(client);
            }
        }

        /// <summary>
        /// �B�zClient(�P�B����)
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

                //�q��ƬyŪ���ǰe�ݰe�Ӫ����
                filePath = Path.Combine(this._receiverSetting.SavePath, Encoding.UTF8.GetString(firstPacket.Buffer));

                if (firstPacket.Status is eStatus.Start)
                {
                    FileInfo fileInfo = new FileInfo(filePath);

                    if (!Directory.Exists(fileInfo.DirectoryName))
                        Directory.CreateDirectory(fileInfo.DirectoryName);

                    using FileStream fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

                    //�p�G�ɮפw�g�s�b�B�ɮפj�p�@��
                    if (firstPacket.Status == eStatus.Start && fileInfo.Exists && fileInfo.Length == firstPacket.FileLength)
                    {
                        //�p���ɮ׫��ƭ�
                        long fileHash = this._fileHashService.ComputeHash(fileStream);

                        //�e�X���T���ܶǰe��
                        networkStream.SendComparePacket(fileHash);
                    }
                    else
                    {
                        //�e�X�~��T�����ܶǰe��
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
                                //�p�G�w���������פ�����ثe�ɮ׬y������
                                if (recvevieLength != fileStream.Position)
                                {
                                    //�N���в��ʨ��e�ǿ��ɮ׬y��m
                                    fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                }

                                //�q��Ƭy������e�x�s���ɮ׬y��
                                fileStream.Write(receivePacket.Buffer);

                                //�p��ثe�w����������
                                recvevieLength += receivePacket.Buffer.Length;

                                //�ǰe�ثe�w����������
                                networkStream.SendContinuePacket(recvevieLength);
                                break;

                            case eStatus.Complete:
                                isBreakLoop = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gcomplete.", filePath);
                                break;

                            case eStatus.Break:
                                isBreakLoop = true;
                                this._logger.LogInformation(MyEventId.WARNING, "file�G\"{0}\"�Gcanceled.", filePath);
                                break;

                            default:
                                isBreakLoop = true;
                                this._logger.LogError(MyEventId.STOP, "file�G\"{0}\"�Gunknown status�G{1}.", filePath, receivePacket.Status);
                                break;
                        }
                    }
                }
                else if (firstPacket.Status is eStatus.Delete)
                {
                    if (TryDeleteFile(filePath))
                        this._logger.LogInformation(MyEventId.DELETE, "file�G\"{0}\"�Gdeleted.", filePath);

                    if (TryDeleteFolder(filePath))
                        this._logger.LogInformation(MyEventId.DELETE, "folder�G\"{0}\"�Gdeleted.", filePath);
                }
                else
                {
                    throw new Exception($"file�G\"{filePath}\"�Greceive unknown status�G{firstPacket.Status}.");
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(MyEventId.EXCEPTION, ex, "file�G\"{0}\"�Gexception.", filePath);

                networkStream.SendBreakPacket();
            }
            finally
            {
                client.Close();
                this._semaphore.Release();
            }
        }

        /// <summary>
        /// �B�zClient(�D�P�B����)
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken = default)
        {
            await using NetworkStream networkStream = client.GetStream();

            string relativePath, absolutePath = null;
            //�O�_�R���������������ɮ�
            bool isInComplete = false;
            try
            {
                //�Ұʱ����ʥ]���u�@
                Task<PacketDto> getPacketTask = networkStream.GetPacketAsync(stoppingToken);

                //�p�G�A�Ȱ���
                if (stoppingToken.IsCancellationRequested)
                {
                    //�e�X���_�T�����ǰe��
                    networkStream.SendBreakPacket();
                }
                else
                {
                    //���Ա��������ʥ]���u�@����
                    await getPacketTask;

                    //�۹���|
                    relativePath = Encoding.UTF8.GetString(getPacketTask.Result.Buffer);

                    //������|
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
                                && fileInfo.Exists //�ɮפw�g�s�b
                                && fileInfo.Length == getPacketTask.Result.FileLength //�B�ɮפj�p�@��
                                )
                            {
                                lastWriteTime = getPacketTask.Result.LastWriteTime;

                                //�ɮ׭ק�ɶ��@�� => ���L
                                if (fileInfo.LastWriteTime == lastWriteTime)
                                {
                                    networkStream.SendCompletePacket();
                                    isInComplete = false;
                                }
                                else
                                {
                                    //�p���ɮ׫��ƭ�
                                    long fileHash = await this._fileHashService.ComputeHashAsync(fileStream, stoppingToken).ConfigureAwait(false);

                                    //�e�X���T���ܶǰe��
                                    networkStream.SendComparePacket(fileHash);
                                }
                            }
                            else
                            {
                                //�e�X�~��T�����ܶǰe��
                                networkStream.SendContinuePacket(0L);
                            }

                            if (isInComplete)
                            {
                                //�O�_���X�j��
                                bool isBreakLoop = false;
                                //�w����������
                                long recvevieLength = 0L;
                                //�Ұʱ����U�@�]�ʥ]���u�@
                                getPacketTask = networkStream.GetPacketAsync(stoppingToken);

                                while (!isBreakLoop)
                                {
                                    //���Ա��������ʥ]���u�@����
                                    await getPacketTask;

                                    if (stoppingToken.IsCancellationRequested)
                                    {
                                        //�e�X���_�T�����ǰe��
                                        networkStream.SendBreakPacket();
                                        break;
                                    }

                                    switch (getPacketTask.Result.Status)
                                    {
                                        case eStatus.Split: //����
                                            if (fileStream.Position != recvevieLength)
                                            {
                                                //�N�ɮ׫��в��ʨ�ثe����������
                                                fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                            }

                                            //�}�l�g�J�ɮ׬y�u�@
                                            var writeFileTask = fileStream.WriteAsync(getPacketTask.Result.Buffer, stoppingToken);

                                            //�[�`�ثe�w����������
                                            recvevieLength += getPacketTask.Result.Buffer.Length;

                                            //�ǰe�ثe�w����������
                                            networkStream.SendContinuePacket(recvevieLength);

                                            //�Ұʱ����U�@�]�ʥ]���u�@
                                            getPacketTask = networkStream.GetPacketAsync(stoppingToken: stoppingToken);

                                            //���Լg�J�ɮ׬y�u�@����
                                            await writeFileTask;
                                            break;

                                        case eStatus.Complete: //��������
                                            isBreakLoop = true;
                                            this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gcomplete.", absolutePath);
                                            isInComplete = false;
                                            break;

                                        case eStatus.Break: //���_
                                            isBreakLoop = true;
                                            this._logger.LogInformation(MyEventId.WARNING, "file�G\"{0}\"�Gcanceled.", absolutePath);
                                            break;

                                        default:
                                            throw new ArgumentException($"file�G\"{absolutePath}\"�Gunknown status�G{getPacketTask.Result.Status}."
                                                , nameof(getPacketTask.Result.Status));
                                    }
                                }
                            }
                        }

                        if (lastWriteTime.HasValue && fileInfo.LastWriteTime != lastWriteTime)
                            fileInfo.LastWriteTime = lastWriteTime.Value;
                    }
                    else if (getPacketTask.Result.Status is eStatus.Delete) //�R��
                    {
                        isInComplete = false;

                        if (TryDeleteFile(absolutePath))
                            this._logger.LogInformation(MyEventId.DELETE, "file�G\"{0}\"�Gdeleted.", absolutePath);

                        if (TryDeleteFolder(absolutePath))
                            this._logger.LogInformation(MyEventId.DELETE, "folder�G\"{0}\"�Gdeleted.", absolutePath);
                    }
                    else
                    {
                        throw new ArgumentException($"file�G\"{absolutePath}\"�Gunknown status�G{getPacketTask.Result.Status}."
                            , nameof(getPacketTask.Result.Status));
                    }
                }
            }
            catch (Exception ex)
            {
                this._logger.LogError(MyEventId.EXCEPTION, ex, "file�G\"{0}\"�Gexception.", absolutePath);

                networkStream.SendBreakPacket();
            }

            //�p�G�|���ǿ駹���A�R���ɮ�
            if (isInComplete)
            {
                if (TryDeleteFile(absolutePath))
                    this._logger.LogInformation(MyEventId.DELETE, "file�G\"{0}\"�Gdeleted due to exception.", absolutePath);
            }
        }

        /// <summary>
        /// �R���ɮ�
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>�O�_���\�R��</returns>
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
        /// �R���ؿ�
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns>�O�_���\�R��</returns>
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