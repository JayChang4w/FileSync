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
        /// ���������ƥ�
        /// https://docs.microsoft.com/zh-tw/dotnet/api/system.threading.semaphoreslim?view=net-5.0
        /// </summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>
        /// �ɮ׺ʱ�
        /// </summary>
        private readonly FileSystemWatcher _watcher;

        /// <summary>
        /// �O���ɮ׿��~����Dictionary
        /// </summary>
        private readonly Dictionary<string, int> ExceptionCountDictionary = new Dictionary<string, int>();

        /// <summary>
        /// �ɮרƥ󶤦C
        /// </summary>
        private readonly UniqueQueue<FileEventDto> EventsQueue = new UniqueQueue<FileEventDto>();

        /// <summary>
        /// �ݳB�z�ɮ׶��C
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
                // �]�w�n�ʬݪ���Ƨ�
                Path = this._transferSetting.Path,
                // �]�w�n�ʬݪ��ܧ�����
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                // �]�w�n�ʬݪ��ɮ�����
                Filter = this._transferSetting.Filter,
                // �]�w�O�_�ʬݤl��Ƨ�
                IncludeSubdirectories = true
            };
        }

        /// <summary>
        /// ���ؿ��U���Ҧ��ɮ�
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

                    //�ƤJ���Ȧܶ��C
                    this.ProgressQueue.Enqueue(dto);
                }

                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
        }

        /// <summary>
        /// �B�z�ɮרƥ�
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        private async Task HandleEventsAsync(CancellationToken stoppingToken = default)
        {
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                //�p�G���C���O�Ū��A���X�@���ƥ󰵳B�z
                while (this.EventsQueue.TryDequeue(out FileEventDto eventDto))
                {
                    //�p�G�O�s�W���ܰʨƥ�
                    if (eventDto.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Changed)
                    {
                        FileInfo fileInfo = new FileInfo(eventDto.FullPath);

                        if (fileInfo?.Exists == true)
                        {
                            FileInfoDto dto = new FileInfoDto(fileInfo, eStatus.Start);

                            this.ProgressQueue.Enqueue(dto);
                        }
                    }
                    //�p�G�O�R���ƥ�
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
        /// �ǰe�ɮ�
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
                    SendBufferSize = 64 * 1024 //�ǰebuffer�j�p
                };

                //�s���ܱ�����
                client.Connect(_endPoint.Address, _endPoint.Port);

                //���o�����y
                await using NetworkStream networkStream = client.GetStream();

                //�۹���|
                string relativePath = Path.GetRelativePath(_transferSetting.Path, dto.FullName);

                string path = relativePath;

                if (!string.IsNullOrWhiteSpace(_transferSetting.ReceiveDirectory))
                {
                    path = Path.Combine(_transferSetting.ReceiveDirectory, path);
                }

                //�R��
                if (dto.Status == eStatus.Delete)
                {
                    //�e�X�R���T��
                    networkStream.SendDeletePacket(path);

                    this._logger.LogInformation(MyEventId.DELETE, "file�G\"{0}\"�Gdelete.", dto.FullName);
                }
                else
                {
                    bool isDone = false;

                    //�e�X�}�l�ǰe�ʥ](�ɮ׸��|+�ɮפj�p)
                    networkStream.SendStartPacket(path, dto.Length, dto.LastWriteTime);

                    await using FileStream fileStream = new FileStream(dto.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                    while (!isDone)
                    {
                        //���o�����ݦ^���ʥ]
                        PacketDto receivePacket = networkStream.GetPacket();

                        switch (receivePacket.Status)
                        {
                            //�~��
                            case eStatus.Continue:
                                //�T�{�����ݦ����ƪ���
                                long recvevieLength = receivePacket.FileLength;

                                //�p�G��ƪ��פp��ǰe�ɮת��סA����٨S�ǰe���A�~�򱵦��ɮ�
                                if (recvevieLength < fileStream.Length)
                                {
                                    //�p�G�w���������פ�����ثe�ɮ׬y������
                                    if (recvevieLength != fileStream.Position)
                                    {
                                        //�N���в��ʨ��e�ǿ��ɮ׬y��m
                                        fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                    }

                                    //�p��|���ǿ骺bytes����
                                    long byteLeft = (fileStream.Length - recvevieLength);

                                    //�p��buffer�j�p
                                    int chunkSize = (int)Math.Min(byteLeft, this._transferSetting.BufferSize);

                                    //�إ߹����j�p��buffer
                                    byte[] buffer = new byte[chunkSize];

                                    //�q�ɮ׬yŪ����buffer
                                    fileStream.Read(buffer);

                                    //�o�e�ʥ]��������
                                    networkStream.SendSplitPacket(buffer);

                                    //�p��ǰe�i��
                                    //var progressPercent = (int)Math.Ceiling((double)recvevieLength / (double)fileStream.Length * 100);

                                    //if (progressPercent % 10 == 0)
                                    //{
                                    //    this._logger.LogInformation(EventIds.SUCCESS, "file�G\"{0}\"�G{1}%.", dto.FullName, progressPercent);
                                    //}
                                }
                                else
                                {
                                    //�e�X�����T����������
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Greceived.", dto.FullName);
                                }
                                break;
                            //���
                            case eStatus.Compare:
                                //��ﱵ���ݶǰe�����ƭȬO�_�۲�
                                bool isEquals = this._fileHashService.CompareHash(receivePacket.FileHash, fileStream);
                                //�p�G�۲�
                                if (isEquals)
                                {
                                    //�e�X�ǿ駹���T��
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gnot changed.", dto.FullName);
                                }
                                else
                                {
                                    //�p�G�w���������פ�����ثe�ɮ׬y������
                                    if (fileStream.Position != 0L)
                                    {
                                        //�N���в��ʨ��e�ǿ��ɮ׬y��m
                                        fileStream.Seek(0L, SeekOrigin.Begin);
                                    }

                                    //�p��chunk�j�p
                                    int chunkSize = (int)Math.Min(fileStream.Length, this._transferSetting.BufferSize);

                                    //�إ߹����j�p��buffer
                                    byte[] buffer = new byte[chunkSize];

                                    //�q�ɮ׬yŪ����buffer
                                    fileStream.Read(buffer);

                                    //�o�e�ʥ]��������
                                    networkStream.SendSplitPacket(buffer);
                                }
                                break;

                            case eStatus.Break:
                                dto.Status = (dto.Status != eStatus.Delete) ? eStatus.Start : dto.Status;
                                //���s�[�J�ݳB�z���C
                                this.ProgressQueue.Enqueue(dto);
                                isDone = true;
                                break;

                            case eStatus.Complete: //�����ݦ^�б�������(�ɮפw�s�b�ӥB�ɮפj�p�έק�ɶ��@��)
                                dto.Status = eStatus.Complete;
                                isDone = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gexist.", dto.FullName);
                                break;

                            default:
                                throw new Exception($"file�G\"{dto.FullName}\"�Gunknown status�G{receivePacket.Status}.");
                        }
                    }

                    //�������ɮת����~����(�p�G������)
                    _ = this.ExceptionCountDictionary.Remove(dto.FullName);
                }
            }
            catch (Exception ex)
            {
                //�p�G���ɮת����~�����W�L3��
                if (this.ExceptionCountDictionary.TryGetValue(dto.FullName, out int failCt) && failCt > 3)
                {
                    this._logger.LogError(MyEventId.ERROR, ex, "file�G\"{0}\"�Gtask canceled due to retry more than three times.", dto.FullName);
                }
                else //�_�h���s�[�J�ݳB�z���C
                {
                    this.ProgressQueue.Enqueue(dto);
                }

                //���ɮ׿��~���� + 1
                this.ExceptionCountDictionary[dto.FullName] = failCt + 1;
            }
        }

        /// <summary>
        /// �ǰe�ɮ�
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
                    SendBufferSize = 64 * 1024 //�ǰebuffer�j�p
                };

                //�s���ܱ�����
                client.Connect(_endPoint.Address, _endPoint.Port);

                //���o�����y
                using NetworkStream networkStream = client.GetStream();

                //�۹���|
                string relativePath = Path.GetRelativePath(_transferSetting.Path, dto.FullName);

                string path = relativePath;

                if (!string.IsNullOrWhiteSpace(_transferSetting.ReceiveDirectory))
                {
                    path = Path.Combine(_transferSetting.ReceiveDirectory, path);
                }

                //�R��
                if (dto.Status == eStatus.Delete)
                {
                    //�e�X�R���T��
                    networkStream.SendDeletePacket(path);

                    this._logger.LogInformation(MyEventId.DELETE, "file�G\"{0}\"�Gdelete.", dto.FullName);
                }
                else
                {
                    bool isDone = false;

                    //�e�X�}�l�ǰe�ʥ](�ɮ׸��|+�ɮפj�p)
                    networkStream.SendStartPacket(path, dto.Length, dto.LastWriteTime);

                    using FileStream fileStream = new FileStream(dto.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                    while (!isDone)
                    {
                        //���o�����ݦ^���ʥ]
                        PacketDto receivePacket = networkStream.GetPacket();

                        switch (receivePacket.Status)
                        {
                            //�~��
                            case eStatus.Continue:
                                //�T�{�����ݦ����ƪ���
                                long recvevieLength = receivePacket.FileLength;

                                //�p�G��ƪ��פp��ǰe�ɮת��סA����٨S�ǰe���A�~�򱵦��ɮ�
                                if (recvevieLength < fileStream.Length)
                                {
                                    //�p�G�w���������פ�����ثe�ɮ׬y������
                                    if (recvevieLength != fileStream.Position)
                                    {
                                        //�N���в��ʨ��e�ǿ��ɮ׬y��m
                                        fileStream.Seek(recvevieLength, SeekOrigin.Begin);
                                    }

                                    //�p��|���ǿ骺bytes����
                                    long byteLeft = (fileStream.Length - recvevieLength);

                                    //�p��buffer�j�p
                                    int chunkSize = (int)Math.Min(byteLeft, this._transferSetting.BufferSize);

                                    //�إ߹����j�p��buffer
                                    Span<byte> buffer = new byte[chunkSize];

                                    //�q�ɮ׬yŪ����buffer
                                    fileStream.Read(buffer);

                                    //�o�e�ʥ]��������
                                    networkStream.SendSplitPacket(buffer);

                                    //�p��ǰe�i��
                                    //var progressPercent = (int)Math.Ceiling((double)recvevieLength / (double)fileStream.Length * 100);

                                    //if (progressPercent % 10 == 0)
                                    //{
                                    //    this._logger.LogInformation(EventIds.SUCCESS, "file�G\"{0}\"�G{1}%.", dto.FullName, progressPercent);
                                    //}
                                }
                                else
                                {
                                    //�e�X�����T����������
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Greceived.", dto.FullName);
                                }
                                break;
                            //���
                            case eStatus.Compare:
                                //��ﱵ���ݶǰe�����ƭȬO�_�۲�
                                bool isEquals = this._fileHashService.CompareHash(receivePacket.FileHash, fileStream);
                                //�p�G�۲�
                                if (isEquals)
                                {
                                    //�e�X�ǿ駹���T��
                                    networkStream.SendCompletePacket();

                                    dto.Status = eStatus.Complete;
                                    isDone = true;

                                    this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gnot changed.", dto.FullName);
                                }
                                else
                                {
                                    //�p�G�w���������פ�����ثe�ɮ׬y������
                                    if (fileStream.Position != 0L)
                                    {
                                        //�N���в��ʨ��e�ǿ��ɮ׬y��m
                                        fileStream.Seek(0L, SeekOrigin.Begin);
                                    }

                                    //�p��chunk�j�p
                                    int chunkSize = (int)Math.Min(fileStream.Length, this._transferSetting.BufferSize);

                                    //�إ߹����j�p��buffer
                                    Span<byte> buffer = new byte[chunkSize];

                                    //�q�ɮ׬yŪ����buffer
                                    fileStream.Read(buffer);

                                    //�o�e�ʥ]��������
                                    networkStream.SendSplitPacket(buffer);
                                }
                                break;

                            case eStatus.Break:
                                dto.Status = (dto.Status != eStatus.Delete) ? eStatus.Start : dto.Status;
                                //���s�[�J�ݳB�z���C
                                this.ProgressQueue.Enqueue(dto);
                                isDone = true;
                                break;

                            case eStatus.Complete: //�����ݦ^�б�������(�ɮפw�s�b�ӥB�ɮפj�p�έק�ɶ��@��)
                                dto.Status = eStatus.Complete;
                                isDone = true;
                                this._logger.LogInformation(MyEventId.SUCCESS, "file�G\"{0}\"�Gexist.", dto.FullName);
                                break;

                            default:
                                throw new Exception($"file�G\"{dto.FullName}\"�Gunknown status�G{receivePacket.Status}.");
                        }
                    }

                    //�������ɮת����~����(�p�G������)
                    _ = this.ExceptionCountDictionary.Remove(dto.FullName);
                }
            }
            catch (Exception ex)
            {
                //�p�G���ɮת����~�����W�L3��
                if (this.ExceptionCountDictionary.TryGetValue(dto.FullName, out int failCt) && failCt > 3)
                {
                    this._logger.LogError(MyEventId.ERROR, ex, "file�G\"{0}\"�Gtask canceled due to retry more than three times.", dto.FullName);
                }
                else //�_�h���s�[�J�ݳB�z���C
                {
                    this.ProgressQueue.Enqueue(dto);
                }

                //���ɮ׿��~���� + 1
                this.ExceptionCountDictionary[dto.FullName] = failCt + 1;
            }
            finally
            {
                //����
                this._semaphore.Release();
            }
        }

        /// <summary>
        /// �A�ȱҰʮɰ���
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            //�e���ɮ׷s�W�ƥ�
            this._watcher.Created += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            //�e���ɮ��ܰʨƥ�
            this._watcher.Changed += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            //�e���ɮקR���ƥ�
            this._watcher.Deleted += (sender, e) => this.EventsQueue.Enqueue(new FileEventDto(e));

            // �]�w�O�_�Ұʤ���A�����n�]�w�� true�A�_�h�ƥ󤣷|�QĲ�o
            this._watcher.EnableRaisingEvents = true;

            this._logger.LogInformation(MyEventId.START, "service start.");

            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// �A�ȥD�n�����
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //�Ұʪ��ɮפu�@
            _ = this.CrawlFilesAsync(stoppingToken);

            //�ҰʳB�z�ɮרƥ�u�@
            _ = this.HandleEventsAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                //�p�G���ݧǦC���O�Ū��A���X�@���ɮ׳B�z
                if (!this.ProgressQueue.IsEmpty && this.ProgressQueue.TryDequeue(out FileInfoDto dto))
                {
                    //�ƶ�
                    await this._semaphore.WaitAsync(stoppingToken);

                    _ = Task.Run(() => this.ProgressFile(dto), stoppingToken);
                    //_ = this.ProgressFileAsync(dto, stoppingToken)
                    //    .ContinueWith(_ => this._semaphore.Release());
                }
            }
        }

        /// <summary>
        /// �A�Ȱ���ɰ���
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