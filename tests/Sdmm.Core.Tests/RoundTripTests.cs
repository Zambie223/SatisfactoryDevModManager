using Sdmm.Core.Installing;
using Sdmm.Core.Installs;

namespace Sdmm.Core.Tests;

/// <summary>
/// Over SFTP/FTP every file-system call is a network round trip, so per-mod operations must not grow with the number
/// of installed mods. (Taking over 40 mods on a hosted server took minutes per mod when each one re-read them all.)
/// </summary>
public class RoundTripTests
{
    private sealed class CountingFs(string root) : IInstallFileSystem
    {
        private readonly LocalInstallFileSystem _inner = new(root);
        public int Calls;
        public string DisplayRoot => _inner.DisplayRoot;
        public bool FileExists(string path) { Calls++; return _inner.FileExists(path); }
        public bool DirectoryExists(string path) { Calls++; return _inner.DirectoryExists(path); }
        public IReadOnlyList<string> ListDirectories(string path) { Calls++; return _inner.ListDirectories(path); }
        public IReadOnlyList<string> ListFiles(string path) { Calls++; return _inner.ListFiles(path); }
        public string ReadAllText(string path) { Calls++; return _inner.ReadAllText(path); }
        public void WriteAllText(string path, string contents) { Calls++; _inner.WriteAllText(path, contents); }
        public void WriteFile(string path, Stream contents) { Calls++; _inner.WriteFile(path, contents); }
        public void CreateDirectory(string path) { Calls++; _inner.CreateDirectory(path); }
        public void DeleteDirectory(string path) { Calls++; _inner.DeleteDirectory(path); }
        public void MoveDirectory(string from, string to) { Calls++; _inner.MoveDirectory(from, to); }
        public void DeleteFile(string path) { Calls++; _inner.DeleteFile(path); }
    }

    [Fact]
    public void TakingOverAndHandingBackCostsTheSamePerModHoweverManyAreInstalled()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path);
        const int count = 30;
        for (int i = 0; i < count; i++)
            game.Write($"FactoryGame/Mods/Mod{i}/Mod{i}.uplugin", """{ "SemVersion": "1.0.0" }""").Write($"FactoryGame/Mods/Mod{i}/.smm", new string('a', 64));
        var fs = new CountingFs(tmp.Path);
        var install = new GameInstall("", fs);
        var installer = new Installer();

        for (int i = 0; i < count; i++) installer.Adopt(install, $"Mod{i}", new NoGameRunning());
        int perAdopt = fs.Calls / count;
        fs.Calls = 0;
        for (int i = 0; i < count; i++) installer.HandBack(install, $"Mod{i}", new NoGameRunning());
        int perHandBack = fs.Calls / count;

        // A full scan of 30 mods alone is over 100 calls; a single mod's folders are a handful.
        Assert.True(perAdopt <= 12, $"Adopt made {perAdopt} file-system calls per mod");
        Assert.True(perHandBack <= 12, $"HandBack made {perHandBack} file-system calls per mod");
    }
}
