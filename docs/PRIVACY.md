# Kantor privacy policy

Last updated: October 16, 2026. (Versión en español: [PRIVACIDAD.md](PRIVACIDAD.md).)

Kantor is a local karaoke app for Windows. This policy explains what data the app uses and what happens to it.
In short: **Kantor sends no data to anyone. Everything stays on your PC.**

## Who is responsible

Pablo Pramparo, author of Kantor (<https://github.com/pablopramparo/kantor>). For questions, open an issue in that
repository.

## What data Kantor uses and where it stays

Everything is stored on your computer, in the `%APPDATA%\Kantor` folder (in the Microsoft Store version, inside the
private space Windows gives the package):

- **Microphone.** Kantor reads the microphone while you sing to detect the pitch of your voice and compute the score.
  **It does not record or save the audio, play it back, or transmit it.** Only the analysis result is kept in memory
  while the song plays.
- **Players and games.** The names you type, the chosen character, games, scores and the history of sung songs are
  stored in a local database (`kantor.db`). You can delete games and export or back up the data from the app.
- **Settings** (`settings.json`): language, audio devices, song folders and preferences.
- **Song library** (`library-index.json`): an index of the files in the folders you choose. Kantor includes no songs
  and does not copy or upload them anywhere.
- **Diagnostic log** (`kantor.log`): timestamps and technical messages to find problems in the app itself. It contains
  no audio and no content of your songs.
- **Custom characters**: if you load images, they are copied into the data folder.

## Internet access

Kantor **does not connect to the internet**: it has no accounts, advertising, usage statistics, error reports or
automatic updates, and uses no third-party services. The only connections are the two optional ones below, which are
local.

## Optional features (off by default)

- **Guest access over the local network.** If you turn it on, Kantor opens an HTTP server on your local network so
  phones can search songs, create a player (name and character) and add songs to the queue. It listens only on your
  network's private address, uses a temporary key carried in the QR that is revoked when you turn it off or close
  Kantor, and is not exposed to the internet. **The traffic is plain, unencrypted HTTP**: anyone who can observe your
  Wi-Fi could see it, so turn it on only on networks you control. Names typed by guests are stored as players in your
  local database. Kantor does not store the phones' addresses.
- **Remote control (Avyn).** A local channel (named pipe) reachable only by your Windows user, to control Kantor from
  another app on the same PC. It does not use the network.

## Who the data is shared with

No one. Since Kantor sends no data, there are no third parties receiving it. The data is yours: it is on your
computer and you control it.

## How to delete your data

Uninstalling Kantor and deleting the `%APPDATA%\Kantor` folder removes everything. Inside the app you can delete games
and archive players. In the Store version, uninstalling the package also deletes its data space.

## Children

Kantor collects no personal data from anyone, including children. If children use it, the names they type stay only on
the computer where it is used.

## Changes to this policy

If anything changes (for example, if a future version added a connection), this document and the date above will be
updated, and the app will not start sending data without clearly saying so.
