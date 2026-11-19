using System.Security.Cryptography;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class TransferHashTests
{
    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead;
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(160)]
    [DataRow(131100)]
    public async Task TransferHashesMatchIndependentAlgorithmsInOnePass(int size)
    {
        var data = Enumerable.Range(0, size).Select(i => (byte)(i * 19)).ToArray();
        var expected = new byte[20];
        for (var i = 0; i < data.Length; i++)
            for (var bit = 0; bit < 8; bit++)
                if ((data[i] & (1 << bit)) != 0)
                {
                    var position = (i * 11 + bit) % 160;
                    expected[position / 8] ^= (byte)(1 << (position % 8));
                }
        var length = BitConverter.GetBytes((long)size);
        for (var i = 0; i < 8; i++) expected[12 + i] ^= length[i];
        using var input = new CountedStream(data);
        var hashes = await QuickXor.ComputeTransferHashesAsync(input, default);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(data)), hashes.Sha256);
        Assert.AreEqual(Convert.ToBase64String(expected), hashes.QuickXor);
        Assert.AreEqual((long)size, hashes.Length);
        Assert.AreEqual((long)size, input.BytesRead);
        Assert.IsTrue(input.CanRead);
    }

    [TestMethod]
    public async Task CancelledHashDoesNotConsumeOrDisposeInput()
    {
        using var input = new CountedStream(new byte[1024]);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => QuickXor.ComputeTransferHashesAsync(input, stop.Token));
        Assert.AreEqual(0L, input.BytesRead);
        Assert.IsTrue(input.CanRead);
    }
}
