# Guest access over the LAN

Guests use their phones to **find a song, pick who sings it (or create their own player with a name and character)
and add it to the queue**. Nothing else.
Kantaria works exactly the same without this: the server is **off by default** and switches itself off when Kantaria
closes. (Spanish version: [INVITADOS.md](INVITADOS.md).)

## How to use it

1. On the **main menu** or the **song list** (bottom right) click **GUESTS · QR**. Access turns on and the QR
   appears. Click the QR to see it large ("JOIN THE PARTY" screen), where you can also **turn it off** (TURN OFF).
2. The phone must be on **the same Wi-Fi network** as the PC. Scanning the QR with the camera opens the page.
3. The guest searches, taps a song, picks singers (up to as many as there are microphones created in
   Options › Audio) and taps **ADD TO QUEUE**. "QUEUED!" appears with their position.
   A guest with no player yet taps **+ NEW PLAYER**, types a name, picks a character from the gallery and taps
   **CREATE & SELECT**: they land back on "Who's singing?" with the new player already picked (the song is only added
   when they tap ADD TO QUEUE).
4. To close: **TURN OFF** on the QR screen (or close Kantaria). The token is revoked and the port is closed.

If the PC has several networks, Kantaria picks the one with a gateway; you can choose another under
**Options › Game › Guest network**. If it changes while access is on, it reopens on the new one (with a new QR).

## What guests can do

| Allowed | Not allowed |
|---|---|
| Search songs (title, artist…, ignoring case and accents) | Temporary or guest profiles |
| Pick singers among **existing** players (those in the open session; with no session, the active ones) | Microphones, settings |
| **Create a new player**: a name and a character from the factory gallery | Edit, rename, archive or delete players (not even their own); upload custom images |
| Add the song to the queue | Play, pause, skip, stop; reorder or remove queue items |
| | History, scores, statistics, Avyn, files on the PC |

The first singer picked takes the first microphone created, the second takes the next, and so on. In a duet, each
microphone sings the voice it gets by the usual rule (it can be changed in "Who sings?" on the PC). If there are no
microphones the page says so: they are created on the PC. Players created from a phone are **normal players**: they
are stored in the same database, join the open session (like those created from "Who sings?" on the PC; when
singing solo they join none), show up at once on the PC, in Avyn and on other phones, and count in results, history
and rankings. Creating them changes neither the microphone selection nor the song in progress. Renaming or deleting
them is done only on the PC. When a queue item is sung from the PC
(SING NOW / NEXT), the singers the guest picked are preloaded in "Who sings?" if they are still valid.

## How it works

- **Kestrel** server in `Kantor.Core/Guest/`, listening **only on a private IPv4 address** (10/8, 172.16/12,
  192.168/16) of an active, non-virtual interface; never on `0.0.0.0` or loopback. Port 8765 (or the next free one).
- Everything goes through what already exists: `LibraryCatalog`/`LibrarySearch` (same search as the PC and Avyn),
  `KantorStore` (players), `QueueService` (queue) and `KantorController.GuestEnqueue`, which reuses the `queue.add`
  validation and runs on the UI thread, serialized with Avyn and the PC. Scoring, playback, audio and the Avyn
  contract are untouched (the pipe is not exposed).
- The page (`Guest/Web/`: HTML, CSS, plain JS) is served from **embedded resources by fixed name**, with Jersey 15,
  the logo and the colour and frame values of `Theme.xaml`, `PixelFrame` and `SongListView.xaml` as CSS variables.
  No CDN, no external services.
- Routes: `GET /` and `/assets/*` (closed list) and, with the token, `GET /api/config|songs|players|avatars`,
  `GET /api/cover/{id}`, `GET /api/avatar/{id}` `POST /api/queue` and `POST /api/players` (create only). Covers and avatars are requested by
  identifier, never by path.

## Security (and its limits)

- **Token** of 128 random bits (cryptographic), new every time access is turned on and revoked when it is turned
  off. It travels in the QR fragment (`#k=…`: the browser sends it to the server neither in the URL nor in
  `Referer`) and then in the `X-Kantor-Token` header. It is not accepted in the URL, never written to logs and only
  works for this feature. It is a **shared credential**: anyone with the link can use access while it is on.
- Rejects clients that are not on a private network; validates `Host` (anti-rebinding) and `Origin`; no CORS;
  CSP/`nosniff`/`no-referrer` headers; 1 KiB maximum body; strict JSON; per-client limits on requests (120 / 10 s)
  and on adds (6 / 30 s), a temporary block after 10 invalid tokens per minute, and at most 32 simultaneous requests.
- Creating players: the rules are the PC's (`KantorStore.CreatePlayer`: 1 to 24 characters, trimmed, unique among
  active players ignoring case), plus: no control characters and a character only from the factory gallery (user-loaded
  ones are neither offered nor accepted; with none, the generic one). Everything is validated on the server. Limit:
  5 per client every 10 minutes and 40 in total per activation.
- Double taps and retries: the same `requestId` (the page reuses it until the choice changes or the guest goes back
  to the list) adds **once**. A new request always adds, even for the same song with the same singers: repeating a
  song is legitimate.
- **HTTP is not encrypted**: anyone who can observe the Wi-Fi can see the token and the traffic. Don't use it on
  networks you don't control. A private IP is not enough to isolate from the internet: a router port forward would
  expose it, which is why Kantaria opens no ports (no UPnP) and rejects public clients. Don't publish it on the router.
- **Windows Firewall** asks the first time: allow it on **private networks** only.
- **Wi-Fi with client isolation** (guest networks, some routers, hotel or office networks) stops the phone from
  reaching the PC: use a network without isolation or another phone's hotspot.

## Verified and pending

See `docs/ESTADO.md` §8 duodecies (in Spanish).
