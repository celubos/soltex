# Soltex audio channels

Open Soltex and choose **Audio**. **Mixer** holds the Soltex channels, stream mix and processing; **Microphone** opens
guided input setup; **Apps** controls Windows app sessions and per-app output. **Sonar** appears only when SteelSeries
Sonar is installed and is a separate, optional view. Everything except the Sonar tab works without vendor software.

Keep `Soltex.Audio.Host\Soltex.Audio.Host.Service.exe` with the rest of the extracted app. It is an unelevated helper
that Soltex starts when needed; it starts stopped.

## Set up and test a microphone

1. Open **Microphone** (or **Set up microphone** in the Mixer). Pick a **capture** device: the recording side of your
   interface or microphone, not its speakers.
2. **Test input for 15 seconds** captures only, shows the measured peak and RMS for each input, and stops by itself. It
   saves no audio and plays nothing back. Stop audio first if that device is already in use by a channel.
3. If your microphone is on one socket of a stereo interface, choose **Input 1 → centered voice** or **Input 2 →
   centered voice** to send that input to both sides. The test suggests the input that had signal but does not change
   your choice.
4. Pick a return device for other apps to listen to. With VB-CABLE, for example, Soltex plays into `CABLE Input` and
   your voice or recording app selects `CABLE Output` as its microphone. **Use microphone route** saves only the
   microphone channel. If audio is stopped, choose **Start audio**; if it is running, the new route applies at once.

You can test a microphone without setting up the other channels. Sending processed voice to another app needs a real
return device.

## Channels and processing

For each Game, Chat, Media or Aux channel you enable, pick a separate capture source and a playback device. Apps play
into a virtual cable's playback side; Soltex captures its recording side. Choose **Apply routing**, then **Start
audio**. The view shows both what you asked for and what is actually running. Missing devices stay marked as
unavailable.

Faders and mutes act on running paths. Master leaves the microphone alone. **Game / Chat balance** affects only those
two channels. **Switch** changes the playback device of the enabled channels and keeps the microphone. **Devices** sets
priorities, exclusions, a pinned device and whether to return to the preferred device; apply routing to commit them.

**Processing** on each channel edits that channel's DSP: ten-band EQ with a response graph, headroom, gain, output
ceiling, gate, compressor, downward expander, noise suppression and stereo width/pan/crossfeed/swap/mono. Noise
suppression uses a noise floor you set; it is not machine-learning voice isolation, and turning it on adds about
20–40 ms of delay. Stereo controls have no effect on mono sources. **Record / replay test** is a separate window with
its own single path; it does not change channel processing.

## Stream mix

Open **Stream mix**. Each enabled source is captured once, processed once, then sent to two outputs with separate gain
and mute: **Personal** (what you hear) and **Stream** (what your streaming app captures). Choose different devices for
the two, select the stream device's recording side in your streaming app, apply, then choose **Start stream mix**.
Channel mode and stream mix cannot run at the same time.

Channel gain, mute and processing affect both sends; master and Game/Chat balance affect Personal only. The
microphone's Personal send starts muted. Gains and processing can change while running; changing sources, channels or
outputs requires **Stop audio** first.

One VB-CABLE is one transport, even though it has several playback pins. It cannot carry several independent buses, a
microphone return and a stream return at once. Soltex refuses known feedback loops, two channels on one transport, and
Sonar devices in this graph.

## Profiles, app routing and lifetime

Profiles store routes, device preferences, processing, master, balance and stream sends as versioned JSON, saved
atomically. Importing a profile only stages it; nothing plays until you apply and start. Device IDs are specific to one
PC, so an imported profile may show unavailable devices until you pick new ones.

**Apps → Route…** sends one app to a chosen output. Choose **Check route**, then **Apply output**. Windows may need the
app to restart its playback before the sound moves; Soltex never closes or restarts the app. Each route change keeps a
journal, and undo restores only values that still match what Soltex set.

Closing the window or leaving the page stops on-screen meters but not running audio. Quitting Soltex stops the helper
and every stream Soltex started. Loading a profile, opening a view or refreshing never starts playback.
