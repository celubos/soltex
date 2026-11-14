# Shared audio workspace

`Soltex.Audio.UI.MixerWorkspace` is a WPF `UserControl` used by both the suite and standalone audio app. It owns the actual mixer controls, controller, device/session observation, policy state, and independent Processing window. It never creates a tray icon, application singleton, hidden hosting window, or replacement `Application`.

```csharp
var mixer = new MixerWorkspace();
audioContent.Content = mixer;
await mixer.InitializeAsync(new MixerHostOptions
{
    PreferencesDirectory = Path.Combine(profileDirectory, "Audio"),
    EnableGlobalShortcuts = false
});
mixer.Activate();
// Navigation away or host hide:
mixer.Deactivate();
// Actual host exit, awaited before closing the real Window:
await mixer.ShutdownAsync();
```

Construction initializes only layout/models/scoped resources. Backend/device creation, preference loading and discovery require `InitializeAsync`, whose task is reused. An optional `MixerWorkspace(MixerHostOptions)` constructor supplies preferences/capabilities before initialization. `Activate`/`Deactivate` are idempotent presentation operations: meter and ChatMix polling pause while inactive; initialized background state/policy observation and explicitly started independent streams remain owned until shutdown. Hosts should cache one workspace instead of creating one per navigation.

The host owns its real Window, tray, singleton and close/hide policy. `AvailableOutputs` returns stable endpoint IDs with labels; `OutputsChanged` signals updates. `SelectLinkedOutputAsync(id)`, `TogglePlaybackMuteAsync()`, `ShowDevicePolicy()` and `ShowProcessing()` support host commands. `QuitRequested` reports Ctrl+Q inside the workspace; the host decides whether to quit. `IsBackendInitialized` and `IsActive` describe lifecycle state. `PreferredWindowSize` and `SaveHostWindowSize(Size)` support the standalone window. All methods run on the owning Dispatcher. `ShutdownAsync` is idempotent and awaits owned child Processing closure before disposing backend/device resources; `DisposeAsync` is equivalent. Merely hiding or deactivating the workspace does not invoke shutdown.

Processing/settings/device windows use `Window.GetWindow(this)` as their actual owner. Hosts must await workspace shutdown before final window closure. Processing is single-instance per workspace and loads its presets beneath the configured preferences directory. Audio resources are merged into the workspace and audio child windows only. Suite application resources and unrelated controls remain untouched.

A host that shares Processing across several mixer views supplies `MixerHostOptions.ProcessingRequested`. Both the Processing button and `ShowProcessing()` invoke that action without creating a workspace child or worker. The host then owns and awaits shutdown of its shared Processing window. Without the override, the standalone workspace retains its existing owned-window behavior.

Global shortcut capability defaults off. When enabled by the host, user settings determine registration on the real host HWND; registration ends at shutdown. Optional `GetStartupEnabled`/`SetStartupEnabled` delegates expose the host's login behavior in Settings. Without both delegates, no startup checkbox or registration is supplied. Default preferences retain standalone `%LOCALAPPDATA%/SoltexAudio`; the suite should supply its own profile subdirectory to prevent unrelated window/startup preferences from contending.

The workspace keeps backend boundaries: the five-bus mixer uses the optional installed Sonar development adapter, while independent Processing uses Soltex's PCM engine and explicit supplied endpoints. It installs no driver, changes no default endpoints during initialization, and does not enable GG/Sonar. Hosts have separate process singletons; running both hosts does not imply cross-process exclusion or exclusive audio control ownership.

Offline regression tests instantiate this actual control without backend initialization and verify layout, scoped themes, child ownership/reuse, processing presets and asynchronous shutdown. They do not claim live routing/hardware or mixed-monitor DPI acceptance.
