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

Nothing is sent over the internet. Soltex has no account and no telemetry.

Removing Soltex
---------------
1. Undo the changes you want to reverse: turn the features off again, or
   open History and choose Undo (or "Undo all..."). Administrator changes
   from Home > Full De-Windows are undone there with "Undo system
   changes". Apps you chose to remove are not reinstalled by Undo.
2. In Settings, turn off "Start when I sign in to Windows" if you turned
   it on.
3. Quit Soltex from the tray icon.
4. Delete this folder and the %LOCALAPPDATA%\Soltex folder.

Soltex is free and open source under the MIT License (LICENSE.txt).

More information, source code and issue reports:
https://github.com/celubos/soltex
