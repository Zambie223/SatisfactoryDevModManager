namespace Sdmm.Remote;

/// <summary>
/// Trust on first use for SSH host keys and TLS certificates. The first fingerprint is accepted only if
/// <see cref="ConfirmNew"/> agrees, and is then remembered. Any later change is refused.
/// </summary>
public sealed class HostKeyTrust
{
    /// <summary>The fingerprint saved in the config, or null on first connect.</summary>
    public string? Trusted { get; private set; }

    /// <summary>Asked when there is no saved fingerprint. Return true to accept and remember it.</summary>
    public Func<string, bool> ConfirmNew { get; }

    /// <summary>Called after a new fingerprint is accepted, so the caller can save it.</summary>
    public Action<string>? Accepted { get; init; }

    /// <summary>Why the last check failed, if it did.</summary>
    public string? Rejected { get; private set; }

    public HostKeyTrust(string? trusted, Func<string, bool> confirmNew)
    {
        Trusted = string.IsNullOrWhiteSpace(trusted) ? null : trusted;
        ConfirmNew = confirmNew;
    }

    public bool Check(string fingerprint)
    {
        Rejected = null;
        if (Trusted is not null)
        {
            if (string.Equals(Trusted, fingerprint, StringComparison.Ordinal)) return true;
            Rejected = $"The server's key changed (saved {Trusted}, now {fingerprint}). This can mean the host rebuilt the server, or that someone is intercepting the connection. Check with the host, then clear the saved fingerprint in the install settings to accept the new key.";
            return false;
        }
        if (!ConfirmNew(fingerprint))
        {
            Rejected = "The server's key was not accepted.";
            return false;
        }
        Trusted = fingerprint;
        Accepted?.Invoke(fingerprint);
        return true;
    }
}
