using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gelatinarm.Shared.Errors;
using Microsoft.Extensions.Logging;

// Stand-ins for the app types SeerrService uses; the real ones need UWP

namespace Gelatinarm.Shared.Errors
{
    public enum ErrorCategory
    {
        User,
        Network,
        System,
        Media,
        Authentication,
        Configuration
    }

    public enum ErrorSeverity
    {
        Warning,
        Error
    }

    public record ErrorContext(string Source, string Operation, ErrorCategory Category, ErrorSeverity Severity);

    public interface IErrorHandlingService
    {
        Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue);
    }

    // Records what reached the error handler, so a test can tell a quiet "no Seerr" from a failure
    public class RecordingErrorHandler : IErrorHandlingService
    {
        public List<Exception> Errors { get; } = new();

        public Task<T> HandleErrorAsync<T>(Exception exception, ErrorContext context, T defaultValue)
        {
            Errors.Add(exception);
            return Task.FromResult(defaultValue);
        }
    }
}

namespace Gelatinarm.SignIn
{
    public interface IAuthenticationService
    {
        string ServerUrl { get; }
        string AccessToken { get; }
        event EventHandler UserChanged;
    }
}

namespace Gelatinarm.Shared.Base
{
    public abstract class BaseService : IDisposable
    {
        protected readonly ILogger Logger;

        protected BaseService(ILogger logger)
        {
            Logger = logger;
        }

        public RecordingErrorHandler Errors { get; } = new();

        protected IErrorHandlingService ErrorHandler => Errors;

        protected bool IsDisposed { get; private set; }

        protected ErrorContext CreateErrorContext(string operation, ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            return new ErrorContext(GetType().Name, operation, category, severity);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                UnsubscribeEvents();
            }

            IsDisposed = true;
        }

        protected virtual void UnsubscribeEvents()
        {
        }
    }
}
