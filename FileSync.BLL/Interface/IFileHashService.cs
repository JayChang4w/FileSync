namespace FileSync.BLL.Interface
{
    public interface IFileHashService
    {
        Task<bool> CompareHashAsync(long source, Stream targetStream, int bufferSize = 8192, ulong seed = 0uL);
        ValueTask<long> ComputeHashAsync(Stream stream, int bufferSize = 8192, ulong seed = 0uL);
        ValueTask<long> ComputeHashAsync(Stream stream, CancellationToken cancellationToken, int bufferSize = 8192, ulong seed = 0uL);

        bool CompareHash(long source, Stream targetStream, int bufferSize = 8192, ulong seed = 0uL);
        long ComputeHash(Stream stream, int bufferSize = 8192, ulong seed = 0uL);
    }
}
