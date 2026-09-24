namespace BrainX.Core.Services.Cloud;

/// <summary>
/// A BrainX Cloud call that did not succeed, carrying the contract's error
/// <see cref="Code"/> (or a client-side one such as <c>NETWORK</c>).
///
/// UI code maps <see cref="Code"/> to a sentence the owner can act on and never
/// shows <see cref="Exception.Message"/> raw. The message is still safe to log:
/// it is built from the status code and the server's own English text, never
/// from request headers — a token never ends up in here.
/// </summary>
public sealed class CloudApiException : Exception
{
    public string Code { get; }

    /// <summary>HTTP status, or null when the request never got an answer.</summary>
    public int? StatusCode { get; }

    public CloudApiException(string code, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = string.IsNullOrWhiteSpace(code) ? CloudErrorCodes.ServerError : code;
        StatusCode = statusCode;
    }

    /// <summary>The request never reached the server, or the answer never came back.</summary>
    public bool IsNetwork => Code is CloudErrorCodes.Network or CloudErrorCodes.Timeout;

    /// <summary>The token this client holds is no longer accepted (revoked, or never valid).</summary>
    public bool IsAuthFailure => StatusCode == 401 && Code != CloudErrorCodes.InvalidLicense;

    /// <summary>Worth trying again later without the owner doing anything.</summary>
    public bool IsTransient => IsNetwork
        || Code is CloudErrorCodes.RateLimited or CloudErrorCodes.LicenseServerUnreachable or CloudErrorCodes.ServerError
        || StatusCode is >= 500;
}
