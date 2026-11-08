using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Sdmm.Remote;

namespace Sdmm.Core.Tests;

/// <summary>
/// "The server is probably running" must only be said for the server refusing a file change, never for a wrong
/// password, a missing path or a connection problem, which have their own explanations.
/// </summary>
public class RemoteFailureTests
{
    [Fact]
    public void RefusalsAreRecognisedEvenWhenWrapped()
    {
        Assert.NotNull(RemoteFailure.FindRefusal(new SftpPermissionDeniedException("Permission denied")));
        Assert.NotNull(RemoteFailure.FindRefusal(new SftpException(Renci.SshNet.Sftp.StatusCode.Failure, "Failure")));
        Assert.NotNull(RemoteFailure.FindRefusal(new FtpCommandException("550", "Requested action not taken")));
        Assert.NotNull(RemoteFailure.FindRefusal(new InvalidOperationException("Install failed", new RemoteRefusedException("refused"))));
        Assert.NotNull(RemoteFailure.FindRefusal(new AggregateException(new IOException("x"), new SftpPermissionDeniedException("denied"))));
    }

    [Fact]
    public void LoginPathAndConnectionProblemsAreNotRefusals()
    {
        Assert.Null(RemoteFailure.FindRefusal(new SshAuthenticationException("Permission denied (password).")));
        Assert.Null(RemoteFailure.FindRefusal(new SshConnectionException("Connection lost")));
        Assert.Null(RemoteFailure.FindRefusal(new SftpPathNotFoundException("No such file")));
        Assert.Null(RemoteFailure.FindRefusal(new FtpAuthenticationException("530", "Login incorrect")));
        Assert.Null(RemoteFailure.FindRefusal(new FtpCommandException("226", "odd but not an error")));
        Assert.Null(RemoteFailure.FindRefusal(new IOException("Cannot move: the destination already exists.")));
    }
}
