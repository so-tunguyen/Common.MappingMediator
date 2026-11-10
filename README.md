# Common.Mapping & Common.Mediator

Two zero-dependency libraries that replace **AutoMapper** and **MediatR** in any .NET 8+ project.

| Library | Replaces | Namespace | DI registration |
|---|---|---|---|
| `Common.Mapping` | AutoMapper | `Common.Mapping` | `services.AddCommonMapping(...)` |
| `Common.Mediator` | MediatR | `Common.Mediator` | `services.AddCommonMediator(...)` |

Type names and member signatures follow the originals (`Profile`, `CreateMap`, `IMapper.Map`, `IRequest<T>`, `IRequestHandler<,>`, `IMediator.Send`...). Existing profiles, handlers and call sites keep working after you change the `using` lines and the registration call.

The namespaces are intentionally different from `AutoMapper` / `MediatR`, so these libraries never conflict with the real packages, even when one of them is pulled in transitively.

## Layout

```
Common.MappingMediator.slnx
src/
  Common.Mapping/                 AutoMapper replacement
  Common.Mediator/                MediatR replacement
tests/
  Common.Mapping.ParityCheck/     compares Common.Mapping output against real AutoMapper 12.0.1
```

## Using it in a project

Add a project reference to whichever library you need:

```xml
<ProjectReference Include="..\path\to\src\Common.Mapping\Common.Mapping.csproj" />
<ProjectReference Include="..\path\to\src\Common.Mediator\Common.Mediator.csproj" />
```

Register in `Program.cs`:

```csharp
using Common.Mapping;
using Common.Mediator;

builder.Services.AddCommonMapping(typeof(Program));
builder.Services.AddCommonMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
```

## Migrating from AutoMapper / MediatR

1. Remove the `AutoMapper`, `AutoMapper.Extensions.Microsoft.DependencyInjection` and `MediatR` package references.
2. Add the project references above.
3. Replace `using AutoMapper;` with `using Common.Mapping;` and `using MediatR;` with `using Common.Mediator;`.
4. Replace `services.AddAutoMapper(...)` with `services.AddCommonMapping(...)` and `services.AddMediatR(...)` with `services.AddCommonMediator(...)`.

## Supported features

**Common.Mapping**: `CreateMap`, `ForMember` (`MapFrom`, `Ignore`, `Condition`), `ForAllMembers`, `ReverseMap`, `ConstructUsing`, `AfterMap`, in-place `Map(source, destination)`, nested and collection mapping, flattening, numeric/nullable/enum conversion, constructor binding, `AssertConfigurationIsValid`.

Not supported: `ProjectTo`, `ITypeConverter`, `IValueResolver`, `ConvertUsing`, `Include`/inheritance maps, open-generic maps, pre/post-processors, dictionary mapping.

**Common.Mediator**: `IRequest<T>`, `IRequestHandler<TRequest, TResponse>`, `IMediator.Send`. Handlers are registered as transient.

Not supported: notifications (`INotification` / `IPublisher`), pipeline behaviors, streams, pre/post-processors.

## Verifying mapping behavior

```bash
dotnet run --project tests/Common.Mapping.ParityCheck
```

It runs the same configurations through real AutoMapper 12.0.1 and Common.Mapping, then prints `PASS`/`FAIL` for each case.
