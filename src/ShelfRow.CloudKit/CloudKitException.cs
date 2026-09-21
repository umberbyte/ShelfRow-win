using System;
using System.Net;

namespace ShelfRow.CloudKit;

/// <summary>
/// A CloudKit Web Services error response. Carries the server's own diagnosis
/// rather than a bare HTTP status, because CloudKit answers most failures with
/// a 4xx plus a JSON body that says what to do about it.
/// </summary>
public class CloudKitException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ServerErrorCode { get; }
    public string? Reason { get; }

    /// <summary>
    /// Where to send the user to sign in. Present when <see cref="IsAuthenticationRequired"/>.
    /// </summary>
    public string? RedirectUrl { get; }

    public bool IsAuthenticationRequired =>
        ServerErrorCode == "AUTHENTICATION_REQUIRED" || ServerErrorCode == "AUTHENTICATION_FAILED";

    public CloudKitException(HttpStatusCode statusCode, string? serverErrorCode, string? reason, string? redirectUrl)
        : base($"CloudKit {(int)statusCode} {serverErrorCode ?? statusCode.ToString()}: {reason ?? "(no reason given)"}")
    {
        StatusCode = statusCode;
        ServerErrorCode = serverErrorCode;
        Reason = reason;
        RedirectUrl = redirectUrl;
    }
}
