using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Mediator;

/// <summary>
/// Default <see cref="IMediator"/> implementation. Resolves the matching
/// <see cref="IRequestHandler{TRequest,TResponse}"/> from the supplied
/// <see cref="IServiceProvider"/> and invokes it.
/// </summary>
/// <remarks>
/// Registered as transient and given the request-scoped <see cref="IServiceProvider"/>
/// via constructor injection, so handlers (and their scoped dependencies such as
/// repositories / DbContext / UnitOfWork) resolve from the correct DI scope.
/// </remarks>
public sealed class Mediator : IMediator
{
    // Maps a (request type, response type) pair to a strongly-typed dispatch wrapper.
    // The response type MUST be part of the key: the same request type can be sent with
    // different TResponse arguments via IRequest<out TResponse> covariance (e.g. Send<object>),
    // and a request-type-only key would return a wrapper of the wrong TResponse and throw
    // InvalidCastException below. The wrappers are stateless (they resolve the handler fresh on
    // every call), so a process-wide cache is safe and avoids repeated reflection.
    private static readonly ConcurrentDictionary<(Type RequestType, Type ResponseType), RequestHandlerWrapper> Wrappers = new();

    private readonly IServiceProvider _serviceProvider;

    public Mediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)Wrappers.GetOrAdd(
            (request.GetType(), typeof(TResponse)),
            static key =>
            {
                var wrapperType = typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(key.RequestType, key.ResponseType);
                return (RequestHandlerWrapper)Activator.CreateInstance(wrapperType)!;
            });

        return wrapper.Handle(request, _serviceProvider, cancellationToken);
    }
}

// Non-generic base so wrappers of differing TResponse can share one cache.
internal abstract class RequestHandlerWrapper
{
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapper
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

// Closed generic wrapper: knows the concrete TRequest, so it can resolve the
// strongly-typed handler and invoke it without per-call reflection.
internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        return handler.Handle((TRequest)request, cancellationToken);
    }
}
