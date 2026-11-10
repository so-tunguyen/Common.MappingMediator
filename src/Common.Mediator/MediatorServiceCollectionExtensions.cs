using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Common.Mediator;

/// <summary>Configuration for <see cref="MediatorServiceCollectionExtensions.AddCommonMediator"/>.</summary>
public sealed class MediatorServiceConfiguration
{
    internal List<Assembly> AssembliesToRegister { get; } = new();

    /// <summary>Register handlers from the given assembly.</summary>
    public MediatorServiceConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        AssembliesToRegister.Add(assembly);
        return this;
    }

    /// <summary>Register handlers from the given assemblies.</summary>
    public MediatorServiceConfiguration RegisterServicesFromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        AssembliesToRegister.AddRange(assemblies);
        return this;
    }

    /// <summary>Register handlers from the assembly containing <typeparamref name="T"/>.</summary>
    public MediatorServiceConfiguration RegisterServicesFromAssemblyContaining<T>()
        => RegisterServicesFromAssembly(typeof(T).Assembly);

    /// <summary>Register handlers from the assembly containing <paramref name="type"/>.</summary>
    public MediatorServiceConfiguration RegisterServicesFromAssemblyContaining(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return RegisterServicesFromAssembly(type.Assembly);
    }
}

/// <summary>
/// Registers the mediator and scans assemblies for
/// <see cref="IRequestHandler{TRequest,TResponse}"/> implementations.
/// </summary>
/// <remarks>
/// The method is named <c>AddCommonMediator</c> (not <c>AddMediatR</c>) so it never becomes
/// ambiguous in a project that also references the real MediatR package.
/// </remarks>
public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMediator"/> and all request handlers found in the
    /// assemblies configured via <paramref name="configuration"/>.
    /// Handlers are registered as transient.
    /// </summary>
    /// <example>
    /// <code>services.AddCommonMediator(cfg => cfg.RegisterServicesFromAssemblyContaining&lt;Program&gt;());</code>
    /// </example>
    public static IServiceCollection AddCommonMediator(this IServiceCollection services, Action<MediatorServiceConfiguration> configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var config = new MediatorServiceConfiguration();
        configuration(config);

        services.TryAddTransient<IMediator, Mediator>();

        var openHandlerInterface = typeof(IRequestHandler<,>);

        foreach (var assembly in config.AssembliesToRegister.Where(a => a is not null).Distinct())
        {
            foreach (var implementationType in GetLoadableTypes(assembly))
            {
                if (implementationType.IsAbstract || implementationType.IsInterface || implementationType.ContainsGenericParameters)
                {
                    continue;
                }

                foreach (var serviceType in implementationType.GetInterfaces())
                {
                    if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == openHandlerInterface)
                    {
                        // TryAddEnumerable: calling AddCommonMediator twice for the same assembly
                        // does not register the same handler twice.
                        services.TryAddEnumerable(ServiceDescriptor.Transient(serviceType, implementationType));
                    }
                }
            }
        }

        return services;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Tolerate types that fail to load (e.g. missing optional dependency).
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
