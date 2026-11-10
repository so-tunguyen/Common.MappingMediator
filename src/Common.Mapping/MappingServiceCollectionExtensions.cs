// -----------------------------------------------------------------------------
//  DI registration for Common.Mapping (equivalent of AutoMapper's AddAutoMapper).
//  Scans assemblies for Profile types, builds one configuration, and registers
//  IConfigurationProvider (singleton) + IMapper (transient).
//
//  The method is named AddCommonMapping (not AddAutoMapper) so it never becomes
//  ambiguous in a project that also references the real AutoMapper package.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Common.Mapping
{
    public static class MappingServiceCollectionExtensions
    {
        /// <summary>Scan the assemblies containing the given marker types for profiles.</summary>
        /// <example><code>services.AddCommonMapping(typeof(Program));</code></example>
        public static IServiceCollection AddCommonMapping(this IServiceCollection services, params Type[] profileAssemblyMarkerTypes)
            => AddCommonMappingCore(services, null, profileAssemblyMarkerTypes.Select(t => t.Assembly));

        /// <summary>Apply a config action and scan the given assemblies for profiles.</summary>
        public static IServiceCollection AddCommonMapping(this IServiceCollection services,
            Action<IMapperConfigurationExpression> configAction, params Assembly[] assemblies)
            => AddCommonMappingCore(services, configAction, assemblies);

        /// <summary>Apply a config action and scan the assemblies of the given marker types.</summary>
        public static IServiceCollection AddCommonMapping(this IServiceCollection services,
            Action<IMapperConfigurationExpression> configAction, params Type[] profileAssemblyMarkerTypes)
            => AddCommonMappingCore(services, configAction, profileAssemblyMarkerTypes.Select(t => t.Assembly));

        /// <summary>Scan the given assemblies for profiles.</summary>
        public static IServiceCollection AddCommonMapping(this IServiceCollection services, params Assembly[] assemblies)
            => AddCommonMappingCore(services, null, assemblies);

        private static IServiceCollection AddCommonMappingCore(IServiceCollection services,
            Action<IMapperConfigurationExpression> configAction, IEnumerable<Assembly> assemblies)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            var config = new MapperConfiguration(cfg =>
            {
                configAction?.Invoke(cfg);
                foreach (var assembly in assemblies.Where(a => a != null).Distinct())
                {
                    foreach (var type in GetLoadableTypes(assembly))
                    {
                        if (type.IsAbstract || type.IsInterface) continue;
                        if (!typeof(Profile).IsAssignableFrom(type)) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                        cfg.AddProfile((Profile)Activator.CreateInstance(type));
                    }
                }
            });

            // Same defaults as AutoMapper's DI package: configuration is a singleton, the mapper
            // is transient. TryAdd => the first registration wins, so call AddCommonMapping once
            // with every assembly that contains profiles.
            services.TryAddSingleton<IConfigurationProvider>(config);
            services.TryAddTransient<IMapper>(sp => sp.GetRequiredService<IConfigurationProvider>().CreateMapper());
            return services;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }
    }
}
