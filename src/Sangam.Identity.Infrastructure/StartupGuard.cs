using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Sangam.Identity.Infrastructure;

/// <summary>
/// Turns a refused start into a clean exit (V-08). Until the host has started, an unhandled exception — a
/// start-up rule refusing (no key-ring certificate, no mail server, unsafe SMS) or anything else — is logged at
/// Critical with its reason and the process exits with code 1, instead of dying with exit code 139. Once the host
/// has started, unhandled exceptions are left to the runtime.
/// </summary>
public static partial class StartupGuard
{
    /// <summary>The exit code of a refused start.</summary>
    public const int RefusedExitCode = 1;

    private static int _started;

    /// <summary>
    /// Installs the guard. Call it first in <c>Program.cs</c>. It acts only when the host is the process's own
    /// program, never inside a test runner that hosts it in-process.
    /// </summary>
    /// <param name="program">The host's own assembly (<c>typeof(Program).Assembly</c>).</param>
    public static void Install(Assembly program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (Assembly.GetEntryAssembly() == program)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }
    }

    /// <summary>Marks the host as started; from then on the guard stands aside.</summary>
    public static void MarkStarted() => Interlocked.Exchange(ref _started, 1);

    /// <summary>The reason to log for a refused start: the innermost message, which is the rule's own sentence.</summary>
    /// <param name="exception">The exception.</param>
    public static string Reason(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception inner = exception;
        while (inner.InnerException is not null && inner is not InvalidOperationException)
        {
            inner = inner.InnerException;
        }

        return inner.Message;
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (Volatile.Read(ref _started) == 1 || e.ExceptionObject is not Exception exception)
        {
            return;
        }

        string reason = Reason(exception);
        string type = exception.GetType().Name;
        using (ILoggerFactory factory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true)))
        {
            ILogger logger = factory.CreateLogger("Sangam.Startup");
            LogRefused(logger, reason, type);
        }

        Environment.Exit(RefusedExitCode);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Critical, Message = "Sangam refused to start: {Reason} ({ExceptionType})")]
    private static partial void LogRefused(ILogger logger, string reason, string exceptionType);
}
