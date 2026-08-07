using Microsoft.Extensions.DependencyInjection;
using StealthEye.Configuration;
using StealthEye.Operations;
using StealthEye.Windows;

namespace StealthEye.Runtime;

public static class ServiceRegistration
{
    public static IServiceCollection AddEyeCore(
        this IServiceCollection services,
        EyeConfig config,
        EyeRuntimeMode mode = EyeRuntimeMode.Cli)
    {
        services.AddSingleton(config);
        services.AddSingleton(new EyeRuntimeContext(mode));
        services.AddSingleton<SystemOperations>();
        services.AddSingleton<FileOperations>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<ProcessRegistry>();
        services.AddSingleton<DesktopOperations>();
        services.AddSingleton<ScreenCaptureOperations>();
        services.AddSingleton<UiAutomationOperations>();
        services.AddSingleton<OperationDispatcher>();
        return services;
    }
}
