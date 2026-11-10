// -----------------------------------------------------------------------------
//  Common.Mapping — configuration + mapping engine.
//  Executes the TypeMapConfig entries captured by Profiles (see Profile.cs).
//  See csproj for the (deliberately narrow) supported feature set.
// -----------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Common.Mapping
{
    /// <summary>Built mapping configuration. Mirrors AutoMapper.MapperConfiguration (subset).</summary>
    public sealed class MapperConfiguration : IConfigurationProvider
    {
        // Keyed by (sourceType, destinationType); last registration for a pair wins.
        internal readonly Dictionary<(Type, Type), TypeMapConfig> Maps = new();

        public MapperConfiguration(Action<IMapperConfigurationExpression> configure)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var expr = new MapperConfigurationExpression();
            configure(expr);
            foreach (var p in expr.Profiles)
                foreach (var m in p.TypeMaps)
                    Maps[(m.SourceType, m.DestinationType)] = m;
        }

        internal MapperConfiguration(IEnumerable<Profile> profiles)
        {
            foreach (var p in profiles)
                foreach (var m in p.TypeMaps)
                    Maps[(m.SourceType, m.DestinationType)] = m;
        }

        public IMapper CreateMapper() => new Mapper(this);

        public void AssertConfigurationIsValid() => MappingEngine.Validate(this);

        // Resolve a TypeMap for the pair, allowing an assignable (derived) source type.
        internal TypeMapConfig Resolve(Type sourceType, Type destinationType)
        {
            if (sourceType != null && Maps.TryGetValue((sourceType, destinationType), out var exact))
                return exact;

            if (sourceType != null)
            {
                foreach (var kv in Maps)
                    if (kv.Key.Item2 == destinationType && kv.Key.Item1.IsAssignableFrom(sourceType))
                        return kv.Value;
            }
            return null;
        }
    }

    // Collects profiles/maps for the `new MapperConfiguration(cfg => ...)` and `AddCommonMapping(cfg => ...)` paths.
    internal sealed class MapperConfigurationExpression : IMapperConfigurationExpression
    {
        public readonly List<Profile> Profiles = new();
        private readonly InlineProfile _inline = new();

        public MapperConfigurationExpression() => Profiles.Add(_inline);

        public void AddProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            Profiles.Add(profile);
        }

        public void AddProfile<TProfile>() where TProfile : Profile, new() => Profiles.Add(new TProfile());

        public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>()
            => _inline.CreateMapInline<TSource, TDestination>();

        private sealed class InlineProfile : Profile
        {
            public IMappingExpression<TS, TD> CreateMapInline<TS, TD>() => CreateMap<TS, TD>();
        }
    }

    /// <summary>Default <see cref="IMapper"/> implementation. Mirrors AutoMapper.Mapper (subset).</summary>
    public sealed class Mapper : IMapper
    {
        private readonly MapperConfiguration _config;
        private readonly ResolutionContext _context;

        public Mapper(IConfigurationProvider configurationProvider)
        {
            if (configurationProvider == null) throw new ArgumentNullException(nameof(configurationProvider));
            _config = configurationProvider as MapperConfiguration
                      ?? throw new ArgumentException(
                          $"Expected a {nameof(MapperConfiguration)} but got '{configurationProvider.GetType().FullName}'.",
                          nameof(configurationProvider));
            _context = new ResolutionContext(this);
        }

        public TDestination Map<TDestination>(object source)
            => (TDestination)MappingEngine.Map(_config, _context, source, source?.GetType(), typeof(TDestination), null);

        public TDestination Map<TSource, TDestination>(TSource source)
            => (TDestination)MappingEngine.Map(_config, _context, source, typeof(TSource), typeof(TDestination), null);

        public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
            => (TDestination)MappingEngine.Map(_config, _context, source, typeof(TSource), typeof(TDestination), destination);
    }

    // ---- The mapping engine -----------------------------------------------------------

    internal static class MappingEngine
    {
        // Normal object graphs are far shallower than this. The guard only fires on a circular object
        // graph, converting an uncatchable StackOverflow into a catchable error (the engine, unlike
        // AutoMapper, has no per-operation cycle cache).
        private const int MaxRecursionDepth = 100;

        public static object Map(MapperConfiguration config, ResolutionContext ctx,
            object source, Type sourceDeclaredType, Type destinationType, object existingDestination, int depth = 0)
        {
            // Null source: AllowNullCollections=false (=> empty collection); AllowNullDestinationValues=true (=> null).
            if (source == null)
            {
                if (existingDestination != null) return existingDestination;
                if (IsCollectionType(destinationType)) return CreateCollection(destinationType, GetElementType(destinationType), new List<object>());
                return null;
            }

            if (depth > MaxRecursionDepth)
                throw new InvalidOperationException(
                    $"Mapping recursion exceeded {MaxRecursionDepth} levels mapping to '{destinationType.FullName}' " +
                    "— likely a circular reference. Break the cycle with .Ignore() or an AfterMap.");

            var sourceType = source.GetType();

            // Collections (top-level or nested) when not the exact same assignable instance.
            if (IsCollectionType(destinationType) && source is IEnumerable && !(source is string))
            {
                return MapCollection(config, ctx, (IEnumerable)source, destinationType, depth);
            }

            // Configured TypeMap.
            var map = config.Resolve(sourceType, destinationType)
                      ?? config.Resolve(sourceDeclaredType, destinationType);
            if (map != null)
            {
                var dest = existingDestination ?? ConstructDestination(config, ctx, map, source, destinationType, depth);
                MapMembers(config, ctx, map, source, dest, depth);
                return dest;
            }

            // Same / assignable type — pass through.
            if (destinationType.IsInstanceOfType(source)) return source;

            // Primitive / enum / nullable / Guid conversion.
            if (TryConvert(source, destinationType, out var converted)) return converted;

            throw new InvalidOperationException(
                $"Missing map or conversion from '{sourceType.FullName}' to '{destinationType.FullName}'. " +
                "Common.Mapping auto-handles same-type assignment, primitive/enum/nullable/Guid conversion, " +
                "and collections of mapped elements; anything else needs a CreateMap.");
        }

        private static object ConstructDestination(MapperConfiguration config, ResolutionContext ctx,
            TypeMapConfig map, object source, Type destinationType, int depth)
        {
            if (map?.ConstructUsing != null) return map.ConstructUsing(source, ctx);

            if (destinationType.IsValueType || destinationType.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(destinationType);

            // No parameterless constructor and no ConstructUsing: bind the greediest public constructor by
            // source-member name (case-insensitive), matching AutoMapper's constructor mapping.
            var ctor = destinationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor == null) return Activator.CreateInstance(destinationType); // surfaces a clear error

            var pars = ctor.GetParameters();
            var args = new object[pars.Length];
            var sourceType = source.GetType();
            for (var i = 0; i < pars.Length; i++)
            {
                var p = pars[i];
                var member = GetReadableMemberCI(sourceType, p.Name);
                if (member != null)
                {
                    var raw = GetMemberValue(source, member);
                    args[i] = Map(config, ctx, raw, raw?.GetType(), p.ParameterType, null, depth + 1);
                }
                else
                {
                    args[i] = p.HasDefaultValue ? p.DefaultValue
                        : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null);
                }
            }
            return ctor.Invoke(args);
        }

        private static void MapMembers(MapperConfiguration config, ResolutionContext ctx,
            TypeMapConfig map, object source, object dest, int depth)
        {
            var sourceType = source.GetType();

            if (!map.IgnoreAll)
            {
                foreach (var destMember in GetWritableMembers(dest.GetType()))
                {
                    map.Members.TryGetValue(destMember.Name, out var member);
                    if (member != null && member.Ignored) continue;

                    object sourceValue;
                    bool haveValue;

                    if (member != null && member.HasMapFrom)
                    {
                        sourceValue = member.MapFrom(source);
                        haveValue = true;
                    }
                    else
                    {
                        haveValue = TryResolveConventionValue(source, sourceType, destMember.Name, out sourceValue);
                    }

                    if (!haveValue) continue; // unmapped member: leave destination value untouched.

                    var destMemberType = GetMemberType(destMember);

                    // Conditions are evaluated against the raw SOURCE member value (AutoMapper semantics).
                    if (member?.Condition != null || map.AllMemberConditions.Count > 0)
                    {
                        var currentDest = GetMemberValue(dest, destMember);
                        if (member?.Condition != null && !member.Condition(source, dest, sourceValue, currentDest)) continue;

                        var blocked = false;
                        foreach (var cond in map.AllMemberConditions)
                            if (!cond(source, dest, sourceValue, currentDest)) { blocked = true; break; }
                        if (blocked) continue;
                    }

                    // Read-only collection property: clear and repopulate the existing instance (AutoMapper semantics).
                    if (destMember is PropertyInfo roProp && !roProp.CanWrite)
                    {
                        var existing = roProp.GetValue(dest);
                        if (existing != null)
                            PopulateCollectionInPlace(config, ctx, existing, GetElementType(destMemberType), sourceValue, depth);
                        continue;
                    }

                    var mapped = Map(config, ctx, sourceValue, sourceValue?.GetType(), destMemberType, null, depth + 1);

                    // Don't write null onto a non-nullable value-type member (leave its default).
                    if (mapped == null && destMemberType.IsValueType && Nullable.GetUnderlyingType(destMemberType) == null)
                        continue;

                    SetMemberValue(dest, destMember, mapped);
                }
            }

            foreach (var after in map.AfterMaps)
                after(source, dest, ctx);
        }

        private static object MapCollection(MapperConfiguration config, ResolutionContext ctx,
            IEnumerable source, Type destinationType, int depth)
        {
            var elementType = GetElementType(destinationType);
            var items = new List<object>();
            foreach (var item in source)
                items.Add(Map(config, ctx, item, item?.GetType(), elementType, null, depth + 1));
            return CreateCollection(destinationType, elementType, items);
        }

        // Clears a get-only destination collection and repopulates it with mapped source elements,
        // matching AutoMapper's handling of read-only collection members. A null source clears it
        // (AllowNullCollections=false => empty), consistent with writable-collection mapping.
        private static void PopulateCollectionInPlace(MapperConfiguration config, ResolutionContext ctx,
            object destinationCollection, Type elementType, object source, int depth)
        {
            var collType = destinationCollection.GetType();
            collType.GetMethod("Clear", Type.EmptyTypes)?.Invoke(destinationCollection, null);

            if (source is IEnumerable items && source is not string)
            {
                var add = collType.GetMethod("Add", new[] { elementType }) ?? collType.GetMethod("Add");
                if (add != null)
                {
                    foreach (var item in items)
                    {
                        var mapped = Map(config, ctx, item, item?.GetType(), elementType, null, depth + 1);
                        add.Invoke(destinationCollection, new[] { mapped });
                    }
                }
            }
        }

        // ---- Convention resolution: same-name member, Get<Name>() method, then flattening ----

        private static bool TryResolveConventionValue(object source, Type sourceType, string name, out object value)
        {
            // 1) property / field of the same name
            var member = GetReadableMember(sourceType, name);
            if (member != null) { value = GetMemberValue(source, member); return true; }

            // 2) parameterless Get<Name>() method
            var getter = sourceType.GetMethod("Get" + name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getter != null && getter.ReturnType != typeof(void))
            {
                value = getter.Invoke(source, null);
                return true;
            }

            // 3) flattening: dest member "ChildName" -> source.Child.Name
            if (TryFlatten(source, sourceType, name, out value)) return true;

            value = null;
            return false;
        }

        private static bool TryFlatten(object source, Type sourceType, string name, out object value)
        {
            value = null;
            // Greedily peel a leading member whose name is a prefix of `name`, then resolve the remainder on it.
            foreach (var member in GetReadableMembersByLongestNamePrefix(sourceType, name))
            {
                if (name.Length <= member.Name.Length) continue;
                var remainder = name.Substring(member.Name.Length);
                var child = GetMemberValue(source, member);
                if (child == null) { value = null; return true; } // flattened-null is a resolved (null) value
                if (TryResolveConventionValue(child, child.GetType(), remainder, out value)) return true;
            }
            return false;
        }

        // ---- Conversion --------------------------------------------------------------

        private static bool TryConvert(object source, Type destinationType, out object result)
        {
            result = null;
            var underlying = Nullable.GetUnderlyingType(destinationType) ?? destinationType;

            if (underlying.IsInstanceOfType(source)) { result = source; return true; }

            try
            {
                if (underlying.IsEnum)
                {
                    result = source is string s
                        ? Enum.Parse(underlying, s, ignoreCase: true)
                        : Enum.ToObject(underlying, Convert.ChangeType(source, Enum.GetUnderlyingType(underlying)));
                    return true;
                }
                if (underlying == typeof(string)) { result = source.ToString(); return true; }
                if (underlying == typeof(Guid) && source is string gs) { result = Guid.Parse(gs); return true; }
                if (source is IConvertible) { result = Convert.ChangeType(source, underlying); return true; }
            }
            catch
            {
                result = null;
                return false;
            }
            return false;
        }

        // ---- Collections -------------------------------------------------------------

        private static bool IsCollectionType(Type type)
        {
            if (type == null || type == typeof(string)) return false;
            return type.IsArray || (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string));
        }

        private static Type GetElementType(Type collectionType)
        {
            if (collectionType.IsArray) return collectionType.GetElementType();
            if (collectionType.IsGenericType)
            {
                var args = collectionType.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }
            foreach (var i in collectionType.GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return i.GetGenericArguments()[0];
            return typeof(object);
        }

        private static object CreateCollection(Type destinationType, Type elementType, List<object> items)
        {
            if (destinationType.IsArray)
            {
                var arr = Array.CreateInstance(elementType, items.Count);
                for (var i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                return arr;
            }

            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (IList)Activator.CreateInstance(listType);
            foreach (var item in items) list.Add(item);

            // List<T> / IList<T> / ICollection<T> / IEnumerable<T> are all satisfied by List<T>.
            if (destinationType.IsAssignableFrom(listType)) return list;

            // Concrete collection with a parameterless ctor + Add (e.g. HashSet<T>): best-effort populate.
            if (!destinationType.IsAbstract && !destinationType.IsInterface)
            {
                var instance = Activator.CreateInstance(destinationType);
                var add = destinationType.GetMethod("Add", new[] { elementType });
                if (add != null)
                {
                    foreach (var item in items) add.Invoke(instance, new[] { item });
                    return instance;
                }
            }
            return list;
        }

        // ---- Reflection over members -------------------------------------------------

        private static readonly Dictionary<Type, MemberInfo[]> _writableCache = new();
        private static readonly object _cacheLock = new();

        private static MemberInfo[] GetWritableMembers(Type type)
        {
            lock (_cacheLock)
            {
                if (_writableCache.TryGetValue(type, out var cached)) return cached;

                // Dedupe by name keeping the most-derived declaration, so a `new`-shadowed
                // property (e.g. derived int IndividualPremium hiding base int?) maps once,
                // matching AutoMapper (which uses the most-derived member).
                var byName = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
                var depthByName = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    // Writable members, PLUS read-only collection properties: AutoMapper maps element-wise
                    // INTO a get-only (ctor-initialized) collection rather than assigning it.
                    var mappable = p.CanWrite || (p.CanRead && IsCollectionType(p.PropertyType));
                    if (!mappable) continue;
                    var d = Depth(p.DeclaringType);
                    if (!byName.ContainsKey(p.Name) || d > depthByName[p.Name])
                    {
                        byName[p.Name] = p;
                        depthByName[p.Name] = d;
                    }
                }
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.IsInitOnly || byName.ContainsKey(f.Name)) continue; // a property of the same name wins
                    byName[f.Name] = f;
                }

                var arr = new MemberInfo[byName.Count];
                byName.Values.CopyTo(arr, 0);
                _writableCache[type] = arr;
                return arr;
            }
        }

        private static MemberInfo GetReadableMember(Type type, string name)
        {
            PropertyInfo bestProp = null;
            var bestDepth = -1;
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != name || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                var d = Depth(p.DeclaringType);
                if (d > bestDepth) { bestDepth = d; bestProp = p; }
            }
            if (bestProp != null) return bestProp;

            FieldInfo bestField = null;
            bestDepth = -1;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name != name) continue;
                var d = Depth(f.DeclaringType);
                if (d > bestDepth) { bestDepth = d; bestField = f; }
            }
            return bestField;
        }

        // Case-insensitive most-derived readable member lookup (for constructor-parameter binding,
        // where ctor params are camelCase and source members are PascalCase).
        private static MemberInfo GetReadableMemberCI(Type type, string name)
        {
            PropertyInfo bestProp = null;
            var bestDepth = -1;
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                var d = Depth(p.DeclaringType);
                if (d > bestDepth) { bestDepth = d; bestProp = p; }
            }
            if (bestProp != null) return bestProp;

            FieldInfo bestField = null;
            bestDepth = -1;
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                var d = Depth(f.DeclaringType);
                if (d > bestDepth) { bestDepth = d; bestField = f; }
            }
            return bestField;
        }

        // Inheritance depth below object; used to pick the most-derived of shadowed members.
        private static int Depth(Type type)
        {
            var d = 0;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType) d++;
            return d;
        }

        private static IEnumerable<MemberInfo> GetReadableMembersByLongestNamePrefix(Type type, string name)
        {
            var candidates = new List<MemberInfo>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.GetIndexParameters().Length == 0 && name.StartsWith(p.Name, StringComparison.Ordinal))
                    candidates.Add(p);
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (name.StartsWith(f.Name, StringComparison.Ordinal))
                    candidates.Add(f);
            return candidates.OrderByDescending(m => m.Name.Length);
        }

        private static Type GetMemberType(MemberInfo member)
            => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

        private static object GetMemberValue(object obj, MemberInfo member)
            => member is PropertyInfo p ? p.GetValue(obj) : ((FieldInfo)member).GetValue(obj);

        private static void SetMemberValue(object obj, MemberInfo member, object value)
        {
            if (member is PropertyInfo p) p.SetValue(obj, value);
            else ((FieldInfo)member).SetValue(obj, value);
        }

        // ---- Optional config validation (emulates AssertConfigurationIsValid loosely) -----

        public static void Validate(MapperConfiguration config)
        {
            var errors = new List<string>();
            foreach (var map in config.Maps.Values)
            {
                if (map.IgnoreAll || map.ConstructUsing != null || map.AfterMaps.Count > 0) continue;
                foreach (var destMember in GetWritableMembers(map.DestinationType))
                {
                    if (map.Members.TryGetValue(destMember.Name, out var m) && (m.Ignored || m.HasMapFrom)) continue;
                    if (GetReadableMember(map.SourceType, destMember.Name) != null) continue;
                    if (map.SourceType.GetMethod("Get" + destMember.Name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null) continue;
                    errors.Add($"{map.SourceType.Name} -> {map.DestinationType.Name}: unmapped member '{destMember.Name}'.");
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException("Unmapped members were found:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
    }

    internal static class ReflectionHelper
    {
        public static object GetMemberValue(object obj, string name)
        {
            if (obj == null) return null;
            var type = obj.GetType();

            // Resolve the most-derived property of this name (avoids AmbiguousMatchException on `new`-shadowed members).
            PropertyInfo bestProp = null;
            var bestDepth = -1;
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name != name || !p.CanRead) continue;
                var d = 0;
                for (var t = p.DeclaringType; t != null && t != typeof(object); t = t.BaseType) d++;
                if (d > bestDepth) { bestDepth = d; bestProp = p; }
            }
            if (bestProp != null) return bestProp.GetValue(obj);

            var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(obj);
        }
    }
}
