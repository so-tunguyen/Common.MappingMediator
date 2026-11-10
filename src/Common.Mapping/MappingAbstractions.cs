// -----------------------------------------------------------------------------
//  Common.Mapping — public API surface.
//  Type names and member signatures match AutoMapper, so migrating a project only
//  requires swapping `using AutoMapper;` for `using Common.Mapping;`.
//  See Common.Mapping.csproj for the supported feature set.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Common.Mapping
{
    /// <summary>Performs object-to-object mapping. Mirrors the subset of AutoMapper.IMapper that is used.</summary>
    public interface IMapper
    {
        /// <summary>Map <paramref name="source"/> to a new instance of <typeparamref name="TDestination"/>.</summary>
        TDestination Map<TDestination>(object source);

        /// <summary>Map a <typeparamref name="TSource"/> to a new <typeparamref name="TDestination"/>.</summary>
        TDestination Map<TSource, TDestination>(TSource source);

        /// <summary>Map <paramref name="source"/> onto an EXISTING <paramref name="destination"/> (in-place merge) and return it.</summary>
        TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
    }

    /// <summary>Holds the built configuration and creates mappers. Mirrors AutoMapper.IConfigurationProvider (subset).</summary>
    public interface IConfigurationProvider
    {
        /// <summary>Create a mapper bound to this configuration.</summary>
        IMapper CreateMapper();

        /// <summary>
        /// Lightweight emulation of AutoMapper's configuration validation: every destination member must be
        /// same-name resolvable, explicitly mapped, ignored, or covered by ConstructUsing/AfterMap.
        /// Not called by current code; provided for an optional startup guard.
        /// </summary>
        void AssertConfigurationIsValid();
    }

    /// <summary>Configuration entry point passed to <c>new MapperConfiguration(cfg =&gt; ...)</c> / <c>AddCommonMapping(cfg =&gt; ...)</c>.</summary>
    public interface IMapperConfigurationExpression
    {
        /// <summary>Add an already-constructed profile instance.</summary>
        void AddProfile(Profile profile);

        /// <summary>Add a profile by type (must have a public parameterless constructor).</summary>
        void AddProfile<TProfile>() where TProfile : Profile, new();

        /// <summary>Create a map directly on the configuration (rarely used; profiles are preferred).</summary>
        IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
    }

    /// <summary>
    /// Context object handed to <c>ConstructUsing</c> / 3-arg <c>AfterMap</c> delegates.
    /// Exposes the running <see cref="IMapper"/> so delegates can map nested objects.
    /// </summary>
    public sealed class ResolutionContext
    {
        internal ResolutionContext(IMapper mapper)
        {
            Mapper = mapper;
            Items = new Dictionary<string, object>();
        }

        /// <summary>The mapper running the current operation.</summary>
        public IMapper Mapper { get; }

        /// <summary>Per-operation bag (kept for API compatibility with AutoMapper).</summary>
        public IDictionary<string, object> Items { get; }
    }

    /// <summary>Fluent configuration for a single source-&gt;destination map.</summary>
    /// <typeparam name="TSource">Source type.</typeparam>
    /// <typeparam name="TDestination">Destination type.</typeparam>
    public interface IMappingExpression<TSource, TDestination>
    {
        /// <summary>Configure a specific destination member.</summary>
        IMappingExpression<TSource, TDestination> ForMember<TMember>(
            Expression<Func<TDestination, TMember>> destinationMember,
            Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions);

        /// <summary>Apply an action to every destination member (used here for a null-skip condition).</summary>
        IMappingExpression<TSource, TDestination> ForAllMembers(
            Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions);

        /// <summary>Provide a factory that constructs the destination from the source.</summary>
        IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> ctor);

        /// <summary>Run an action after member mapping (2-arg).</summary>
        IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction);

        /// <summary>Run an action after member mapping (3-arg with context).</summary>
        IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> afterFunction);

        /// <summary>Create (and return) the reverse map, auto-inverting simple member MapFroms.</summary>
        IMappingExpression<TDestination, TSource> ReverseMap();
    }

    /// <summary>Per-member configuration handed to the <c>ForMember</c> options lambda.</summary>
    public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
    {
        /// <summary>Do not map this destination member (authoritative even if a same-name source exists).</summary>
        void Ignore();

        /// <summary>Map this member from the given source expression (source member type may differ; it is converted).</summary>
        void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

        /// <summary>Only write the member when the condition holds (used for null-skip merges).</summary>
        void Condition(Func<TSource, TDestination, TMember, TMember, bool> condition);
    }
}
