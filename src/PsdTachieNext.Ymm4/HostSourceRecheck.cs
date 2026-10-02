using Microsoft.Win32;
using PsdTachieNext.Compiler;
using System.Windows;

namespace PsdTachieNext.Ymm4;

/// <summary>Public application activation/power resume only. Does not request a player redraw or touch GPU objects.</summary>
internal static class HostSourceRecheck
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SourcePreparationService, int> Users = [];
    private static Application? application;
    private static int rechecking;
    internal static Exception? LastError { get; private set; }
    internal static IDisposable Subscribe(SourcePreparationService service)
    {
        lock (Gate)
        {
            if (Users.Count == 0)
            {
                application = Application.Current;
                if (application is not null) application.Activated += Activated;
                SystemEvents.PowerModeChanged += PowerChanged;
            }
            Users[service] = Users.GetValueOrDefault(service) + 1;
        }
        return new Subscription(service);
    }
    private static void Activated(object? sender, EventArgs args) => Recheck();
    private static void PowerChanged(object? sender, PowerModeChangedEventArgs args)
    { if (args.Mode == PowerModes.Resume) Recheck(); }
    private static void Recheck()
    {
        if (Interlocked.Exchange(ref rechecking, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                SourcePreparationService[] services; lock (Gate) services = Users.Keys.ToArray();
                foreach (var service in services) service.RecheckWatchedSources();
                LastError = null;
            }
            catch (Exception error) { LastError = error; }
            finally { Volatile.Write(ref rechecking, 0); }
        }); // Observe failures and keep watcher I/O / source owner locks off the activation thread.
    }
    private sealed class Subscription(SourcePreparationService service) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (Gate)
            {
                if (--Users[service] != 0) return;
                Users.Remove(service);
                if (Users.Count != 0) return;
                if (application is not null) application.Activated -= Activated;
                application = null; SystemEvents.PowerModeChanged -= PowerChanged;
            }
        }
    }
}
