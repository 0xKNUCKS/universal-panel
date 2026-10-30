// Ported from Flow Launcher (MIT), Flow.Launcher/Helper/SingleInstance.cs
// https://github.com/Flow-Launcher/Flow.Launcher @ 440f38bd2addc49c1507ef32536edb9511477f82
// Copyright (c) 2019 Flow-Launcher. Changes: non-generic, renamed identifiers, nullable annotations.

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
/// No security checking (same caveat as upstream): an unelevated second instance can signal an
/// elevated first instance. The signal carries no data; it only asks the panel to show.
/// </remarks>
public static class SingleInstance
{
    private const string Delimiter = ":";
    private const string ChannelNameSuffix = "SingleInstanceIPCChannel";
    private const string InstanceMutexName = "UniversalPanel_Unique_Application_Mutex";

    internal static Mutex? SingleInstanceMutex { get; set; }

    /// <returns>True if this is the first instance of the application.</returns>
    public static bool InitializeAsFirstInstance()
    {
        string applicationIdentifier = InstanceMutexName + Environment.UserName;
        string channelName = string.Concat(applicationIdentifier, Delimiter, ChannelNameSuffix);

        SingleInstanceMutex = new Mutex(true, applicationIdentifier, out var firstInstance);
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
        using var pipeServer = new NamedPipeServerStream(channelName, PipeDirection.In);
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
            using var pipeClient = new NamedPipeClientStream(".", channelName, PipeDirection.Out);
            pipeClient.Connect(1000);
        }
        catch (TimeoutException)
        {
            // First instance is still starting up or is shutting down; nothing to signal.
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
