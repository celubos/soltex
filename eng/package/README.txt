Soltex
======

Soltex tidies up and personalizes Windows (taskbar, Start, File Explorer,
privacy, desktop) and adds an audio mixer and a few everyday tools, with
every change listed in History so you can undo it.

Requirements
------------
- Windows 10 version 2004 or later, or Windows 11
- 64-bit (x64) PC
- Nothing else to install: everything Soltex needs is in this folder.

Running Soltex
--------------
1. Unzip this folder anywhere you like, for example in Documents.
2. Run Soltex.exe.

(The setup.exe on the releases page installs Soltex for you instead,
with a Start menu entry and an uninstaller.)

Soltex lives in the notification area (system tray). Closing the window
hides it; to exit, right-click the tray icon and choose Quit.

Soltex runs as your normal user. A few changes need administrator rights;
for those, Windows shows its usual permission prompt first.

Windows SmartScreen warning
---------------------------
Soltex is not code-signed, so Windows SmartScreen may say it "protected
your PC" the first time you run it. If you downloaded this zip from the
Soltex releases page on GitHub, choose "More info" and then "Run anyway".

Your settings
-------------
Settings, the change history and audio profiles are stored in:

    %LOCALAPPDATA%\Soltex

The history of Home > Full De-Windows (administrator) changes is kept
where only administrators can change it:

    %ProgramData%\Soltex\System

Nothing is sent over the internet. Soltex has no account and no telemetry.

Removing Soltex
---------------
If you installed with setup.exe, uninstall from Settings > Apps >
Installed apps instead of steps 3 to 5 (do steps 1 and 2 first). The
uninstaller quits Soltex for you.

1. Undo the changes you want to reverse: turn the features off again, or
   open History and choose Undo (or "Undo all..."). Administrator changes
   from Home > Full De-Windows are undone there with "Undo system
   changes". Apps you chose to remove are not reinstalled by Undo.
2. If you changed the taskbar look, open Taskbar, set the look to
   "Windows default" and turn off the taskbar details you turned on.
   Otherwise the taskbar keeps its look until you sign out or restart.
3. In Settings, turn off "Start when I sign in to Windows" if you turned
   it on.
4. Quit Soltex from the tray icon.
5. Delete this folder.

Your settings stay in %LOCALAPPDATA%\Soltex and in the registry key
HKCU\Software\Soltex. To remove them too, sign out or restart Windows
first (the taskbar helper stays loaded in File Explorer until then),
then delete that folder and that key.

If you used Full De-Windows, its change history stays in
%ProgramData%\Soltex\System, so Soltex can still undo those changes
after a reinstall. Deleting that folder needs administrator rights, and
afterwards those changes can no longer be undone from Soltex. Undo them
first (step 1) if you want them reversed.

Soltex is free and open source under the MIT License (LICENSE.txt).

More information, source code and issue reports:
https://github.com/celubos/soltex
