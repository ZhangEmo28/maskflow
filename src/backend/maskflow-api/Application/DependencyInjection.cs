using Microsoft.Extensions.DependencyInjection;

namespace MaskFlow.Api.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddMaskFlowApplication(this IServiceCollection services)
    {
        services.AddScoped<ProjectService>();
        services.AddSingleton<AutoAnnotationService>();
        services.AddSingleton<BatchAnnotationWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<BatchAnnotationWorker>());
        services.AddSingleton<VideoTaskWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<VideoTaskWorker>());
        services.AddSingleton<TrainingWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<TrainingWorker>());
        return services;
    }
}
