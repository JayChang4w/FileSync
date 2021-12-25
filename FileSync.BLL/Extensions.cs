using FileSync.DOL;
using FileSync.DOL.Dto;
using System.Net.Sockets;
using System.Text;

namespace FileSync.BLL
{
    public static class Extensions
    {
        public static IEnumerable<byte[]> ReadAsChunks(this FileStream fileStream, int chunkSize)
        {
            byte[] buffer = new byte[chunkSize];
            while (fileStream.Read(buffer, 0, chunkSize) != 0) //reading chunkSize at a time
            {
                yield return buffer;
            }
        }

        public static byte[] ReadBytes(this NetworkStream networkStream, int length)
        {
            int offset = 0;
            int remaining = length;
            byte[] buffer = new byte[length];
            while (remaining > 0)
            {
                int read = networkStream.Read(buffer, offset, remaining);

                if (read <= 0)
                    throw new EndOfStreamException($"End of stream reached with {remaining} bytes left to read");

                remaining -= read;
                offset += read;
            }

            return buffer;
        }

        public static async Task<byte[]> ReadBytesAsync(this NetworkStream networkStream, int length, CancellationToken stoppingToken = default)
        {
            int offset = 0;
            int remaining = length;

            byte[] buffer = new byte[length];

            while (remaining > 0 && !stoppingToken.IsCancellationRequested)
            {
                int read = await networkStream.ReadAsync(buffer.AsMemory(offset, remaining), stoppingToken).ConfigureAwait(false);

                if (read <= 0)
                    throw new EndOfStreamException($"End of stream reached with {remaining} bytes left to read");

                remaining -= read;
                offset += read;
            }

            return buffer;
        }

        public static PacketDto GetPacket(this NetworkStream networkStream)
        {
            PacketDto packet = new PacketDto
            {
                //接收傳送端Command確認要執行的動作
                Status = (eStatus)networkStream.ReadByte() //1 bytes
            };

            ReadOnlySpan<byte> buffer;

            switch (packet.Status)
            {
                case eStatus.Start:
                    buffer = networkStream.ReadBytes(4); //4 bytes

                    packet.Buffer = networkStream.ReadBytes(BitConverter.ToInt32(buffer)); //n bytes (n=bufferLength)

                    buffer = networkStream.ReadBytes(8); //8 bytes

                    packet.FileLength = BitConverter.ToInt64(buffer);
                    break;

                case eStatus.Compare:
                    buffer = networkStream.ReadBytes(8); //8 bytes
                    packet.FileHash = BitConverter.ToInt64(buffer);
                    break;

                case eStatus.Continue:
                    buffer = networkStream.ReadBytes(8); //8 bytes

                    packet.FileLength = BitConverter.ToInt64(buffer);
                    break;

                case eStatus.Split:
                    buffer = networkStream.ReadBytes(4); //4 bytes

                    packet.Buffer = networkStream.ReadBytes(BitConverter.ToInt32(buffer)); //n bytes (n=bufferLength)
                    break;

                case eStatus.Break:
                case eStatus.Complete:
                    break;

                case eStatus.Delete:
                    buffer = networkStream.ReadBytes(4); //4 bytes

                    packet.Buffer = networkStream.ReadBytes(BitConverter.ToInt32(buffer)); //n bytes (n=bufferLength)
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(packet.Status), packet.Status, "不明的狀態");
            }

            return packet;
        }

        public static async Task<PacketDto> GetPacketAsync(this NetworkStream networkStream, CancellationToken stoppingToken = default)
        {
            PacketDto packet = new PacketDto
            {
                //接收傳送端Command確認要執行的動作
                Status = (eStatus)networkStream.ReadByte() //1 bytes
            };

            byte[] buffer;
            switch (packet.Status)
            {
                case eStatus.Start:
                case eStatus.Split:
                case eStatus.Delete:
                    //4 bytes
                    buffer = await networkStream.ReadBytesAsync(4, stoppingToken).ConfigureAwait(false);

                    int bufferLength = BitConverter.ToInt32(buffer);

                    //n bytes (n=bufferLength)
                    packet.Buffer = await networkStream.ReadBytesAsync(bufferLength, stoppingToken).ConfigureAwait(false);

                    if (packet.Status == eStatus.Start)
                    {
                        //8 bytes
                        buffer = await networkStream.ReadBytesAsync(8, stoppingToken).ConfigureAwait(false);

                        packet.FileLength = BitConverter.ToInt64(buffer);

                        //8 bytes
                        buffer = await networkStream.ReadBytesAsync(8, stoppingToken).ConfigureAwait(false);

                        packet.LastWriteTime = new DateTime(BitConverter.ToInt64(buffer));
                    }
                    break;

                case eStatus.Compare:
                    //8 bytes
                    buffer = await networkStream.ReadBytesAsync(8, stoppingToken).ConfigureAwait(false);
                    packet.FileHash = BitConverter.ToInt64(buffer);
                    break;

                case eStatus.Continue:
                    //8 bytes
                    buffer = await networkStream.ReadBytesAsync(8, stoppingToken).ConfigureAwait(false);
                    packet.FileLength = BitConverter.ToInt64(buffer);
                    break;

                case eStatus.Break:
                case eStatus.Complete:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(packet.Status), packet.Status, "不明的狀態");
            }

            return packet;
        }

        /// <summary>
        /// 發送封包(初始)
        /// </summary>
        /// <param name="networkStream"></param>
        /// <param name="relativePath">相對路徑</param>
        /// <param name="fileLength">檔案大小</param>
        /// <param name="lastWriteTime">最後修改時間</param>
        public static void SendStartPacket(this NetworkStream networkStream, string relativePath, long fileLength, DateTime lastWriteTime)
        {
            networkStream.SendPacket(eStatus.Start, buffer: Encoding.UTF8.GetBytes(relativePath), fileLength: fileLength, lastWriteTime: lastWriteTime);
        }

        /// <summary>
        /// 發送封包(比對哈希)
        /// </summary>
        /// <param name="networkStream"></param>
        /// <param name="fileHash"></param>
        public static void SendComparePacket(this NetworkStream networkStream, long fileHash)
        {
            networkStream.SendPacket(eStatus.Compare, fileHash: fileHash);
        }

        /// <summary>
        /// 發送封包(繼續)
        /// </summary>
        /// <param name="networkStream"></param>
        /// <param name="fileLength"></param>
        public static void SendContinuePacket(this NetworkStream networkStream, long fileLength)
        {
            networkStream.SendPacket(eStatus.Continue, fileLength: fileLength);
        }

        /// <summary>
        /// 發送封包(分割)
        /// </summary>
        /// <param name="networkStream"></param>
        /// <param name="buffer"></param>
        public static void SendSplitPacket(this NetworkStream networkStream, ReadOnlySpan<byte> buffer)
        {
            networkStream.SendPacket(eStatus.Split, buffer: buffer);
        }

        /// <summary>
        /// 發送封包(中斷)
        /// </summary>
        /// <param name="networkStream"></param>
        public static void SendBreakPacket(this NetworkStream networkStream)
        {
            networkStream.SendPacket(eStatus.Break);
        }

        /// <summary>
        /// 完成封包
        /// </summary>
        /// <param name="networkStream"></param>
        public static void SendCompletePacket(this NetworkStream networkStream)
        {
            networkStream.SendPacket(eStatus.Complete);
        }

        /// <summary>
        /// 刪除封包
        /// </summary>
        /// <param name="networkStream"></param>
        /// <param name="relativePath">相對路徑</param>
        public static void SendDeletePacket(this NetworkStream networkStream, string relativePath)
        {
            networkStream.SendPacket(eStatus.Delete, buffer: Encoding.UTF8.GetBytes(relativePath));
        }

        public static void SendPacket(this NetworkStream networkStream, eStatus status, ReadOnlySpan<byte> buffer = default, long? fileHash = null, long? fileLength = null, DateTime? lastWriteTime = null)
        {
            networkStream.WriteByte((byte)status);  //1 bytes

            switch (status)
            {
                case eStatus.Start:
                case eStatus.Split:
                case eStatus.Delete:
                    if (buffer == default)
                    {
                        throw new ArgumentException(null, nameof(buffer));
                    }

                    if (status == eStatus.Start && fileLength == null)
                    {
                        if (fileLength == null)
                            throw new ArgumentNullException(nameof(fileLength));
                        else if (fileLength <= 0)
                            throw new ArgumentOutOfRangeException(nameof(fileLength));

                        if (lastWriteTime == null)
                            throw new ArgumentNullException(nameof(lastWriteTime));
                    }

                    networkStream.Write(BitConverter.GetBytes((int)buffer.Length)); //4 bytes
                    networkStream.Write(buffer); //n bytes

                    if (status == eStatus.Start)
                    {
                        networkStream.Write(BitConverter.GetBytes((long)fileLength)); //8 bytes
                        networkStream.Write(BitConverter.GetBytes(lastWriteTime.Value.Ticks)); //8 bytes
                    }
                    break;

                case eStatus.Compare:
                    if (fileHash is null)
                    {
                        throw new ArgumentNullException(nameof(fileHash));
                    }

                    networkStream.Write(BitConverter.GetBytes((long)fileHash)); //8 bytes
                    break;

                case eStatus.Continue:
                    if (fileLength is null)
                    {
                        throw new ArgumentNullException(nameof(fileLength));
                    }

                    networkStream.Write(BitConverter.GetBytes((long)fileLength)); //8 bytes
                    break;

                case eStatus.Break:
                case eStatus.Complete:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "不明的狀態");
            }
        }
    }
}