// Ported from Flow Launcher (MIT), Flow.Launcher/Helper/SingleInstance.cs
// https://github.com/Flow-Launcher/Flow.Launcher @ 440f38bd2addc49c1507ef32536edb9511477f82
// Copyright (c) 2019 Flow-Launcher. Changes: non-generic, renamed identifiers, nullable annotations.

using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace UniversalPanel.Shell;

public interface ISingleInstanceApp
{
    void OnSecondAppStarted();
}

/// <summary>
/// Ensures only one instance runs per user. A second launch signals the first one over a named
/// pipe (so it can show the panel) and then exits.
/// </summary>
/// <remarks>
/// Hardened vs. upstream: the pipe's DACL grants only the current user's SID, so only processes
/// running as the same user can connect, and the mutex lives in the per-session <c>Local\</c>
/// namespace. The signal carries no data and the server never reads from the pipe; connecting only
/// asks the panel to show.
/// The user SID (not <see cref="PipeOptions.CurrentUserOnly"/>, which compares token owners) keeps
/// this working across elevation: an elevated token's owner is Administrators, so a normal launch
/// was refused by an elevated panel even though it's the same user.
/// </remarks>
public static class SingleInstance
{
    private const string Delimiter = ":";
    private const string ChannelNameSuffix = "SingleInstanceIPCChannel";
    private const string InstanceMutexName = @"Local\UniversalPanel_Unique_Application_Mutex";

    internal static Mutex? SingleInstanceMutex { get; set; }

    /// <returns>True if this is the first instance of the application.</returns>
    public static bool InitializeAsFirstInstance()
    {
        string applicationIdentifier = InstanceMutexName + Environment.UserName;
        // Pipe names can't contain the "Local\" prefix's backslash.
        string channelName = string.Concat(applicationIdentifier.Replace('\\', '_'), Delimiter, ChannelNameSuffix);

        bool firstInstance;
        try
        {
            SingleInstanceMutex = new Mutex(true, applicationIdentifier, out firstInstance);
        }
        catch (UnauthorizedAccessException)
        {
            // It exists but was created by an elevated instance, whose default DACL keeps a normal
            // process out. That still means "already running".
            firstInstance = false;
        }

        if (firstInstance)
        {
            _ = CreateRemoteServiceAsync(channelName);
            return true;
        }

        SignalFirstInstance(channelName);
        return false;
    }

    public static void Cleanup()
    {
        SingleInstanceMutex?.ReleaseMutex();
    }

    private static async Task CreateRemoteServiceAsync(string channelName)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No user SID for the current token.");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));

        using var pipeServer = NamedPipeServerStreamAcl.Create(
            channelName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
        while (true)
        {
            await pipeServer.WaitForConnectionAsync();

            Application.Current?.Dispatcher.Invoke(ActivateFirstInstance);

            pipeServer.Disconnect();
        }
    }

    // Upstream fires this as an un-awaited async call and then lets Main return, which can end the
    // process before the connect completes. We block briefly instead so the signal is delivered.
    private static void SignalFirstInstance(string channelName)
    {
        try
        {
            // No CurrentUserOnly here (see remarks); the server's DACL is what restricts access,
            // and the client sends nothing, so even a spoofed server would learn nothing.
            using var pipeClient = new NamedPipeClientStream(".", channelName, PipeDirection.Out);
            pipeClient.Connect(1000);
        }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException or IOException)
        {
            // First instance is starting/stopping, or the pipe belongs to another user (rejected by
            // its DACL). Either way there's nothing safe to signal.
        }
    }

    private static void ActivateFirstInstance()
    {
        if (Application.Current is ISingleInstanceApp app)
        {
            app.OnSecondAppStarted();
        }
    }
}
