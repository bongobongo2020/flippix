extern alias remote;

using System;
using System.IO;
using remote::FlipPix.Remote.Host;
using remote::FlipPix.Remote.Library;
using FlipPix.Core.Models;

namespace FlipPix.Tests;

/// <summary>
/// The two checks between the LAN and the desktop's files: a phone pairs only with the code on screen
/// (and can't guess it), and a library id can't name a file outside the output folder.
/// </summary>
public sealed class RemoteSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flippix-tests-" + Guid.NewGuid().ToString("N"));

    public RemoteSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private RemoteHost NewHost()
    {
        var host = new RemoteHost(() => new ComfyUISettings(), null, a => a(),
            Path.Combine(_dir, "remote.json"), Path.Combine(_dir, "data"));
        host.NewCodeCommand.Execute(null);
        return host;
    }

    private static string Code(RemoteHost host) => host.PairingCode.Replace(" ", "");

    private static string WrongCode(RemoteHost host) => Code(host) == "000000" ? "000001" : "000000";

    [Fact]
    public void TheCodeOnScreen_Pairs()
    {
        var host = NewHost();
        Assert.NotNull(host.TryPair(Code(host), "Phone"));
    }

    [Fact]
    public void FiveWrongCodes_RefuseEvenTheRightOneForAWhile()
    {
        var host = NewHost();
        for (var i = 0; i < 5; i++) Assert.Null(host.TryPair(WrongCode(host), "Guesser"));

        Assert.Null(host.TryPair(Code(host), "Guesser"));
    }

    [Fact]
    public void LibraryId_CannotLeaveTheOutputFolder()
    {
        var root = Path.Combine(_dir, "output");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "inside.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "secret.png"), new byte[] { 1 });
        var library = new LibraryIndex(() => root);

        Assert.NotNull(library.Find(LibraryIndex.IdOf("inside.png")));
        Assert.Null(library.Find(LibraryIndex.IdOf("../secret.png")));
        Assert.Null(library.Find(LibraryIndex.IdOf(Path.Combine(_dir, "secret.png"))));
    }
}
