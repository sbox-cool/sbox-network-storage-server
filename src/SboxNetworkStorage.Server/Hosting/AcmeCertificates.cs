using LettuceEncrypt;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Hosting;

/// <summary>Automatic Let's Encrypt certificates for <c>tls.mode = "acme"</c> (TLS-ALPN-01 on the HTTPS listener).</summary>
public static class AcmeCertificates
{
    public static void Register(IServiceCollection services, EffectiveConfig config)
    {
        var storage = Path.Combine(config.DataDirectory, "acme");
        Directory.CreateDirectory(storage);
        services.AddLettuceEncrypt(options =>
            {
                options.AcceptTermsOfService = config.GetBoolean("tls.acme_accept_terms");
                options.DomainNames = [config.GetString("tls.acme_domain")];
                options.EmailAddress = config.GetString("tls.acme_email");
            })
            .PersistDataToDirectory(new DirectoryInfo(storage), pfxPassword: null);
    }

    public static void UseHttps(ListenOptions listen, IServiceProvider services)
        => listen.UseHttps(https => https.UseLettuceEncrypt(services));
}
