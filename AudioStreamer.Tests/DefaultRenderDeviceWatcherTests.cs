using NAudio.CoreAudioApi;
using Xunit;

namespace AudioStreamer.Tests
{
    /// <summary>
    /// Guards the notification filter in <see cref="DefaultRenderDeviceWatcher"/>. The watcher is constructed but
    /// never Start()ed here, so no COM registration happens and these run headless on any machine — the
    /// IMMNotificationClient methods are invoked directly, exactly as the OS would call them.
    ///
    /// The filter matters because Windows fires OnDefaultDeviceChanged once per (flow, role) pair: a single click
    /// in Sound settings produces up to six callbacks. Without the filter the receiver would tear down and rebuild
    /// its WasapiOut several times over, each rebuild costing an audible ClearBuffer + re-cushion gap.
    /// </summary>
    public class DefaultRenderDeviceWatcherTests
    {
        private static DefaultRenderDeviceWatcher Watcher(List<string> changes, Role watchedRole = Role.Console) =>
            new DefaultRenderDeviceWatcher(changes.Add, _ => { }, watchedRole);

        [Fact]
        public void RenderConsoleChange_RaisesTheCallback()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");

            Assert.Equal(new[] { "{device-b}" }, changes);
        }

        [Fact]
        public void CaptureFlowChange_IsIgnored()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            // Selecting a different microphone must not disturb playback.
            watcher.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "{mic-b}");

            Assert.Empty(changes);
        }

        [Theory]
        [InlineData(Role.Multimedia)]
        [InlineData(Role.Communications)]
        public void NonConsoleRoleChange_IsIgnored(Role role)
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            // WasapiOut's default-device constructor binds Role.Console, so only that role tells us the endpoint
            // we are actually rendering to has moved. The sibling roles are duplicates of the same user action.
            watcher.OnDefaultDeviceChanged(DataFlow.Render, role, "{device-b}");

            Assert.Empty(changes);
        }

        [Fact]
        public void RepeatedNotificationForTheSameDevice_RaisesTheCallbackOnce()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");

            Assert.Equal(new[] { "{device-b}" }, changes);
        }

        [Fact]
        public void SwitchingAwayAndBack_RaisesTheCallbackEachTime()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-a}");

            Assert.Equal(new[] { "{device-b}", "{device-a}" }, changes);
        }

        [Fact]
        public void NoDefaultDevice_IsIgnoredButStillTracked()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");
            // Windows passes a null id when the last render endpoint disappears. There is nothing to rebuild onto,
            // and the device-loss path (PlaybackStopped -> RestartOutput) owns that case...
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, null!);
            // ...but the id must still be recorded, or coming back to the SAME device would look unchanged and
            // the rebuild would be suppressed exactly when it is needed.
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");

            Assert.Equal(new[] { "{device-b}", "{device-b}" }, changes);
        }

        [Fact]
        public void UnrelatedDeviceNotifications_AreIgnored()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            watcher.OnDeviceAdded("{device-c}");
            watcher.OnDeviceRemoved("{device-c}");
            watcher.OnDeviceStateChanged("{device-c}", DeviceState.Disabled);
            watcher.OnPropertyValueChanged("{device-c}", default);

            Assert.Empty(changes);
        }

        [Fact]
        public void WatchingMultimediaRole_ConsoleRoleChangeIsIgnoredButMultimediaIsNot()
        {
            // The sender's loopback capture binds Role.Multimedia (unlike WasapiOut's Role.Console), so a watcher
            // constructed for it must filter on that role instead.
            var changes = new List<string>();
            var watcher = Watcher(changes, watchedRole: Role.Multimedia);

            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-console}");
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "{device-multimedia}");

            Assert.Equal(new[] { "{device-multimedia}" }, changes);
        }

        [Fact]
        public void AfterDispose_NotificationsAreIgnored()
        {
            var changes = new List<string>();
            var watcher = Watcher(changes);

            // A callback can be in flight while Stop() unregisters; the watcher must not call back into a
            // session that is already tearing its output down.
            watcher.Dispose();
            watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "{device-b}");

            Assert.Empty(changes);
        }
    }
}
