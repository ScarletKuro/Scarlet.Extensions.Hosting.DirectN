// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// Modified for Scarlet.Extensions.Hosting.DirectN.Threading
// Minimal inline replacement for the subset of Microsoft.VisualStudio.Validation (MIT) used by the extracted code.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Scarlet.Extensions.Hosting.DirectN.Threading;

/// <summary>
/// Common runtime checks that throw <see cref="ArgumentException"/> upon failure.
/// </summary>
internal static class Requires
{
    /// <summary>
    /// Throws an <see cref="ArgumentNullException"/> if the specified parameter's value is null.
    /// </summary>
    /// <typeparam name="T">The type of the parameter.</typeparam>
    /// <param name="value">The value of the argument.</param>
    /// <param name="parameterName">The name of the parameter to include in any thrown exception.</param>
    /// <returns>The value of the parameter.</returns>
    [DebuggerStepThrough]
    public static T NotNull<T>([NotNull] T? value, string? parameterName)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return value;
    }

    /// <summary>
    /// Throws an exception if the specified parameter's value is null or empty.
    /// </summary>
    /// <param name="value">The value of the argument.</param>
    /// <param name="parameterName">The name of the parameter to include in any thrown exception.</param>
    [DebuggerStepThrough]
    public static void NotNullOrEmpty([NotNull] string? value, string? parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("The value cannot be an empty string.", parameterName);
        }
    }

    /// <summary>
    /// Throws an <see cref="ArgumentOutOfRangeException"/> if a condition does not evaluate to true.
    /// </summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="parameterName">The name of the parameter to include in any thrown exception.</param>
    /// <param name="message">An optional message.</param>
    [DebuggerStepThrough]
    public static void Range([DoesNotReturnIf(false)] bool condition, string? parameterName, string? message = null)
    {
        if (!condition)
        {
            throw new ArgumentOutOfRangeException(parameterName, message);
        }
    }

    /// <summary>
    /// Throws an <see cref="ArgumentException"/> if a condition does not evaluate to true.
    /// </summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="parameterName">The name of the parameter to include in any thrown exception.</param>
    /// <param name="message">The message to include in the exception.</param>
    [DebuggerStepThrough]
    public static void Argument([DoesNotReturnIf(false)] bool condition, string? parameterName, string? message)
    {
        if (!condition)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}

/// <summary>
/// Common runtime checks that throw <see cref="InvalidOperationException"/> upon failure.
/// </summary>
internal static class Verify
{
    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/> if a condition is false.
    /// </summary>
    /// <param name="condition">The condition to check.</param>
    /// <param name="message">The message to include in the exception.</param>
    [DebuggerStepThrough]
    public static void Operation([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Throws an <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="message">The message to include in the exception.</param>
    /// <returns>Nothing. This method always throws.</returns>
    [DebuggerStepThrough]
    [DoesNotReturn]
    public static Exception FailOperation(string message)
    {
        throw new InvalidOperationException(message);
    }
}

/// <summary>
/// Common runtime checks that throw <see cref="InternalErrorException"/> upon failure.
/// These indicate a bug in this library rather than in its caller.
/// </summary>
internal static class Assumes
{
    /// <summary>
    /// Throws an exception if a condition is false.
    /// </summary>
    /// <param name="condition">The condition that is expected to be true.</param>
    /// <param name="message">An optional message.</param>
    [DebuggerStepThrough]
    public static void True([DoesNotReturnIf(false)] bool condition, string? message = null)
    {
        if (!condition)
        {
            Fail(message);
        }
    }

    /// <summary>
    /// Throws an exception if a condition is true.
    /// </summary>
    /// <param name="condition">The condition that is expected to be false.</param>
    /// <param name="message">An optional message.</param>
    [DebuggerStepThrough]
    public static void False([DoesNotReturnIf(true)] bool condition, string? message = null)
    {
        if (condition)
        {
            Fail(message);
        }
    }

    /// <summary>
    /// Throws an exception if the specified value is null.
    /// </summary>
    /// <typeparam name="T">The type of value to test.</typeparam>
    /// <param name="value">The value to test.</param>
    [DebuggerStepThrough]
    public static void NotNull<T>([NotNull] T? value)
        where T : class
    {
        if (value is null)
        {
            Fail("Unexpected null value.");
        }
    }

    /// <summary>
    /// Throws an exception if the specified value is not null.
    /// </summary>
    /// <typeparam name="T">The type of value to test.</typeparam>
    /// <param name="value">The value to test.</param>
    [DebuggerStepThrough]
    public static void Null<T>(T? value)
        where T : class
    {
        if (value is not null)
        {
            Fail("Unexpected non-null value.");
        }
    }

    /// <summary>
    /// Throws an exception indicating that a code path that should never be reached was reached.
    /// </summary>
    /// <returns>Nothing. This method always throws.</returns>
    [DebuggerStepThrough]
    [DoesNotReturn]
    public static Exception NotReachable()
    {
        throw new InternalErrorException("Unreachable code path reached.");
    }

    [DoesNotReturn]
    private static void Fail(string? message)
    {
        throw new InternalErrorException(message);
    }
}

/// <summary>
/// Debug-only assertions that do not throw.
/// </summary>
internal static class Report
{
    /// <summary>
    /// Reports an error if a condition evaluates to true.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">An optional message.</param>
    [Conditional("DEBUG")]
    public static void If(bool condition, string? message = null)
    {
        if (condition)
        {
            Fail(message ?? "A recoverable error has been detected.");
        }
    }

    /// <summary>
    /// Reports an error if a condition does not evaluate to true.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="message">An optional message.</param>
    [Conditional("DEBUG")]
    public static void IfNot(bool condition, string? message = null)
    {
        if (!condition)
        {
            Fail(message ?? "A recoverable error has been detected.");
        }
    }

    /// <summary>
    /// Reports a certain failure.
    /// </summary>
    /// <param name="message">The message (may be a composite format string).</param>
    /// <param name="args">Formatting arguments.</param>
    [Conditional("DEBUG")]
    public static void Fail(string message, params object?[] args)
    {
        string formatted = args.Length > 0 ? string.Format(CultureInfo.CurrentCulture, message, args) : message;
        Debug.Fail(formatted);
    }
}

/// <summary>
/// The exception thrown when an internal assumption of this library is violated.
/// </summary>
internal sealed class InternalErrorException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InternalErrorException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public InternalErrorException(string? message)
        : base(message ?? "An internal error occurred.")
    {
    }
}
