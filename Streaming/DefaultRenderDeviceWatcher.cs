using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioStreamer
{
    /// <summary>
    /// Watches Windows' default *render* endpoint and reports when the user switches it.
    ///
    /// Why this exists: <c>WasapiOut</c> resolves <c>GetDefaultAudioEndpoint(Render, Console)</c> once, in its
    /// constructor, and then renders to that endpoint for its whole life. When the default output is changed while
    /// the old endpoint is still present and enabled, WASAPI does *not* invalidate the stream — so no
    /// <c>PlaybackStopped</c> fires and the existing device-loss recovery (OnPlaybackStopped -> RestartOutput) never
    /// runs. Audio simply keeps going to the previous device, which sounds like silence from the new one until the
    /// session is restarted. Endpoint notifications are the only way to see the change, so we subscribe to them.
    ///
    /// Lifetime: construct, <see cref="Start"/> to register with the OS, <see cref="Dispose"/> to unregister. The
    /// enumerator is held in a field because disposing it would drop the registration. Constructing without
    /// starting is inert (and is how the tests exercise the filter headlessly).
    /// </summary>
    internal sealed class DefaultRenderDeviceWatcher : IMMNotificationClient, IDisposable
    {
        // The role WasapiOut's default-device constructor binds. Windows raises OnDefaultDeviceChanged once per
        // (flow, role) pair, so one click in Sound settings arrives as up to six callbacks; filtering to the one
        // role we actually render on collapses that to a single rebuild.
        private const Role WatchedRole = Role.Console;

        private readonly Action<string> onDefaultRenderDeviceChanged;
        private readonly Action<string> logLine;

        // Notifications arrive on OS-owned threads with no ordering guarantee across them, so the
        // compare-and-record of the last device id is locked rather than a bare field write.
        private readonly object gate = new();
        private string? lastDeviceId;
        private bool disposed;

        private MMDeviceEnumerator? enumerator;

        internal DefaultRenderDeviceWatcher(Action<string> onDefaultRenderDeviceChanged, Action<string> logLine)
        {
            this.onDefaultRenderDeviceChanged = onDefaultRenderDeviceChanged;
            this.logLine = logLine;
        }

        /// <summary>
        /// Registers for endpoint notifications. Best-effort: if registration fails we log and carry on without
        /// device-switch following rather than failing the session start — the stream still plays, just to the
        /// endpoint it started on (the pre-fix behaviour).
        /// </summary>
        internal void Start()
        {
            try
            {
                var mmEnumerator = new MMDeviceEnumerator();
                try
                {
                    // Seed with the endpoint the output is about to bind to, so a redundant notification naming the
                    // device we are already rendering on can't trigger a pointless rebuild (which costs an audible
                    // ClearBuffer + re-cushion gap). A failure here just means the first notification is treated as
                    // a genuine change, which is the safe direction.
                    lastDeviceId = mmEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, WatchedRole).ID;
                }
                catch { /* no default render endpoint right now — leave unseeded */ }

                mmEnumerator.RegisterEndpointNotificationCallback(this);
                enumerator = mmEnumerator;
            }
            catch (Exception ex)
            {
                logLine($"Could not subscribe to audio endpoint notifications ({ex.Message}); "
                      + "a default output device change will not be followed until the session is restarted.");
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;   // read under the same lock by OnDefaultDeviceChanged, so a callback racing
                                   // teardown can't reach the session after Stop() has begun
            }

            var mmEnumerator = enumerator;
            enumerator = null;
            if (mmEnumerator == null)
                return;
            try { mmEnumerator.UnregisterEndpointNotificationCallback(this); } catch { /* already gone */ }
            try { mmEnumerator.Dispose(); } catch { /* already gone */ }
        }

        // --- IMMNotificationClient. Called by the OS on its own threads. ---

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // This is a callback from unmanaged code: an escaping exception would cross the COM boundary, so
            // everything here is guarded. The handler itself only queues work (see ReceiverSession) — the OS
            // notification thread must never be blocked.
            try
            {
                if (flow != DataFlow.Render || role != WatchedRole)
                    return;

                lock (gate)
                {
                    if (disposed || defaultDeviceId == lastDeviceId)
                        return;
                    lastDeviceId = defaultDeviceId;
                    // A null id means the last render endpoint went away: there is nothing to rebuild onto, and
                    // that case belongs to the device-loss path (PlaybackStopped -> RestartOutput). We still
                    // recorded it above, so returning to the *same* device afterwards reads as a change and
                    // rebuilds — which it must, since the output was torn down in the meantime.
                    if (string.IsNullOrEmpty(defaultDeviceId))
                        return;
                }

                onDefaultRenderDeviceChanged(defaultDeviceId);
            }
            catch { /* never throw into the COM callback */ }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
