using System.Security.Cryptography;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;

using Stampd.Core.Sealing;

namespace Stampd.Engine.Sealing;

/// <summary>
/// Adapts an <see cref="ICryptographicSealingProvider"/> to BouncyCastle's
/// <see cref="ISignatureFactory"/> contract so the CMS construction layer can stay
/// provider-agnostic.
/// </summary>
/// <remarks>
/// BouncyCastle's CMS generator drives signing through a synchronous <c>IStreamCalculator</c>
/// abstraction. Our provider is async, so the calculator's <c>GetResult</c> bridges via
/// <c>GetAwaiter().GetResult()</c>. The whole engine pipeline is already running on a
/// thread-pool thread inside <see cref="PdfSharpStampdEngine.SignAsync"/>, so blocking
/// briefly while the provider returns is acceptable. HSM-backed providers can keep their
/// public <see cref="ICryptographicSealingProvider.SignAsync"/> implementations async.
/// </remarks>
internal sealed class ProviderSignatureFactory : ISignatureFactory
{
    private static readonly AlgorithmIdentifier Sha256WithRsa =
        new(PkcsObjectIdentifiers.Sha256WithRsaEncryption, DerNull.Instance);

    private readonly ICryptographicSealingProvider _provider;
    private readonly HashAlgorithmName _hashAlgorithm;

    public ProviderSignatureFactory(ICryptographicSealingProvider provider, HashAlgorithmName hashAlgorithm)
    {
        _provider = provider;
        _hashAlgorithm = hashAlgorithm;
    }

    public object AlgorithmDetails => Sha256WithRsa;

    public IStreamCalculator<IBlockResult> CreateCalculator()
        => new ProviderStreamCalculator(_provider, _hashAlgorithm);

    private sealed class ProviderStreamCalculator : IStreamCalculator<IBlockResult>, IDisposable
    {
        private readonly MemoryStream _buffer = new();
        private readonly ICryptographicSealingProvider _provider;
        private readonly HashAlgorithmName _hashAlgorithm;
        private bool _disposed;

        public ProviderStreamCalculator(ICryptographicSealingProvider provider, HashAlgorithmName hashAlgorithm)
        {
            _provider = provider;
            _hashAlgorithm = hashAlgorithm;
        }

        public Stream Stream => _buffer;

        public IBlockResult GetResult()
        {
            var data = _buffer.ToArray();
            var signature = _provider
                .SignAsync(data, _hashAlgorithm, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ByteArrayBlockResult(signature);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Dispose();
            _disposed = true;
        }
    }

    private sealed class ByteArrayBlockResult : IBlockResult
    {
        private readonly byte[] _bytes;

        public ByteArrayBlockResult(byte[] bytes) => _bytes = bytes;

        public byte[] Collect() => _bytes;

        public int Collect(byte[] destination, int offset)
        {
            Buffer.BlockCopy(_bytes, 0, destination, offset, _bytes.Length);
            return _bytes.Length;
        }

        public int Collect(Span<byte> destination)
        {
            _bytes.AsSpan().CopyTo(destination);
            return _bytes.Length;
        }

        public int GetMaxResultLength() => _bytes.Length;
    }
}
