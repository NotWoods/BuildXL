// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Utilities.Tasks;
using BuildXL.Utilities.Tracing;

#nullable enable

namespace BuildXL.Cache.ContentStore.Tracing.Internal
{
    /// <summary>
    /// Configurable defaults for tracing configuration.
    /// </summary>
    public static class DefaultTracingConfiguration
    {
        /// <summary>
        /// If an operation takes longer than this threshold it will be traced regardless of other flags or options.
        /// </summary>
        public static TimeSpan DefaultSilentOperationDurationThreshold { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// A default interval for periodically tracing pending operations.
        /// </summary>
        public static TimeSpan? DefaultPendingOperationTracingInterval { get; set; } = null;
    }

    /// <summary>
    /// A set of extension methods that create helper builder classes for configuring tracing options for an operation.
    /// </summary>
    public static class OperationContextExtensions
    {
        /// <summary>
        /// Detaches a <see cref="CancellationToken"/> instance from <paramref name="context"/>.
        /// </summary>
        public static OperationContext WithoutCancellationToken(this OperationContext context) => new OperationContext(context.TracingContext, token: CancellationToken.None);

        /// <nodoc />
        public static PerformAsyncOperationBuilder<TResult> CreateOperation<TResult>(this OperationContext context, Tracer tracer, Func<Task<TResult>> operation) where TResult : ResultBase
        {
            return new PerformAsyncOperationBuilder<TResult>(context, tracer, operation);
        }

        /// <nodoc />
        public static PerformAsyncOperationWithTimeoutBuilder<TResult> CreateOperationWithTimeout<TResult>(this OperationContext context, Tracer tracer, Func<OperationContext, Task<TResult>> operation, TimeSpan? timeout) where TResult : ResultBase
        {
            return new PerformAsyncOperationWithTimeoutBuilder<TResult>(context, tracer, operation)
                .WithTimeout(timeout);
        }

        /// <nodoc />
        public static PerformAsyncOperationNonResultBuilder<TResult> CreateNonResultOperation<TResult>(this OperationContext context, Tracer tracer, Func<Task<TResult>> operation, Func<TResult, ResultBase>? resultBaseFactory = null)
        {
            return new PerformAsyncOperationNonResultBuilder<TResult>(context, tracer, operation, resultBaseFactory);
        }

        /// <nodoc />
        public static PerformInitializationOperationBuilder<TResult> CreateInitializationOperation<TResult>(this OperationContext context, Tracer tracer, Func<Task<TResult>> operation) where TResult : ResultBase
        {
            return new PerformInitializationOperationBuilder<TResult>(context, tracer, operation);
        }

        /// <nodoc />
        public static PerformOperationBuilder<TResult> CreateOperation<TResult>(this OperationContext context, Tracer tracer, Func<TResult> operation) where TResult : ResultBase
        {
            return new PerformOperationBuilder<TResult>(context, tracer, operation);
        }
    }

    /// <nodoc />
    public static class PerformOperationExtensions
    {
        /// <summary>
        /// Create a builder that will trace only operation completion failures and ONLY when the <paramref name="enableTracing"/> is true.
        /// </summary>
        public static TBuilder TraceErrorsOnlyIfEnabled<TResult, TBuilder>(
            this PerformOperationBuilderBase<TResult, TBuilder> builder,
            bool enableTracing,
            Func<TResult, string>? endMessageFactory = null)
            where TBuilder : PerformOperationBuilderBase<TResult, TBuilder>
        {
            return builder.WithOptions(traceErrorsOnly: true, traceOperationStarted: false, traceOperationFinished: enableTracing, endMessageFactory: endMessageFactory);
        }
    }
    /// <summary>
    /// A builder pattern used for perform operations with configurable tracings.
    /// </summary>
    public abstract class PerformOperationBuilderBase<TResult, TBuilder>
        where TBuilder : PerformOperationBuilderBase<TResult, TBuilder>
    {
        /// <nodoc />
        protected readonly OperationContext _context;

        /// <nodoc />
        protected readonly Tracer _tracer;

        /// <nodoc />
        protected Counter? Counter;

        /// <nodoc />
        protected bool _traceErrorsOnly = false;

        /// <nodoc />
        protected bool _traceOperationStarted = true;

        /// <nodoc />
        protected bool _traceOperationFinished = true;

        /// <nodoc />
        protected string? _extraStartMessage;

        /// <nodoc />
        protected Func<TResult, string>? _endMessageFactory;

        /// <nodoc />
        protected TimeSpan? _silentOperationDurationThreshold;

        protected TimeSpan SilentOperationDurationThreshold => _silentOperationDurationThreshold ?? TimeSpan.MaxValue;

        /// <nodoc />
        protected bool _isCritical;

        private bool _isConfigured = false;

        private readonly Func<TResult, ResultBase>? _resultBaseFactory;

        /// <summary>
        /// An interval for periodically tracing pending operations.
        /// </summary>
        protected TimeSpan? PendingOperationTracingInterval;

        /// <summary>
        /// A name of the caller used for tracing pending operations.
        /// </summary>
        protected string? Caller;

        /// <nodoc />
        protected PerformOperationBuilderBase(OperationContext context, Tracer tracer, Func<TResult, ResultBase>? resultBaseFactory)
        {
            _context = context;
            _tracer = tracer;
            _resultBaseFactory = resultBaseFactory;
        }

        /// <summary>
        /// Appends the start message to the current start message
        /// </summary>
        public TBuilder AppendStartMessage(string extraStartMessage)
        {
            _extraStartMessage = _extraStartMessage != null
                ? string.Join(" ", _extraStartMessage, extraStartMessage)
                : extraStartMessage;
            return (TBuilder)this;
        }

        /// <summary>
        /// Set tracing options for the operation the builder is responsible for construction.
        /// </summary>
        public TBuilder WithOptions(
            Counter? counter = default,
            bool traceErrorsOnly = false,
            bool traceOperationStarted = true,
            bool traceOperationFinished = true,
            string? extraStartMessage = null,
            Func<TResult, string>? endMessageFactory = null,
            TimeSpan? silentOperationDurationThreshold = null,
            bool isCritical = false,
            TimeSpan? pendingOperationTracingInterval = null,
            string? caller = null)
        {
            Counter = counter;
            _traceErrorsOnly = traceErrorsOnly;
            _traceOperationStarted = traceOperationStarted;
            _traceOperationFinished = traceOperationFinished;
            _extraStartMessage = extraStartMessage;
            _endMessageFactory = endMessageFactory;
            _silentOperationDurationThreshold = silentOperationDurationThreshold;
            _isCritical = isCritical;
            PendingOperationTracingInterval = pendingOperationTracingInterval;
            Caller = caller;
            return (TBuilder)this;
        }

        private void ConfigureIfNeeded(string caller)
        {
            if (_isConfigured)
            {
                return;
            }

            var configuration = LogManager.GetConfiguration(_tracer.Name, caller);
            if (configuration is not null)
            {
                _traceOperationStarted = configuration.StartMessage ?? _traceOperationStarted;
                _traceErrorsOnly = configuration.ErrorsOnly ?? _traceErrorsOnly;
                _traceOperationFinished = configuration.StopMessage ?? _traceOperationFinished;
                _silentOperationDurationThreshold ??= configuration.SilentOperationDurationThreshold?.Value;
                PendingOperationTracingInterval ??= configuration.PendingOperationTracingInterval?.Value;
            }

            _silentOperationDurationThreshold ??= DefaultTracingConfiguration.DefaultPendingOperationTracingInterval;
            PendingOperationTracingInterval ??= DefaultTracingConfiguration.DefaultPendingOperationTracingInterval;

            _isConfigured = true;
        }

        /// <nodoc />
        protected void TraceOperationStarted(string caller)
        {
            ConfigureIfNeeded(caller);

            if (_traceOperationStarted && !_traceErrorsOnly)
            {
                _tracer.OperationStarted(_context, caller, enabled: true, additionalInfo: _extraStartMessage);
            }
        }

        /// <nodoc />
        protected void TraceOperationFinished(TResult result, TimeSpan duration, string caller)
        {
            ConfigureIfNeeded(caller);

            if (_traceOperationFinished || duration > SilentOperationDurationThreshold)
            {
                var traceableResult = _resultBaseFactory?.Invoke(result) ?? BoolResult.Success;
                traceableResult.SetDuration(duration);
                string message = _context.TracingContext.RequiresMessage(traceableResult, _traceErrorsOnly)
                    ? _endMessageFactory?.Invoke(result) ?? string.Empty
                    : string.Empty;

                // Marking the operation as critical failure only when it was not a cancellation.
                if (_isCritical && !traceableResult.IsCancelled && !traceableResult.Succeeded)
                {
                    traceableResult.MakeCritical();
                }

                _tracer.OperationFinished(
                    _context,
                    traceableResult,
                    duration,
                    message,
                    caller,
                    // Ignoring _traceErrorsOnly flag if the operation is too long.
                    traceErrorsOnly: duration > SilentOperationDurationThreshold ? false : _traceErrorsOnly);
            }
        }

        /// <nodoc />
        protected void TracePendingOperation(StopwatchSlim stopwatch)
        {
            string extraStartMessage = !string.IsNullOrEmpty(_extraStartMessage) ? " Start message: " + _extraStartMessage : string.Empty;
            _tracer.Debug(_context,
                $"The operation '{_tracer.Name}.{Caller}' has been running for '{stopwatch.Elapsed}' and is not finished yet.{extraStartMessage}",
                // Propagate the right operation name and not put 'TracePendingOperations' in telemetry
                operation: Caller);
        }

        /// <nodoc />
        [StackTraceHidden]
        protected T RunOperationAndConvertExceptionToError<T>(Func<T> operation)
            where T : ResultBase
        {
            try
            {
                // No need to run anything if the cancellation is requested already.
                _context.Token.ThrowIfCancellationRequested();
                return operation();
            }
            catch (Exception ex)
            {
                return FromException<T>(ex);
            }
        }

        /// <nodoc />
        [StackTraceHidden]
        protected async Task<T> RunOperationAndConvertExceptionToErrorAsync<T>(Func<Task<T>> operation, StopwatchSlim stopwatch)
            where T : ResultBase
        {
            try
            {
                // No need to run anything if the cancellation is requested already.
                _context.Token.ThrowIfCancellationRequested();

                using var timer = CreatePeriodicTimerIfNeeded(stopwatch);
                return await operation();
            }
            catch (Exception ex)
            {
                return FromException<T>(ex);
            }
        }

        /// <nodoc />
        protected Timer? CreatePeriodicTimerIfNeeded(StopwatchSlim stopwatch)
        {
            if (PendingOperationTracingInterval == null || PendingOperationTracingInterval.Value == TimeSpan.MaxValue || PendingOperationTracingInterval.Value == Timeout.InfiniteTimeSpan)
            {
                return null;
            }

            return new Timer(
                static state =>
                {
                    var (@this, stopwatch) = ((PerformOperationBuilderBase<TResult, TBuilder> Instance, StopwatchSlim Stopwatch))state!;
                    @this.TracePendingOperation(stopwatch);
                },
                (Instance: this, Stopwatch: stopwatch),
                PendingOperationTracingInterval.Value,
                PendingOperationTracingInterval.Value);
        }

        /// <nodoc />
        protected T FromException<T>(Exception ex) where T : ResultBase
        {
            var result = new ErrorResult(ex).AsResult<T>();
            MarkResultIsCancelledIfNeeded(result, ex);
            return result;
        }

        /// <nodoc />
        protected void MarkResultIsCancelledIfNeeded(ResultBase result, Exception ex)
        {
            if (_context.Token.IsCancellationRequested && ResultBase.NonCriticalForCancellation(ex))
            {
                // Set the cancellation flag only when the error is non-critical.
                result.IsCancelled = true;
            }
        }
    }

    /// <summary>
    /// A builder pattern used for perform operations with configurable tracings.
    /// </summary>
    public class PerformAsyncOperationNonResultBuilder<TResult> : PerformOperationBuilderBase<TResult, PerformAsyncOperationNonResultBuilder<TResult>>
    {
        /// <nodoc />
        protected readonly Func<Task<TResult>> AsyncOperation;

        /// <nodoc />
        public PerformAsyncOperationNonResultBuilder(OperationContext context, Tracer tracer, Func<Task<TResult>> operation, Func<TResult, ResultBase>? resultBaseFactory)
        : base(context, tracer, resultBaseFactory)
        {
            AsyncOperation = operation;
        }

        /// <nodoc />
        [StackTraceHidden]
        public async Task<TResult> RunAsync([CallerMemberName] string? caller = null)
        {
            // If the caller was not set by 'WithOptions' call, setting it here.
            Caller ??= caller;

            using (Counter?.Start())
            {
                TraceOperationStarted(caller!);
                var stopwatch = StopwatchSlim.Start();

                try
                {
                    // No need to run anything if the cancellation is requested already.
                    _context.Token.ThrowIfCancellationRequested();

                    var result = await AsyncOperation();
                    TraceOperationFinished(result, stopwatch.Elapsed, caller!);

                    return result;
                }
                catch (Exception e)
                {
                    var resultBase = new BoolResult(e);

                    MarkResultIsCancelledIfNeeded(resultBase, e);

                    // Marking the operation as critical failure only when it was not a cancellation.
                    if (_isCritical && !resultBase.IsCancelled)
                    {
                        resultBase.MakeCritical();
                    }

                    TraceResultOperationFinished(resultBase, stopwatch.Elapsed, caller!);

                    throw;
                }
            }
        }

        private void TraceResultOperationFinished<TOther>(TOther result, TimeSpan duration, string caller) where TOther : ResultBase
        {
            var traceOperationFinished = _traceOperationFinished;
            var traceErrorsOnly = _traceErrorsOnly;

            var configuration = LogManager.GetConfiguration(_tracer.Name, caller);
            if (configuration is not null)
            {
                traceOperationFinished = configuration.StopMessage ?? traceOperationFinished;
                traceErrorsOnly = configuration.ErrorsOnly ?? traceErrorsOnly;
            }

            if (traceOperationFinished || duration > SilentOperationDurationThreshold)
            {
                // Ignoring _traceErrorsOnly flag if the operation is too long.
                _tracer.OperationFinished(
                    _context,
                    result,
                    duration,
                    message: string.Empty,
                    caller,
                    traceErrorsOnly: duration > SilentOperationDurationThreshold ? false : traceErrorsOnly);
            }
        }
    }

    /// <summary>
    /// A builder pattern used for perform operations with configurable tracings.
    /// </summary>
    public class PerformAsyncOperationBuilder<TResult> : PerformOperationBuilderBase<TResult, PerformAsyncOperationBuilder<TResult>>
        where TResult : ResultBase
    {
        /// <nodoc />
        protected readonly Func<Task<TResult>> AsyncOperation;

        /// <nodoc />
        public PerformAsyncOperationBuilder(OperationContext context, Tracer tracer, Func<Task<TResult>> operation)
        : base(context, tracer, r => r)
        {
            AsyncOperation = operation;
        }

        /// <nodoc />
        [StackTraceHidden]
        public virtual async Task<TResult> RunAsync([CallerMemberName] string? caller = null)
        {
            // If the caller was not set by 'WithOptions' call, setting it here.
            Caller ??= caller;

            using (Counter?.Start())
            {
                TraceOperationStarted(caller!);
                var stopwatch = StopwatchSlim.Start();

                var result = await RunOperationAndConvertExceptionToErrorAsync(AsyncOperation, stopwatch);

                TraceOperationFinished(result, stopwatch.Elapsed, caller!);

                return result;
            }
        }
    }

    /// <summary>
    /// A builder pattern used for perform operations with configurable tracings.
    /// </summary>
    public class PerformAsyncOperationWithTimeoutBuilder<TResult> : PerformOperationBuilderBase<TResult, PerformAsyncOperationWithTimeoutBuilder<TResult>>
        where TResult : ResultBase
    {
        /// <summary>
        /// An optional timeout for asynchronous operations.
        /// </summary>
        protected TimeSpan? _timeout;

        /// <nodoc />
        protected readonly Func<OperationContext, Task<TResult>> AsyncOperation;

        /// <nodoc />
        public PerformAsyncOperationWithTimeoutBuilder(OperationContext context, Tracer tracer, Func<OperationContext, Task<TResult>> operation)
        : base(context, tracer, r => r)
        {
            AsyncOperation = operation;
        }

        /// <nodoc />
        [StackTraceHidden]
        public PerformAsyncOperationWithTimeoutBuilder<TResult> WithTimeout(TimeSpan? timeout)
        {
            _timeout = timeout;
            return this;
        }

        /// <nodoc />
        [StackTraceHidden]
        public virtual async Task<TResult> RunAsync([CallerMemberName] string? caller = null)
        {
            // If the caller was not set by 'WithOptions' call, setting it here.
            Caller ??= caller;

            using (Counter?.Start())
            {
                TraceOperationStarted(caller!);
                var stopwatch = StopwatchSlim.Start();

                var result = await RunOperationAndConvertExceptionToErrorAsync(AsyncOperation, stopwatch);

                TraceOperationFinished(result, stopwatch.Elapsed, caller!);

                return result;
            }
        }

        /// <nodoc />
        [StackTraceHidden]
        protected async Task<T> RunOperationAndConvertExceptionToErrorAsync<T>(Func<OperationContext, Task<T>> operation, StopwatchSlim stopwatch)
            where T : ResultBase
        {
            try
            {
                // No need to run anything if the cancellation is requested already.
                _context.Token.ThrowIfCancellationRequested();

                using var timer = CreatePeriodicTimerIfNeeded(stopwatch);

                return await WithOptionalTimeoutAsync(operation, _timeout, _context, caller: Caller);
            }
            catch (Exception ex)
            {
                return FromException<T>(ex);
            }
        }

        /// <nodoc />
        [StackTraceHidden]
        public static async Task<T> WithOptionalTimeoutAsync<T>(Func<OperationContext, Task<T>> operation, TimeSpan? timeout, OperationContext context, [CallerMemberName] string? caller = null) where T : ResultBase
        {
            if (timeout == null || timeout.Value == TimeSpan.MaxValue)
            {
                return await operation(context);
            }

            try
            {
                return await TaskUtilities.WithTimeoutAsync(async ct =>
                {
                    // If the operation does any synchronous work before returning the task, our timeout mechanism
                    // will never kick in. This yield is here to prevent that from happening.
                    await Task.Yield();
                    var nestedContext = new OperationContext(context.TracingContext, ct);
                    return await operation(nestedContext);
                },
                    timeout.Value,
                    context.Token);
            }
            catch (TimeoutException exception)
            {
                return new ErrorResult(exception, $"The operation '{caller}' has timed out after '{timeout}'.").AsResult<T>();
            }
        }
    }

    /// <summary>
    /// A builder pattern used for perform operations with configurable tracings.
    /// </summary>
    public class PerformOperationBuilder<TResult> : PerformOperationBuilderBase<TResult, PerformOperationBuilder<TResult>>
        where TResult : ResultBase
    {
        private readonly Func<TResult> _operation;

        /// <nodoc />
        public PerformOperationBuilder(OperationContext context, Tracer tracer, Func<TResult> operation)
            : base(context, tracer, r => r)
        {
            _operation = operation;
        }

        /// <nodoc />
        public TResult Run([CallerMemberName] string? caller = null)
        {
            using (Counter?.Start())
            {
                TraceOperationStarted(caller!);

                var stopwatch = StopwatchSlim.Start();

                var result = RunOperationAndConvertExceptionToError(_operation);

                TraceOperationFinished(result, stopwatch.Elapsed, caller!);
                return result;
            }
        }
    }

    /// <summary>
    /// A builder pattern used for perform initialization operations with configurable tracings.
    /// </summary>
    public class PerformInitializationOperationBuilder<TResult> : PerformAsyncOperationBuilder<TResult>
        where TResult : ResultBase
    {
        /// <nodoc />
        public PerformInitializationOperationBuilder(OperationContext context, Tracer tracer, Func<Task<TResult>> operation)
        : base(context, tracer, operation)
        {
        }

        /// <inheritdoc />
        public override async Task<TResult> RunAsync([CallerMemberName] string? caller = null)
        {
            // If the caller was not set by 'WithOptions' call, setting it here.
            Caller ??= caller;

            using (Counter?.Start())
            {
                TraceOperationStarted(caller!);

                var stopwatch = StopwatchSlim.Start();

                var result = await RunOperationAndConvertExceptionToErrorAsync(AsyncOperation, stopwatch);

                TraceInitializationFinished(result, stopwatch.Elapsed, caller!);

                return result;
            }
        }

        private void TraceInitializationFinished(TResult result, TimeSpan duration, string caller)
        {
            if (_traceOperationFinished)
            {
                string extraMessage = _endMessageFactory?.Invoke(result) ?? string.Empty;
                string message = _tracer.CreateMessageText(result, duration, extraMessage, caller);
                _tracer.InitializationFinished(_context, result, duration, message, caller);
            }
        }
    }
}
