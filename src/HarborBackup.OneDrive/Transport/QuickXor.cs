using System.Buffers.Binary;
using System.Security.Cryptography;
namespace HarborBackup.OneDrive.Transport;

public sealed record TransferHashes(string Sha256, string QuickXor, long Length);

// Microsoft QuickXorHash, 160-bit circular shift by 11 per input byte, then little-endian length.
public static class QuickXor
{
    private sealed class Accumulator
    {
        private readonly byte[] hash = new byte[20];
        public long Length { get; private set; }
        public void Append(ReadOnlySpan<byte> bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var shift = (int)((Length + i) % 160) * 11 % 160;
                var slot = shift / 8; var bits = shift % 8;
                hash[slot] ^= (byte)(bytes[i] << bits);
                hash[(slot + 1) % 20] ^= (byte)(bytes[i] >> (8 - bits));
            }
            Length += bytes.Length;
        }
        public string Finish()
        {
            Span<byte> size = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(size, Length);
            for (var i = 0; i < 8; i++) hash[12 + i] ^= size[i];
            return Convert.ToBase64String(hash);
        }
    }
    public static async Task<string> ComputeAsync(Stream stream, CancellationToken token)
    {
        var hash = new Accumulator(); var buffer = new byte[131072]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0) hash.Append(buffer.AsSpan(0, count));
        return hash.Finish();
    }
    /// <summary>Compute both transfer checksums in a single sequential read; never seek or close the caller's stream.</summary>
    public static async Task<TransferHashes> ComputeTransferHashesAsync(Stream stream, CancellationToken token)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var quick = new Accumulator(); var buffer = new byte[131072]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            sha256.AppendData(buffer, 0, count);
            quick.Append(buffer.AsSpan(0, count));
        }
        return new(Convert.ToHexStringLower(sha256.GetHashAndReset()), quick.Finish(), quick.Length);
    }
}
