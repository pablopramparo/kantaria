# Guest access over the LAN

Guests use their phones to **find a song, pick who sings it and add it to the queue**. Nothing else.
Kantor works exactly the same without this: the server is **off by default** and switches itself off when Kantor
closes. (Spanish version: [INVITADOS.md](INVITADOS.md).)

## How to use it

1. On the **main menu** or the **song list** (bottom right) click **GUESTS · QR**. Access turns on and the QR
   appears. Click the QR to see it large ("JOIN THE PARTY" screen), where you can also **turn it off** (TURN OFF).
2. The phone must be on **the same Wi-Fi network** as the PC. Scanning the QR with the camera opens the page.
3. The guest searches, taps a song, picks singers (up to as many as there are microphones created in
   Options › Audio) and taps **ADD TO QUEUE**. "QUEUED!" appears with their position.
4. To close: **TURN OFF** on the QR screen (or close Kantor). The token is revoked and the port is closed.

If the PC has several networks, Kantor picks the one with a gateway; you can choose another under
**Options › Game › Guest network**. If it changes while access is on, it reopens on the new one (with a new QR).

## What guests can do

| Allowed | Not allowed |
|---|---|
| Search songs (title, artist…, ignoring case and accents) | Create, edit or delete players; temporary profiles |
| Pick singers among **existing** players (those in the open session; with no session, the active ones) | Microphones, avatars, settings |
| Add the song to the queue | Play, pause, skip, stop; reorder or remove queue items |
| | History, scores, statistics, Avyn, files on the PC |

The first singer picked takes the first microphone created, the second takes the next, and so on. In a duet, each
microphone sings the voice it gets by the usual rule (it can be changed in "Who sings?" on the PC). If there are no
players or no microphones the page says so: they are created on the PC. When a queue item is sung from the PC
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
- Routes: `GET /` and `/assets/*` (closed list) and, with the token, `GET /api/config|songs|players`,
  `GET /api/cover/{id}`, `GET /api/avatar/{id}` and `POST /api/queue`. Covers and avatars are requested by
  identifier, never by path.

## Security (and its limits)

- **Token** of 128 random bits (cryptographic), new every time access is turned on and revoked when it is turned
  off. It travels in the QR fragment (`#k=…`: the browser sends it to the server neither in the URL nor in
  `Referer`) and then in the `X-Kantor-Token` header. It is not accepted in the URL, never written to logs and only
  works for this feature. It is a **shared credential**: anyone with the link can use access while it is on.
- Rejects clients that are not on a private network; validates `Host` (anti-rebinding) and `Origin`; no CORS;
  CSP/`nosniff`/`no-referrer` headers; 1 KiB maximum body; strict JSON; per-client limits on requests (120 / 10 s)
  and on adds (6 / 30 s), a temporary block after 10 invalid tokens per minute, and at most 32 simultaneous requests.
- Double taps and retries: the same `requestId` (the page reuses it until the choice changes or the guest goes back
  to the list) adds **once**. A new request always adds, even for the same song with the same singers: repeating a
  song is legitimate.
- **HTTP is not encrypted**: anyone who can observe the Wi-Fi can see the token and the traffic. Don't use it on
  networks you don't control. A private IP is not enough to isolate from the internet: a router port forward would
  expose it, which is why Kantor opens no ports (no UPnP) and rejects public clients. Don't publish it on the router.
- **Windows Firewall** asks the first time: allow it on **private networks** only.
- **Wi-Fi with client isolation** (guest networks, some routers, hotel or office networks) stops the phone from
  reaching the PC: use a network without isolation or another phone's hotspot.

## Verified and pending

See `docs/ESTADO.md` §8 duodecies (in Spanish).
