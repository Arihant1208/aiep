using System.Collections.Frozen;

namespace Aiep.Application.Execution;

/// <summary>
/// The in-memory handler allowlist (ADR-0007). Built once from the handlers registered in DI; a definition version
/// whose (handler_type, handler_version) is not present cannot run.
/// </summary>
public sealed class HandlerRegistry : IHandlerRegistry
{
    private readonly FrozenDictionary<(string Type, string Version), ITaskHandler> _handlers;

    public HandlerRegistry(IEnumerable<ITaskHandler> handlers)
    {
        _handlers = handlers.ToFrozenDictionary(h => (h.HandlerType, h.Version));
    }

    public ITaskHandler? Resolve(string handlerType, string version) =>
        _handlers.TryGetValue((handlerType, version), out var handler) ? handler : null;
}
