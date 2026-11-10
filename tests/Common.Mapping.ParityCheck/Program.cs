
using System;
using System.Collections.Generic;
using System.Text.Json;
using Real = AutoMapper;     // the genuine AutoMapper 12.0.1 NuGet package
using Shim = Common.Mapping; // the library under test

namespace Common.Mapping.ParityCheck
{
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Console.WriteLine("Common.Mapping vs real AutoMapper 12.0.1 — parity checks\n");

            // 1) Plain same-name copy with widening + nullable conversions (int->long, int->int?).
            RunMap<SrcBasic, DstBasic>("01 same-name + int->long + int->int?",
                r => r.CreateMap<SrcBasic, DstBasic>(),
                s => s.CreateMap<SrcBasic, DstBasic>(),
                new SrcBasic { A = 7, B = "hi", C = 42 });

            // 2) MapFrom rename (financial: SponsorDues <- RatingSponsorDues).
            RunMap<SrcRename, DstRename>("02 ForMember.MapFrom rename",
                r => r.CreateMap<SrcRename, DstRename>().ForMember(d => d.SponsorDues, o => o.MapFrom(s => s.RatingSponsorDues)),
                s => s.CreateMap<SrcRename, DstRename>().ForMember(d => d.SponsorDues, o => o.MapFrom(s => s.RatingSponsorDues)),
                new SrcRename { RatingSponsorDues = 1234, Other = 9 });

            // 3) MapFrom with ternary + method-ish expression.
            RunMap<SrcExpr, DstExpr>("03 ForMember.MapFrom ternary",
                r => r.CreateMap<SrcExpr, DstExpr>().ForMember(d => d.Reason, o => o.MapFrom(s => s.Code > 0 ? s.Primary : s.Fallback)),
                s => s.CreateMap<SrcExpr, DstExpr>().ForMember(d => d.Reason, o => o.MapFrom(s => s.Code > 0 ? s.Primary : s.Fallback)),
                new SrcExpr { Code = 0, Primary = "P", Fallback = "F" });

            // 4) Ignore is authoritative even when a same-name source exists.
            RunMap<SrcIgnore, DstIgnore>("04 ForMember.Ignore authority",
                r => r.CreateMap<SrcIgnore, DstIgnore>().ForMember(d => d.Drop, o => o.Ignore()),
                s => s.CreateMap<SrcIgnore, DstIgnore>().ForMember(d => d.Drop, o => o.Ignore()),
                new SrcIgnore { Keep = 1, Drop = 999 });

            // 5) ReverseMap auto-inverts a simple-member MapFrom (round-trip SponsorDues).
            RunMapReverse("05 ReverseMap simple-member inversion",
                new MoneyB { SponsorDues = 500, Shared = 3 });

            // 6) ConstructUsing for a type with no parameterless ctor.
            RunMap<SrcCtor, DstCtor>("06 ConstructUsing (no parameterless ctor)",
                r => r.CreateMap<SrcCtor, DstCtor>().ConstructUsing((s, ctx) => new DstCtor(s.Name)),
                s => s.CreateMap<SrcCtor, DstCtor>().ConstructUsing((s, ctx) => new DstCtor(s.Name)),
                new SrcCtor { Name = "doc.pdf" });

            // 7) AfterMap (3-arg) rebuilding a child collection (Ignored member filled by AfterMap).
            RunMap<SrcAfter, DstAfter>("07 AfterMap rebuilds child collection",
                r => r.CreateMap<SrcAfter, DstAfter>().ForMember(d => d.Names, o => o.Ignore())
                      .AfterMap((s, d, ctx) => { d.Names = new List<string>(); foreach (var i in s.Items) d.Names.Add("N" + i); }),
                s => s.CreateMap<SrcAfter, DstAfter>().ForMember(d => d.Names, o => o.Ignore())
                      .AfterMap((s, d, ctx) => { d.Names = new List<string>(); foreach (var i in s.Items) d.Names.Add("N" + i); }),
                new SrcAfter { Items = new List<int> { 1, 2, 3 } });

            // 8) Nested complex member mapped via a registered child map.
            RunMap<SrcNest, DstNest>("08 nested complex member",
                r => { r.CreateMap<SrcChild, DstChild>(); r.CreateMap<SrcNest, DstNest>(); },
                s => { s.CreateMap<SrcChild, DstChild>(); s.CreateMap<SrcNest, DstNest>(); },
                new SrcNest { Top = 5, Child = new SrcChild { Code = 11, Label = "x" } });

            // 9) Null nested member stays null (AllowNullDestinationValues default).
            RunMap<SrcNest, DstNest>("09 null nested member -> null",
                r => { r.CreateMap<SrcChild, DstChild>(); r.CreateMap<SrcNest, DstNest>(); },
                s => { s.CreateMap<SrcChild, DstChild>(); s.CreateMap<SrcNest, DstNest>(); },
                new SrcNest { Top = 5, Child = null });

            // 10) Collection member: List<SrcChild> -> List<DstChild>.
            RunMap<SrcColl, DstColl>("10 collection member element mapping",
                r => { r.CreateMap<SrcChild, DstChild>(); r.CreateMap<SrcColl, DstColl>(); },
                s => { s.CreateMap<SrcChild, DstChild>(); s.CreateMap<SrcColl, DstColl>(); },
                new SrcColl { Items = new List<SrcChild> { new() { Code = 1, Label = "a" }, new() { Code = 2, Label = "b" } } });

            // 11) Null source collection member -> empty list (AllowNullCollections=false default).
            RunMap<SrcColl, DstColl>("11 null source collection -> empty",
                r => { r.CreateMap<SrcChild, DstChild>(); r.CreateMap<SrcColl, DstColl>(); },
                s => { s.CreateMap<SrcChild, DstChild>(); s.CreateMap<SrcColl, DstColl>(); },
                new SrcColl { Items = null });

            // 12) Enum -> string (by name) and int -> enum.
            RunMap<SrcEnum, DstEnum>("12 enum->string + int->enum",
                r => r.CreateMap<SrcEnum, DstEnum>(),
                s => s.CreateMap<SrcEnum, DstEnum>(),
                new SrcEnum { Color = Color.Green, Kind = 2 });

            // 13) Two-arg in-place merge with ForAllMembers null-skip Condition (the critical financial merge).
            RunInPlace<SrcMerge, DstMerge>("13 two-arg in-place + ForAllMembers null-skip",
                r => r.CreateMap<SrcMerge, DstMerge>().ForAllMembers(o => o.Condition((src, dest, sm, dm) => sm != null)),
                s => s.CreateMap<SrcMerge, DstMerge>().ForAllMembers(o => o.Condition((src, dest, sm, dm) => sm != null)),
                new SrcMerge { Name = "new", Premium = null },
                () => new DstMerge { Name = "OLD", Premium = 100, Untouched = "keep" });

            // 14) Two-arg in-place WITHOUT condition: overwrite mapped (incl. null), preserve unmapped.
            RunInPlace<SrcMerge, DstMerge>("14 two-arg in-place overwrite (no condition)",
                r => r.CreateMap<SrcMerge, DstMerge>(),
                s => s.CreateMap<SrcMerge, DstMerge>(),
                new SrcMerge { Name = "new", Premium = 250 },
                () => new DstMerge { Name = "OLD", Premium = 100, Untouched = "keep" });

            // ===== numeric / nullable / enum conversions (financial-precision risk) =====
            // KEY: does narrowing float->int ROUND or TRUNCATE? A divergence here silently shifts money.
            RunMap<Box<decimal>, Box<int>>("15 decimal 1.9 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = 1.9m });
            RunMap<Box<decimal>, Box<int>>("16 decimal 1.4 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = 1.4m });
            RunMap<Box<decimal>, Box<int>>("17 decimal 2.5 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = 2.5m });
            RunMap<Box<decimal>, Box<int>>("18 decimal -1.9 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = -1.9m });
            RunMap<Box<double>, Box<int>>("19 double 3.7 -> int", r => r.CreateMap<Box<double>, Box<int>>(), s => s.CreateMap<Box<double>, Box<int>>(), new Box<double> { Value = 3.7 });
            RunMap<Box<decimal>, Box<long>>("20 decimal 100.99 -> long", r => r.CreateMap<Box<decimal>, Box<long>>(), s => s.CreateMap<Box<decimal>, Box<long>>(), new Box<decimal> { Value = 100.99m });
            RunMap<Box<int>, Box<decimal>>("21 int 7 -> decimal", r => r.CreateMap<Box<int>, Box<decimal>>(), s => s.CreateMap<Box<int>, Box<decimal>>(), new Box<int> { Value = 7 });
            RunMap<Box<long>, Box<int>>("22 long 9 -> int", r => r.CreateMap<Box<long>, Box<int>>(), s => s.CreateMap<Box<long>, Box<int>>(), new Box<long> { Value = 9 });
            RunMap<Box<int>, Box<int?>>("23 int 5 -> int?", r => r.CreateMap<Box<int>, Box<int?>>(), s => s.CreateMap<Box<int>, Box<int?>>(), new Box<int> { Value = 5 });
            RunMap<Box<int?>, Box<int>>("24 int? null -> int", r => r.CreateMap<Box<int?>, Box<int>>(), s => s.CreateMap<Box<int?>, Box<int>>(), new Box<int?> { Value = null });
            RunMap<Box<Color>, Box<int>>("25 enum -> int", r => r.CreateMap<Box<Color>, Box<int>>(), s => s.CreateMap<Box<Color>, Box<int>>(), new Box<Color> { Value = Color.Blue });
            RunMap<Box<Color>, Box<string>>("26 enum -> string", r => r.CreateMap<Box<Color>, Box<string>>(), s => s.CreateMap<Box<Color>, Box<string>>(), new Box<Color> { Value = Color.Green });
            RunMap<Box<int>, Box<Color>>("27 int 2 -> enum", r => r.CreateMap<Box<int>, Box<Color>>(), s => s.CreateMap<Box<int>, Box<Color>>(), new Box<int> { Value = 2 });
            RunMap<Box<string>, Box<Color>>("28 string Blue -> enum", r => r.CreateMap<Box<string>, Box<Color>>(), s => s.CreateMap<Box<string>, Box<Color>>(), new Box<string> { Value = "Blue" });
            RunMap<Box<string>, Box<string>>("29 null string member", r => r.CreateMap<Box<string>, Box<string>>(), s => s.CreateMap<Box<string>, Box<string>>(), new Box<string> { Value = null });

            // ===== member-kind edge cases =====
            // 30: private setter — does AutoMapper write a public-get/private-set property? Mirror it.
            RunMap<SrcVal, DstPrivateSet>("30 private setter property", r => r.CreateMap<SrcVal, DstPrivateSet>(), s => s.CreateMap<SrcVal, DstPrivateSet>(), new SrcVal { Val = 42 });
            // 31: read-only (get-only, ctor-initialized) collection — does AutoMapper populate it in place?
            RunMap<SrcRoColl, DstRoColl>("31 read-only collection member", r => r.CreateMap<SrcRoColl, DstRoColl>(), s => s.CreateMap<SrcRoColl, DstRoColl>(), new SrcRoColl { Items = new List<int> { 1, 2, 3 } });
            // 32: get-only computed property on dest must not break mapping
            RunMap<SrcVal, DstComputed>("32 get-only computed property", r => r.CreateMap<SrcVal, DstComputed>(), s => s.CreateMap<SrcVal, DstComputed>(), new SrcVal { Val = 10 });
            // 33: read-only collection in-place onto a PRE-POPULATED dest — does AutoMapper clear first or append?
            RunInPlace<SrcRoColl, DstRoColl>("33 read-only coll in-place (clear vs append)",
                r => r.CreateMap<SrcRoColl, DstRoColl>(), s => s.CreateMap<SrcRoColl, DstRoColl>(),
                new SrcRoColl { Items = new List<int> { 1, 2, 3 } },
                () => { var d = new DstRoColl(); d.Items.Add(9); return d; });
            // 34: read-only COMPLEX member — does AutoMapper map into the existing instance?
            RunMap<SrcRoChild, DstRoChild>("34 read-only complex member",
                r => { r.CreateMap<SrcChild, DstChild>(); r.CreateMap<SrcRoChild, DstRoChild>(); },
                s => { s.CreateMap<SrcChild, DstChild>(); s.CreateMap<SrcRoChild, DstRoChild>(); },
                new SrcRoChild { Child = new SrcChild { Code = 7, Label = "z" } });
            // 35: read-only collection with NULL source in-place — does AutoMapper clear (empty) or leave existing?
            RunInPlace<SrcRoColl, DstRoColl>("35 read-only coll null source in-place",
                r => r.CreateMap<SrcRoColl, DstRoColl>(), s => s.CreateMap<SrcRoColl, DstRoColl>(),
                new SrcRoColl { Items = null },
                () => { var d = new DstRoColl(); d.Items.Add(9); return d; });

            // ===== contested behaviors flagged by the code review (resolve empirically) =====
            // decimal/double -> int: does AutoMapper TRUNCATE (cast) or ROUND (Convert)? Use NON-midpoint values.
            RunMap<Box<decimal>, Box<int>>("36 decimal 2.7 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = 2.7m });
            RunMap<Box<decimal>, Box<int>>("37 decimal -2.7 -> int", r => r.CreateMap<Box<decimal>, Box<int>>(), s => s.CreateMap<Box<decimal>, Box<int>>(), new Box<decimal> { Value = -2.7m });
            RunMap<Box<decimal>, Box<long>>("38 decimal 12345.70 -> long", r => r.CreateMap<Box<decimal>, Box<long>>(), s => s.CreateMap<Box<decimal>, Box<long>>(), new Box<decimal> { Value = 12345.70m });
            RunMap<Box<double>, Box<int>>("39 double 2.7 -> int", r => r.CreateMap<Box<double>, Box<int>>(), s => s.CreateMap<Box<double>, Box<int>>(), new Box<double> { Value = 2.7 });

            // Duplicate CreateMap for the same pair: does AutoMapper MERGE member config or REPLACE (last-wins)?
            RunMap<SrcDup, DstDup>("40 duplicate pair: MapFrom then plain (merge vs replace)",
                r => { r.CreateMap<SrcDup, DstDup>().ForMember(d => d.X, o => o.MapFrom(s => s.Y)); r.CreateMap<SrcDup, DstDup>(); },
                s => { s.CreateMap<SrcDup, DstDup>().ForMember(d => d.X, o => o.MapFrom(s => s.Y)); s.CreateMap<SrcDup, DstDup>(); },
                new SrcDup { Y = 5 });
            // ReverseMap-produced pair then explicit plain CreateMap of the same pair (the actual RetroStudyProfile shape).
            RunMap<RevB, RevA>("41 ReverseMap pair then plain CreateMap (merge vs replace)",
                r => { r.CreateMap<RevA, RevB>().ForMember(b => b.X, o => o.MapFrom(a => a.Y)).ReverseMap(); r.CreateMap<RevB, RevA>(); },
                s => { s.CreateMap<RevA, RevB>().ForMember(b => b.X, o => o.MapFrom(a => a.Y)).ReverseMap(); s.CreateMap<RevB, RevA>(); },
                new RevB { X = 5 });

            // Destination with ONLY a parameterized ctor (no parameterless, no ConstructUsing): bind-by-name or throw?
            RunMap<SrcCtorBind, CtorOnly>("42 parameterized-ctor binding",
                r => r.CreateMap<SrcCtorBind, CtorOnly>(), s => s.CreateMap<SrcCtorBind, CtorOnly>(),
                new SrcCtorBind { Val = 7, Name = "x" });

            Console.WriteLine($"\n{(_failures == 0 ? "ALL PARITY CHECKS PASSED" : _failures + " PARITY CHECK(S) FAILED")}");
            return _failures;
        }

        private static void RunMap<TSrc, TDst>(string name,
            Action<Real.IMapperConfigurationExpression> realCfg,
            Action<Shim.IMapperConfigurationExpression> shimCfg,
            TSrc source)
        {
            object real, shim;
            try { real = new Real.MapperConfiguration(realCfg).CreateMapper().Map<TDst>(source); }
            catch (Exception e) { real = "THREW: " + e.GetType().Name; }
            try { shim = new Shim.MapperConfiguration(shimCfg).CreateMapper().Map<TDst>(source); }
            catch (Exception e) { shim = "THREW: " + e.GetType().Name + ": " + e.Message; }
            Check(name, real, shim);
        }

        private static void RunInPlace<TSrc, TDst>(string name,
            Action<Real.IMapperConfigurationExpression> realCfg,
            Action<Shim.IMapperConfigurationExpression> shimCfg,
            TSrc source, Func<TDst> destFactory)
        {
            object real, shim;
            try { var d = destFactory(); new Real.MapperConfiguration(realCfg).CreateMapper().Map(source, d); real = d; }
            catch (Exception e) { real = "THREW: " + e.GetType().Name; }
            try { var d = destFactory(); new Shim.MapperConfiguration(shimCfg).CreateMapper().Map(source, d); shim = d; }
            catch (Exception e) { shim = "THREW: " + e.GetType().Name + ": " + e.Message; }
            Check(name, real, shim);
        }

        private static void RunMapReverse(string name, MoneyB source)
        {
            object real, shim;
            try
            {
                var m = new Real.MapperConfiguration(c => c.CreateMap<MoneyA, MoneyB>()
                    .ForMember(b => b.SponsorDues, o => o.MapFrom(a => a.RetroSponsorDues)).ReverseMap()).CreateMapper();
                real = m.Map<MoneyA>(source);
            }
            catch (Exception e) { real = "THREW: " + e.GetType().Name; }
            try
            {
                var m = new Shim.MapperConfiguration(c => c.CreateMap<MoneyA, MoneyB>()
                    .ForMember(b => b.SponsorDues, o => o.MapFrom(a => a.RetroSponsorDues)).ReverseMap()).CreateMapper();
                shim = m.Map<MoneyA>(source);
            }
            catch (Exception e) { shim = "THREW: " + e.GetType().Name + ": " + e.Message; }
            Check(name, real, shim);
        }

        private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

        private static void Check(string name, object real, object shim)
        {
            var r = J(real);
            var s = J(shim);
            if (r == s) { Console.WriteLine($"PASS  {name}"); }
            else
            {
                _failures++;
                Console.WriteLine($"FAIL  {name}\n        real: {r}\n        shim: {s}");
            }
        }

        private static string J(object o)
        {
            try { return JsonSerializer.Serialize(o, JsonOpts); }
            catch (Exception e) { return "SERIALIZE_ERROR: " + e.Message; }
        }
    }

    // ---- synthetic POCO types (no AutoMapper dependency) ----
    public class SrcBasic { public int A { get; set; } public string B { get; set; } public int C { get; set; } }
    public class DstBasic { public long A { get; set; } public string B { get; set; } public int? C { get; set; } }

    public class SrcRename { public int RatingSponsorDues { get; set; } public int Other { get; set; } }
    public class DstRename { public int SponsorDues { get; set; } public int Other { get; set; } }

    public class SrcExpr { public int Code { get; set; } public string Primary { get; set; } public string Fallback { get; set; } }
    public class DstExpr { public string Reason { get; set; } }

    public class SrcIgnore { public int Keep { get; set; } public int Drop { get; set; } }
    public class DstIgnore { public int Keep { get; set; } public int Drop { get; set; } }

    public class MoneyA { public int RetroSponsorDues { get; set; } public int Shared { get; set; } }
    public class MoneyB { public int SponsorDues { get; set; } public int Shared { get; set; } }

    public class SrcCtor { public string Name { get; set; } }
    public class DstCtor { public DstCtor(string name) { Name = name; } public string Name { get; set; } }

    public class SrcAfter { public List<int> Items { get; set; } }
    public class DstAfter { public List<string> Names { get; set; } }

    public class SrcChild { public int Code { get; set; } public string Label { get; set; } }
    public class DstChild { public int Code { get; set; } public string Label { get; set; } }
    public class SrcNest { public int Top { get; set; } public SrcChild Child { get; set; } }
    public class DstNest { public int Top { get; set; } public DstChild Child { get; set; } }

    public class SrcColl { public List<SrcChild> Items { get; set; } }
    public class DstColl { public List<DstChild> Items { get; set; } }

    public enum Color { Red = 0, Green = 1, Blue = 2 }
    public enum Kind { None = 0, A = 1, B = 2 }
    public class SrcEnum { public Color Color { get; set; } public int Kind { get; set; } }
    public class DstEnum { public string Color { get; set; } public Kind Kind { get; set; } }

    public class SrcMerge { public string Name { get; set; } public int? Premium { get; set; } }
    public class DstMerge { public string Name { get; set; } public int? Premium { get; set; } public string Untouched { get; set; } }

    // generic single-value carrier to test one conversion at a time (Value -> Value, same name)
    public class Box<T> { public T Value { get; set; } }

    public class SrcVal { public int Val { get; set; } }
    public class DstPrivateSet { public int Val { get; private set; } public int Other { get; set; } }
    public class DstComputed { public int Val { get; set; } public int Doubled => Val * 2; }

    public class SrcRoColl { public List<int> Items { get; set; } }
    public class DstRoColl { public List<int> Items { get; } = new List<int>(); }

    public class SrcRoChild { public SrcChild Child { get; set; } }
    public class DstRoChild { public DstChild Child { get; } = new DstChild(); }

    public class SrcDup { public int Y { get; set; } }   // no X -> convention can't fill X; only the MapFrom can
    public class DstDup { public int X { get; set; } }

    public class RevA { public int Y { get; set; } }
    public class RevB { public int X { get; set; } }

    public class SrcCtorBind { public int Val { get; set; } public string Name { get; set; } }
    public class CtorOnly
    {
        public CtorOnly(int val, string name) { Val = val; Name = name; }
        public int Val { get; }
        public string Name { get; }
    }
}
