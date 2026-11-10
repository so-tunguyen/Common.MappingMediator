// -----------------------------------------------------------------------------
//  Common.Mapping — Profile + fluent configuration capture. Profiles register TypeMapConfig entries; the engine in
//  MapperConfiguration.cs executes them. See csproj for scope notes.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Common.Mapping
{
    /// <summary>Base class for mapping profiles. Mirrors AutoMapper.Profile (subset).</summary>
    public abstract class Profile
    {
        internal readonly List<TypeMapConfig> TypeMaps = new();

        /// <summary>Define a map from <typeparamref name="TSource"/> to <typeparamref name="TDestination"/>.</summary>
        protected internal IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
        {
            var cfg = new TypeMapConfig(typeof(TSource), typeof(TDestination));
            TypeMaps.Add(cfg);
            return new MappingExpression<TSource, TDestination>(this, cfg);
        }
    }

    // ---- Internal configuration model -------------------------------------------------

    internal sealed class TypeMapConfig
    {
        public TypeMapConfig(Type sourceType, Type destinationType)
        {
            SourceType = sourceType;
            DestinationType = destinationType;
        }

        public Type SourceType { get; }
        public Type DestinationType { get; }

        // ConstructUsing factory (source, context) -> destination instance.
        public Func<object, ResolutionContext, object> ConstructUsing;

        // AfterMap delegates, run (in order) after member mapping, against the SAME destination instance.
        public readonly List<Action<object, object, ResolutionContext>> AfterMaps = new();

        // Per-destination-member configuration keyed by destination member name.
        public readonly Dictionary<string, MemberMapConfig> Members = new(StringComparer.Ordinal);

        // ForAllMembers conditions: (source, destination, srcMemberValue, destMemberValue) -> shouldWrite.
        public readonly List<Func<object, object, object, object, bool>> AllMemberConditions = new();

        // ForAllMembers(opt => opt.Ignore()) — ignore every member.
        public bool IgnoreAll;

        public MemberMapConfig GetOrAddMember(string name)
        {
            if (!Members.TryGetValue(name, out var m))
            {
                m = new MemberMapConfig { DestinationName = name };
                Members[name] = m;
            }
            return m;
        }
    }

    internal sealed class MemberMapConfig
    {
        public string DestinationName;
        public bool Ignored;
        public bool HasMapFrom;

        // Compiled MapFrom: source(object) -> resolved value(object).
        public Func<object, object> MapFrom;

        // Set when MapFrom is a simple member access (s => s.X); enables ReverseMap inversion.
        public string SimpleSourceMemberName;

        // Per-member Condition (source, destination, srcMemberValue, destMemberValue) -> shouldWrite.
        public Func<object, object, object, object, bool> Condition;
    }

    // ---- Fluent builders --------------------------------------------------------------

    internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
    {
        private readonly Profile _profile;
        private readonly TypeMapConfig _map;

        public MappingExpression(Profile profile, TypeMapConfig map)
        {
            _profile = profile;
            _map = map;
        }

        public IMappingExpression<TSource, TDestination> ForMember<TMember>(
            Expression<Func<TDestination, TMember>> destinationMember,
            Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> memberOptions)
        {
            var name = ExpressionHelper.GetMemberName(destinationMember);
            var member = _map.GetOrAddMember(name);
            memberOptions(new MemberConfigurationExpression<TSource, TDestination, TMember>(member));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ForAllMembers(
            Action<IMemberConfigurationExpression<TSource, TDestination, object>> memberOptions)
        {
            memberOptions(new AllMembersSink<TSource, TDestination>(_map));
            return this;
        }

        public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> ctor)
        {
            _map.ConstructUsing = (src, ctx) => ctor((TSource)src, ctx);
            return this;
        }

        public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction)
        {
            _map.AfterMaps.Add((src, dest, ctx) => afterFunction((TSource)src, (TDestination)dest));
            return this;
        }

        public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> afterFunction)
        {
            _map.AfterMaps.Add((src, dest, ctx) => afterFunction((TSource)src, (TDestination)dest, ctx));
            return this;
        }

        public IMappingExpression<TDestination, TSource> ReverseMap()
        {
            var reverse = new TypeMapConfig(typeof(TDestination), typeof(TSource));

            // Invert simple-member MapFroms so e.g. forward dest.SponsorDues <- src.RetroSponsorDues
            // becomes reverse dest.RetroSponsorDues <- src.SponsorDues. Ignores are NOT carried over
            // (AutoMapper falls back to convention for those on the reverse map).
            foreach (var kv in _map.Members)
            {
                var fwd = kv.Value;
                if (fwd.HasMapFrom && fwd.SimpleSourceMemberName != null && !fwd.Ignored)
                {
                    var revMember = reverse.GetOrAddMember(fwd.SimpleSourceMemberName);
                    var forwardDestName = fwd.DestinationName;
                    revMember.HasMapFrom = true;
                    revMember.SimpleSourceMemberName = forwardDestName;
                    revMember.MapFrom = srcObj => ReflectionHelper.GetMemberValue(srcObj, forwardDestName);
                }
            }

            _profile.TypeMaps.Add(reverse);
            return new MappingExpression<TDestination, TSource>(_profile, reverse);
        }
    }

    internal sealed class MemberConfigurationExpression<TSource, TDestination, TMember>
        : IMemberConfigurationExpression<TSource, TDestination, TMember>
    {
        private readonly MemberMapConfig _member;

        public MemberConfigurationExpression(MemberMapConfig member) => _member = member;

        public void Ignore()
        {
            _member.Ignored = true;
            _member.HasMapFrom = false;
            _member.MapFrom = null;
            _member.SimpleSourceMemberName = null;
        }

        public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        {
            _member.Ignored = false;
            _member.HasMapFrom = true;
            _member.SimpleSourceMemberName = ExpressionHelper.TryGetSimpleMemberName(sourceMember);
            var compiled = sourceMember.Compile();
            _member.MapFrom = src => compiled((TSource)src);
        }

        public void Condition(Func<TSource, TDestination, TMember, TMember, bool> condition)
        {
            _member.Condition = (src, dest, sm, dm) =>
                condition((TSource)src, (TDestination)dest, Cast(sm), Cast(dm));
        }

        private static TMember Cast(object v)
        {
            if (v is TMember t) return t;
            if (v == null) return default;
            return (TMember)v;
        }
    }

    // ForAllMembers sink: translates Condition into a map-wide condition; Ignore into IgnoreAll.
    internal sealed class AllMembersSink<TSource, TDestination>
        : IMemberConfigurationExpression<TSource, TDestination, object>
    {
        private readonly TypeMapConfig _map;

        public AllMembersSink(TypeMapConfig map) => _map = map;

        public void Ignore() => _map.IgnoreAll = true;

        public void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        {
            // Not supported for ForAllMembers; intentionally a no-op.
        }

        public void Condition(Func<TSource, TDestination, object, object, bool> condition)
        {
            _map.AllMemberConditions.Add((src, dest, sm, dm) =>
                condition((TSource)src, (TDestination)dest, sm, dm));
        }
    }

    internal static class ExpressionHelper
    {
        public static string GetMemberName(LambdaExpression expr)
        {
            var body = expr.Body;
            while (body is UnaryExpression u) body = u.Operand;
            if (body is MemberExpression m) return m.Member.Name;
            throw new ArgumentException($"Expression '{expr}' must be a simple member access (e.g. x => x.Member).");
        }

        // Returns the member name when the body is a direct member access on the lambda parameter
        // (s => s.X). Returns null for nested access, method calls, conditionals, etc.
        public static string TryGetSimpleMemberName(LambdaExpression expr)
        {
            var body = expr.Body;
            while (body is UnaryExpression u) body = u.Operand;
            if (body is MemberExpression m && m.Expression == expr.Parameters[0])
                return m.Member.Name;
            return null;
        }
    }
}
