using FileSync.BLL.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Standart.Hash.xxHash;


namespace FileSync.BLL.Service
{
    public static class FileHasherExtensions
    {
        public static long ToInt64(this ulong value)
        {
            return unchecked((long)value + long.MinValue);
        }

        public static ulong ToUInt64(this long value)
        {
            return unchecked((ulong)(value - long.MinValue));
        }
    }

    public class FileHashService : IFileHashService
    {
        public async ValueTask<long> ComputeHashAsync(Stream stream, int bufferSize = 8192, ulong seed = 0uL)
        {
            var uHash64 = await xxHash64.ComputeHashAsync(stream, bufferSize, seed);

            //轉成INT64
            return uHash64.ToInt64();
        }

        public async ValueTask<long> ComputeHashAsync(Stream stream, CancellationToken cancellationToken, int bufferSize = 8192, ulong seed = 0uL)
        {
            var uHash64 = await xxHash64.ComputeHashAsync(stream, bufferSize, seed, cancellationToken);

            //轉成INT64
            return uHash64.ToInt64();
        }

        public async Task<bool> CompareHashAsync(long source, Stream targetStream, int bufferSize = 8192, ulong seed = 0uL)
        {
            var uHash64 = await xxHash64.ComputeHashAsync(targetStream, bufferSize, seed);

            return uHash64.Equals(source.ToUInt64());
        }

        public long ComputeHash(Stream stream, int bufferSize = 8192, ulong seed = 0uL)
        {
            var uHash64 = xxHash64.ComputeHash(stream, bufferSize, seed);

            //轉成INT64
            return uHash64.ToInt64();
        }

        public bool CompareHash(long source, Stream targetStream, int bufferSize = 8192, ulong seed = 0uL)
        {
            var uHash64 = xxHash64.ComputeHash(targetStream, bufferSize, seed);

            return uHash64.Equals(source.ToUInt64());
        }
    }
}
