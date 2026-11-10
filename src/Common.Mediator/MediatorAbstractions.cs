// -----------------------------------------------------------------------------
//  Zero-dependency mediator with a MediatR-style API.
//
//  Type names and member signatures match MediatR, so migrating a project only
//  requires swapping `using MediatR;` for `using Common.Mediator;`.
//
//  Implemented:
//      * IRequest<TResponse>                 - request/command marker
//      * IRequestHandler<TRequest,TResponse> - request handler
//      * IMediator.Send(...)                 - request dispatch
//
//  Notifications (INotification/IPublisher), pipeline behaviors, streams and
//  pre/post-processors are not provided. Extend this project if they are needed.
// -----------------------------------------------------------------------------

namespace Common.Mediator;

/// <summary>Marker interface to represent a request.</summary>
public interface IBaseRequest
{
}

/// <summary>Marker interface to represent a request with a response.</summary>
/// <typeparam name="TResponse">Response type.</typeparam>
public interface IRequest<out TResponse> : IBaseRequest
{
}

/// <summary>Defines a handler for a request.</summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response from the handler.</typeparam>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>Handles a request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Response from the request.</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Sends requests to a single handler.</summary>
public interface IMediator
{
    /// <summary>Asynchronously send a request to a single handler.</summary>
    /// <typeparam name="TResponse">Response type.</typeparam>
    /// <param name="request">Request object.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that represents the send operation. The task result contains the handler response.</returns>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
