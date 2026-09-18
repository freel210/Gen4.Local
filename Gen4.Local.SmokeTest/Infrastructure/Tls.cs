using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Gen4.Local.SmokeTest.Infrastructure;

public static class Tls
{
    public static HttpClientHandler CreateHandler(string certsRoot)
    {
        var rootCa = new X509Certificate2(Path.Combine(certsRoot, "root-ca-cert.pem"));

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, chain, _) =>
                IsTrusted(certificate, chain, rootCa),
        };
    }

    private static bool IsTrusted(X509Certificate? certificate, X509Chain? chain, X509Certificate2 rootCa)
    {
        if (certificate is null)
        {
            return false;
        }

        X509Certificate2 certificate2;
        try
        {
            certificate2 = (X509Certificate2)certificate;
        }
        catch (InvalidCastException)
        {
            certificate2 = new X509Certificate2(certificate);
        }

        if (chain is null)
        {
            return false;
        }

        using var validationChain = new X509Chain();
        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validationChain.ChainPolicy.CustomTrustStore.Add(rootCa);
        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return validationChain.Build(certificate2);
    }
}