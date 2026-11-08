using FluentFTP.Exceptions;
using Renci.SshNet.Common;

namespace Sdmm.Remote;

/// <summary>The server refused a file operation (an upload, move or delete), as opposed to a connection or login problem.</summary>
public sealed class RemoteRefusedException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Recognises the server refusing to change (or read) files. Hosts lock the game's files while the server is running,
/// which shows up as exactly this: permission denied or a generic failure for an upload, move or delete. Connection,
/// login and host-key problems are not refusals; they have their own messages.
/// </summary>
public static class RemoteFailure
{
    /// <summary>The refusal in <paramref name="ex"/> or its inner exceptions, or null if it's something else.</summary>
    public static Exception? FindRefusal(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (FindRefusal(inner) is { } found) return found;
            switch (e)
            {
                case RemoteRefusedException:
                case SftpPermissionDeniedException:
                    return e;
                case SftpPathNotFoundException:
                    break;
                case SftpException: // SSH_FX_FAILURE and friends: what a locked file or folder gives on most servers
                    return e;
                case FtpAuthenticationException:
                    break;
                case FtpCommandException c when c.CompletionCode is { Length: > 0 } code && code[0] is '4' or '5':
                    return e; // 450/550 "file unavailable", 553 "not allowed" and the like
            }
        }
        return null;
    }
}
