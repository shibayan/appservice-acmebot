using System;
using System.Security.Cryptography.X509Certificates;

namespace AppService.Acmebot.Internal
{
    internal static class X509Certificate2Helper
    {
        private static ReadOnlySpan<byte> Separator => new byte[] { 0x0A, 0x0A };

        public static (X509Certificate2, X509Certificate2) LoadFromPem(byte[] rawData)
        {
            var rawDataSpan = rawData.AsSpan();

            var separator = rawDataSpan.IndexOf(Separator);

            var certificate = new X509Certificate2(rawDataSpan.Slice(0, separator).ToArray());
            var chainCertificate = new X509Certificate2(rawDataSpan.Slice(separator + 2).ToArray());

            return (certificate, chainCertificate);
        }
    }
}
