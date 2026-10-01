using System;
using System.Threading;
using Writeback.Execution;

namespace Writeback;

/// <summary>
/// Process-wide configuration, in the same spirit as Dapper's own static <c>SqlMapper</c> settings.
/// Call <see cref="Configure"/> once at startup, before the first Writeback call.
/// </summary>
public static class WritebackConfig
{
    private static EntityModel? _model;

    /// <summary>
    /// Applies configuration and validates every fluently configured entity immediately, so mapping errors surface at
    /// startup. Calling it again (e.g. from a test host that runs startup per test) atomically replaces the configuration
    /// and all cached SQL; calls already in flight finish with the previous configuration.
    /// </summary>
    /// <exception cref="EntityMappingException">A fluently configured entity cannot be mapped.</exception>
    public static void Configure(Action<WritebackOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new WritebackOptions();
        configure(options);
        var model = new EntityModel(options);
        foreach (var entityType in options.Entities.Keys)
        {
            model.GetMap(entityType);
        }

        Volatile.Write(ref _model, model);
    }

    internal static EntityModel Model
    {
        get
        {
            var model = Volatile.Read(ref _model);
            if (model is not null)
            {
                return model;
            }

            Interlocked.CompareExchange(ref _model, new EntityModel(new WritebackOptions()), null);
            return Volatile.Read(ref _model)!;
        }
    }
}
