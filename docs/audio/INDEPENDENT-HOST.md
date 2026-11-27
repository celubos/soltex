# Soltex audio channels

Open Soltex and choose **Audio**. **Mixer** holds the Soltex channels, stream mix and processing; **Microphone** opens
guided input setup; **Apps** controls Windows app sessions and per-app output. **Sonar** appears only when SteelSeries
Sonar is installed and is a separate, optional view. Everything except the Sonar tab works without vendor software.

Keep `Soltex.Audio.Host\Soltex.Audio.Host.Service.exe` with the rest of the extracted app. It is an unelevated helper
that Soltex starts when needed; it starts stopped.

## Set up and test a microphone

The **Microphone** tab shows your microphone as four steps: the microphone, its processing, where Soltex sends it, and
the name other apps choose.

1. Choose **Set up my microphone** (later **Change setup**). Under **Microphone**, pick the recording side of your
   microphone or interface, not its speakers or headphones.
2. If you know it, choose which input the microphone is plugged into. If you are not sure, leave it and run the test
   after saving.
3. Under **Send my voice to**, pick a virtual cable. With VB-CABLE, for example, Soltex plays into `CABLE Input` and
   your voice or recording app selects `CABLE Output` as its microphone; the card shows that name with **Copy name**.
   Without a virtual cable, other apps cannot receive your processed voice.
4. Pick a **Sound** and choose **Save**. Nothing changes before that, and saving never starts audio by itself; if audio
   is already on, the new setup is used at once. This saves only the microphone channel.
5. **Test my microphone** listens for 10 seconds while you speak and then says what it heard, with the loudest level.
   When audio is off it opens only the microphone; it never saves audio or plays anything back. If your voice arrives
   on one input of a stereo interface, the result offers **Use input 1 only** (or 2), which sends that input to both
   sides.
6. Choose **Start audio**. **Adjust sound…** opens the microphone's processing.

You can test a microphone without setting up the other channels.

## Channels and processing

For each Game, Chat, Media or Aux channel you enable, pick a separate capture source and a playback device. Apps play
into a virtual cable's playback side; Soltex captures its recording side. Choose **Apply routing**, then **Start
audio**. The view shows both what you asked for and what is actually running. Missing devices stay marked as
unavailable.

Every path converts between the two devices' sample rates with a high-quality resampler that also follows the drift
between their clocks, so a path left running does not slowly gain delay or drop out.

Faders and mutes act on running paths. Master leaves the microphone alone. **Game / Chat balance** affects only those
two channels. **Switch** changes the playback device of the enabled channels and keeps the microphone. **Devices** sets
priorities, exclusions, a pinned device and whether to return to the preferred device; apply routing to commit them.

**Processing** on each channel edits that channel's DSP: ten-band EQ with a response graph, headroom, gain, output
ceiling, gate, compressor, downward expander, noise suppression and stereo width/pan/crossfeed/swap/mono. Noise
suppression uses a noise floor you set; it is not machine-learning voice isolation, and turning it on adds about
20–40 ms of delay. **Smooth background** changes how it treats what is left: a steadier, slightly quieter background
instead of a faint warble. The **Safety limiter** catches peaks above the output ceiling, including peaks between
samples, instead of clipping them, and adds about 1.5 ms of delay. Both are off by default. Stereo controls have no
effect on mono sources. **Record / replay test** is a separate window with
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
