using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Server.Activity;

public static class RuntimeActivityServices
{
    /// <summary>
    /// Registers the runtime request log and its writer, and wraps the alert sinks registered so far with
    /// <see cref="RuntimeErrorRecordingSink"/>. Call after the alert sinks are registered.
    /// </summary>
    public static IServiceCollection AddRuntimeActivityLog(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeActivityLog>();
        services.AddSingleton<RuntimeActivityWriterService>();
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeActivityWriterService>());

        var exceptionSink = services.Last(d => d.ServiceType == typeof(IExceptionAlertSink));
        var storageSink = services.Last(d => d.ServiceType == typeof(INetworkStorageErrorAlertSink));
        services.Remove(exceptionSink);
        services.Remove(storageSink);
        services.AddSingleton(sp => new RuntimeErrorRecordingSink(
            Resolve<IExceptionAlertSink>(sp, exceptionSink),
            Resolve<INetworkStorageErrorAlertSink>(sp, storageSink),
            sp.GetRequiredService<RuntimeActivityLog>()));
        services.AddSingleton<IExceptionAlertSink>(sp => sp.GetRequiredService<RuntimeErrorRecordingSink>());
        services.AddSingleton<INetworkStorageErrorAlertSink>(sp => sp.GetRequiredService<RuntimeErrorRecordingSink>());
        return services;
    }

    private static T Resolve<T>(IServiceProvider services, ServiceDescriptor descriptor) => (T)(descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(services)
        ?? ActivatorUtilities.GetServiceOrCreateInstance(services, descriptor.ImplementationType!));
}
