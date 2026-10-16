// Página de invitados de Kantor: buscar una canción, elegir quién la canta y agregarla a la cola.
// Sin librerías. El texto de las canciones y los jugadores siempre entra por textContent.
(function () {
  'use strict';

  var TEXTS = {
    es: {
      find: 'BUSCAR CANCIÓN', placeholder: 'Artista o título…', more: 'MÁS', back: 'VOLVER',
      whoSings: '¿QUIÉN CANTA?', add: 'AGREGAR A LA COLA', adding: 'AGREGANDO…', queued: '¡EN COLA!',
      alreadyQueued: 'YA ESTABA EN COLA', retry: 'REINTENTAR', loading: 'CARGANDO…', noResults: 'SIN RESULTADOS',
      noResultsHint: 'Probá con otra palabra.', clear: 'Borrar',
      songs: function (n) { return n === 1 ? '1 canción' : n + ' canciones'; },
      pick: function (n) { return n === 1 ? 'Elegí quién canta.' : 'Elegí hasta ' + n + ' cantantes. El orden es el de los micrófonos.'; },
      position: function (n) { return 'Vas en el puesto ' + n; },
      scan: 'ESCANEÁ EL QR', scanHint: 'Abrí esta página con el código QR que muestra la pantalla de Kantor.',
      closed: 'ACCESO CERRADO', closedHint: 'El anfitrión cerró el acceso. Escaneá el QR otra vez.',
      offline: 'SIN CONEXIÓN', offlineHint: 'No se llega a Kantor. Revisá que estés en la misma red Wi-Fi.',
      noPlayers: 'TODAVÍA NO HAY JUGADORES', noPlayersHint: 'Creá el tuyo con + NUEVO JUGADOR.',
      newPlayer: '+ NUEVO JUGADOR', newPlayerTitle: 'NUEVO JUGADOR', yourName: 'TU NOMBRE',
      pickCharacter: 'ELEGÍ TU PERSONAJE', create: 'CREAR Y SELECCIONAR', creating: 'CREANDO…', cancel: 'CANCELAR',
      namePlaceholder: 'Tu nombre', created: '¡JUGADOR CREADO!', selectedNow: 'Ya está elegido para cantar',
      noMic: 'FALTA UN MICRÓFONO', noMicHint: 'En la PC: OPCIONES › Audio › Crear micrófono.',
      errors: {
        NAME_EMPTY: 'Escribí tu nombre.',
        NAME_TOO_LONG: 'El nombre es demasiado largo.',
        NAME_INVALID: 'El nombre tiene caracteres que no se pueden usar.',
        NAME_TAKEN: 'Ya hay un jugador con ese nombre. Probá con otro, o buscalo en la lista.',
        INVALID_AVATAR: 'Ese personaje no está disponible. Elegí otro.',
        TOO_MANY_SINGERS: 'Hay más cantantes que micrófonos.',
        PLAYER_NOT_AVAILABLE: 'Alguien ya no está en la partida. Actualizá la lista.',
        PLAYER_NOT_FOUND: 'Ese jugador ya no existe.',
        SONG_NOT_FOUND: 'Esa canción ya no está en la biblioteca.',
        QUEUE_FULL: 'La cola está llena.',
        RATE_LIMITED: 'Muy rápido. Esperá unos segundos.',
        NO_MICROPHONES: 'Falta un micrófono en la PC.',
        REQUEST_REUSED: 'Probá de nuevo.',
        INVALID_REQUEST: 'No se pudo enviar. Probá de nuevo.',
        UNAVAILABLE: 'Kantor no puede hacerlo ahora. Probá en un momento.',
        BUSY: 'Kantor está ocupado. Probá en un momento.'
      },
      generic: 'Algo salió mal. Probá de nuevo.'
    },
    en: {
      find: 'FIND A SONG', placeholder: 'Artist or title…', more: 'MORE', back: 'BACK',
      whoSings: "WHO'S SINGING?", add: 'ADD TO QUEUE', adding: 'ADDING…', queued: 'QUEUED!',
      alreadyQueued: 'ALREADY QUEUED', retry: 'RETRY', loading: 'LOADING…', noResults: 'NO RESULTS',
      noResultsHint: 'Try another word.', clear: 'Clear',
      songs: function (n) { return n === 1 ? '1 song' : n + ' songs'; },
      pick: function (n) { return n === 1 ? 'Pick who sings.' : 'Pick up to ' + n + ' singers. Order follows the microphones.'; },
      position: function (n) { return "You're number " + n; },
      scan: 'SCAN THE QR', scanHint: "Open this page with the QR code shown on Kantor's screen.",
      closed: 'ACCESS CLOSED', closedHint: 'The host closed access. Scan the QR again.',
      offline: 'NO CONNECTION', offlineHint: "Can't reach Kantor. Make sure you are on the same Wi-Fi.",
      noPlayers: 'NO PLAYERS YET', noPlayersHint: 'Create yours with + NEW PLAYER.',
      newPlayer: '+ NEW PLAYER', newPlayerTitle: 'NEW PLAYER', yourName: 'YOUR NAME',
      pickCharacter: 'PICK YOUR CHARACTER', create: 'CREATE & SELECT', creating: 'CREATING…', cancel: 'CANCEL',
      namePlaceholder: 'Your name', created: 'PLAYER CREATED!', selectedNow: 'Already picked to sing',
      noMic: 'MICROPHONE NEEDED', noMicHint: 'On the PC: OPTIONS › Audio › Create microphone.',
      errors: {
        NAME_EMPTY: 'Type your name.',
        NAME_TOO_LONG: 'That name is too long.',
        NAME_INVALID: 'The name has characters that can’t be used.',
        NAME_TAKEN: 'A player with that name already exists. Try another, or find it in the list.',
        INVALID_AVATAR: 'That character is not available. Pick another.',
        TOO_MANY_SINGERS: 'There are more singers than microphones.',
        PLAYER_NOT_AVAILABLE: 'Someone is no longer in the session. Refresh the list.',
        PLAYER_NOT_FOUND: 'That player no longer exists.',
        SONG_NOT_FOUND: 'That song is no longer in the library.',
        QUEUE_FULL: 'The queue is full.',
        RATE_LIMITED: 'Too fast. Wait a few seconds.',
        NO_MICROPHONES: 'The PC needs a microphone.',
        REQUEST_REUSED: 'Try again.',
        INVALID_REQUEST: "Couldn't send it. Try again.",
        UNAVAILABLE: "Kantor can't do that right now. Try again in a moment.",
        BUSY: 'Kantor is busy. Try again in a moment.'
      },
      generic: 'Something went wrong. Try again.'
    }
  };

  var SLOT_COLORS = ['#19D3FF', '#FF3DB8', '#5BE56B', '#A06BFF'];   // PlayerColors de Kantor (J1–J4)
  var PAGE = 30;

  var $ = function (id) { return document.getElementById(id); };
  var el = {
    lang: $('lang'), library: $('screenLibrary'), singers: $('screenSingers'), search: $('search'),
    clear: $('clear'), count: $('count'), songs: $('songs'), libState: $('libraryState'), more: $('more'),
    sentinel: $('sentinel'), back: $('back'), chosenCover: $('chosenCover'), chosenTitle: $('chosenTitle'),
    chosenArtist: $('chosenArtist'), hint: $('pickHint'), newPlayer: $('newPlayer'), create: $('create'),
    screenCreate: $('screenCreate'), createBack: $('createBack'), nameInput: $('nameInput'), avatars: $('avatars'),
    avatarBlock: $('avatarBlock'), createState: $('createState'), createNote: $('createNote'), players: $('players'), singState: $('singersState'),
    note: $('actionNote'), add: $('add'), toast: $('toast'), toastTitle: $('toastTitle'), toastSub: $('toastSub')
  };

  var state = {
    lang: (navigator.language || 'es').slice(0, 2) === 'en' ? 'en' : 'es',
    token: null, query: '', nextOffset: 0, total: 0, loading: false, seq: 0, abort: null,
    song: null, max: 0, players: [], selected: [], requestId: null, adding: false, closed: false, libraryScroll: 0,
    maxName: 24, avatarList: null, avatar: null, createId: null, creating: false, pendingSelect: null, afterBack: null
  };

  function t() { return TEXTS[state.lang]; }

  // ---- Token: llega en el fragmento del QR (nunca viaja al servidor en la URL) -----------------------------------

  function initToken() {
    var m = /[#&]k=([A-Za-z0-9_-]{16,64})/.exec(location.hash || '');
    if (m) {
      state.token = m[1];
      try { sessionStorage.setItem('kantor.k', m[1]); } catch (e) { /* sin almacenamiento: sigue en memoria */ }
      try { history.replaceState(null, '', location.pathname + location.search); } catch (e) { /* ignorar */ }
    } else {
      try { state.token = sessionStorage.getItem('kantor.k'); } catch (e) { state.token = null; }
    }
  }

  // ---- Red --------------------------------------------------------------------------------------------------

  function ApiError(code, status) { this.code = code; this.status = status; }

  function api(path, options) {
    options = options || {};
    var headers = { 'X-Kantor-Token': state.token || '' };
    if (options.body) { headers['Content-Type'] = 'application/json'; }
    return fetch(path, {
      method: options.method || 'GET', headers: headers, body: options.body, signal: options.signal,
      cache: 'no-store', credentials: 'omit', referrerPolicy: 'no-referrer'
    }).then(function (res) {
      if (res.status === 401) { state.closed = true; throw new ApiError('UNAUTHORIZED', 401); }
      return res.json().catch(function () { return {}; }).then(function (data) {
        if (!res.ok) { throw new ApiError(data.error || 'UNAVAILABLE', res.status); }
        return data;
      });
    });
  }

  function isOffline(e) { return !(e instanceof ApiError) && !(e && e.name === 'AbortError'); }

  // ---- Imágenes (con token: se piden con fetch y se muestran como blob) -------------------------------------------

  var blobs = new Map();
  var jobs = [];
  var active = 0;

  function pump() {
    while (active < 4 && jobs.length) {
      (function (job) {
        active++;
        fetch(job.url, { headers: { 'X-Kantor-Token': state.token || '' }, cache: 'force-cache', credentials: 'omit', referrerPolicy: 'no-referrer' })
          .then(function (r) { return r.ok ? r.blob() : Promise.reject(); })
          .then(function (b) {
            var u = URL.createObjectURL(b);
            blobs.set(job.url, u);
            if (blobs.size > 400) {
              var first = blobs.keys().next().value;
              URL.revokeObjectURL(blobs.get(first));
              blobs.delete(first);
            }
            job.done(u);
          })
          .catch(function () { /* queda el marcador */ })
          .then(function () { active--; pump(); });
      })(jobs.shift());
    }
  }

  var seen = typeof IntersectionObserver === 'function' ? new IntersectionObserver(function (entries) {
    entries.forEach(function (entry) {
      if (entry.isIntersecting) {
        seen.unobserve(entry.target);
        entry.target._load();
      }
    });
  }, { rootMargin: '300px' }) : null;

  function setImage(holder, url, alt) {
    var show = function (u) {
      var img = document.createElement('img');
      img.alt = alt || '';
      img.decoding = 'async';
      img.onload = function () { holder.textContent = ''; holder.appendChild(img); };   // si no carga, queda el marcador
      img.src = u;
    };
    if (blobs.has(url)) { show(blobs.get(url)); return; }
    holder._load = function () { jobs.push({ url: url, done: show }); pump(); };
    if (seen) { seen.observe(holder); } else { holder._load(); }
  }

  // ---- Textos -----------------------------------------------------------------------------------------------

  function applyTexts() {
    document.documentElement.lang = state.lang;
    document.querySelectorAll('[data-t]').forEach(function (n) { n.textContent = t()[n.getAttribute('data-t')]; });
    el.search.placeholder = t().placeholder;
    el.nameInput.placeholder = t().namePlaceholder;
    el.clear.setAttribute('aria-label', t().clear);
    el.lang.textContent = state.lang === 'es' ? 'EN' : 'ES';
    if (!state.adding) { el.add.textContent = t().add; }
    if (state.total || state.loadedOnce) { el.count.textContent = t().songs(state.total); }
    if (!el.singers.hidden) { renderHint(); }
    if (!state.creating) { el.create.textContent = t().create; }
  }

  // ---- Estados ----------------------------------------------------------------------------------------------

  function showState(box, kind, title, hint, retry) {
    box.className = 'state px state-box ' + kind;
    box.textContent = '';
    var h = document.createElement('h2'); h.textContent = title; box.appendChild(h);
    if (hint) { var p = document.createElement('p'); p.textContent = hint; box.appendChild(p); }
    if (retry) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'px px-button'; b.textContent = t().retry;
      b.addEventListener('click', retry);
      box.appendChild(b);
    }
    box.hidden = false;
  }

  function hideState(box) { box.hidden = true; box.textContent = ''; }

  function showFailure(box, e, retry) {
    if (state.closed) { showState(box, 'error', t().closed, t().closedHint); }
    else if (isOffline(e)) { showState(box, 'error', t().offline, t().offlineHint, retry); }
    else { showState(box, 'error', t().generic, null, retry); }
  }

  // ---- Pantalla A: biblioteca ---------------------------------------------------------------------------------

  function songRow(song) {
    var li = document.createElement('li');
    var b = document.createElement('button');
    b.type = 'button'; b.className = 'px song';
    var cover = document.createElement('div'); cover.className = 'cover'; cover.textContent = '♪'; cover.setAttribute('aria-hidden', 'true');
    var meta = document.createElement('div'); meta.className = 'meta';
    var name = document.createElement('div'); name.className = 'name'; name.textContent = song.title;
    var sub = document.createElement('div'); sub.className = 'sub';
    sub.textContent = song.artist + (song.edition ? ' · ' + song.edition : '');
    meta.appendChild(name); meta.appendChild(sub);
    var go = document.createElement('span'); go.className = 'go'; go.setAttribute('aria-hidden', 'true');
    b.appendChild(cover); b.appendChild(meta); b.appendChild(go);
    b.addEventListener('click', function () { chooseSong(song); });
    li.appendChild(b);
    if (song.cover) { setImage(cover, '/api/cover/' + encodeURIComponent(song.id) + '?v=' + encodeURIComponent(song.cover), ''); }
    return li;
  }

  function loadSongs(reset) {
    if (state.closed || !state.token) { showNoToken(); return; }
    if (reset) {
      if (state.abort) { state.abort.abort(); }
      state.nextOffset = 0; el.songs.textContent = ''; el.more.hidden = true; hideState(el.libState);
    } else if (state.loading || state.nextOffset === null) { return; }
    var seq = ++state.seq;
    var ctl = typeof AbortController === 'function' ? new AbortController() : null;
    state.abort = ctl; state.loading = true;
    if (reset || el.songs.children.length === 0) { showState(el.libState, 'loading', t().loading); }
    var url = '/api/songs?offset=' + state.nextOffset + '&limit=' + PAGE + '&q=' + encodeURIComponent(state.query);
    api(url, { signal: ctl ? ctl.signal : undefined }).then(function (data) {
      if (seq !== state.seq) { return; }
      state.loading = false; state.loadedOnce = true;
      hideState(el.libState);
      var frag = document.createDocumentFragment();
      data.items.forEach(function (s) { frag.appendChild(songRow(s)); });
      el.songs.appendChild(frag);
      state.total = data.total;
      state.nextOffset = data.nextOffset === null || data.nextOffset === undefined ? null : data.nextOffset;
      el.count.textContent = t().songs(data.total);
      el.more.hidden = state.nextOffset === null;
      if (data.total === 0) { showState(el.libState, 'empty', t().noResults, t().noResultsHint); }
    }).catch(function (e) {
      if (seq !== state.seq || (e && e.name === 'AbortError')) { return; }
      state.loading = false;
      showFailure(el.libState, e, function () { loadSongs(el.songs.children.length === 0); });
    });
  }

  function showNoToken() {
    el.songs.textContent = ''; el.more.hidden = true; el.count.textContent = '';
    if (state.closed) { showState(el.libState, 'error', t().closed, t().closedHint); }
    else { showState(el.libState, 'empty', t().scan, t().scanHint); }
  }

  var typing = null;
  el.search.addEventListener('input', function () {
    el.clear.hidden = el.search.value === '';
    clearTimeout(typing);
    typing = setTimeout(function () {
      var q = el.search.value.trim();
      if (q !== state.query) { state.query = q; loadSongs(true); }
    }, 250);
  });
  el.search.addEventListener('keydown', function (e) { if (e.key === 'Enter') { el.search.blur(); } });
  // Al desplazar la lista se guarda el teclado para ver los resultados.
  window.addEventListener('touchmove', function () {
    if (document.activeElement === el.search) { el.search.blur(); }
  }, { passive: true });
  el.clear.addEventListener('click', function () {
    el.search.value = ''; el.clear.hidden = true; state.query = ''; loadSongs(true); el.search.focus();
  });
  el.more.addEventListener('click', function () { loadSongs(false); });
  if (typeof IntersectionObserver === 'function') {
    new IntersectionObserver(function (entries) {
      if (entries[0].isIntersecting && !el.library.hidden) { loadSongs(false); }
    }, { rootMargin: '400px' }).observe(el.sentinel);
  }

  // ---- Pantalla B: cantantes ------------------------------------------------------------------------------------

  function newRequestId() {
    var bytes = new Uint8Array(16);
    (window.crypto || window.msCrypto).getRandomValues(bytes);
    return Array.prototype.map.call(bytes, function (b) { return ('0' + b.toString(16)).slice(-2); }).join('');
  }

  function chooseSong(song) {
    state.song = song; state.selected = []; state.players = []; state.max = 0;
    state.requestId = newRequestId(); state.adding = false;
    state.libraryScroll = window.scrollY;
    el.chosenTitle.textContent = song.title;
    el.chosenArtist.textContent = song.artist + (song.edition ? ' · ' + song.edition : '');
    el.chosenCover.textContent = '♪';
    if (song.cover) { setImage(el.chosenCover, '/api/cover/' + encodeURIComponent(song.id) + '?v=' + encodeURIComponent(song.cover), ''); }
    try { history.pushState({ screen: 'singers' }, ''); } catch (e) { /* sin historial: queda el botón VOLVER */ }
    el.players.textContent = ''; el.hint.textContent = ''; setNote('');
    showScreen('singers');
    loadPlayers();
  }

  // Tres pantallas: biblioteca → cantantes → nuevo jugador. El botón "atrás" del celular retrocede una, en vez de
  // salir de la página.
  function showScreen(name) {
    el.library.hidden = name !== 'library';
    el.singers.hidden = name !== 'singers';
    el.screenCreate.hidden = name !== 'create';
    window.scrollTo(0, name === 'library' ? state.libraryScroll : 0);
  }

  window.addEventListener('popstate', function (e) {
    showScreen((e.state && e.state.screen) || 'library');
    runAfterBack();
  });

  function runAfterBack() {
    var fn = state.afterBack; state.afterBack = null;
    if (fn) { fn(); }
  }

  function goBack(to, afterwards) {
    state.afterBack = afterwards || null;
    if (history.state && history.state.screen && history.state.screen !== to) { history.back(); return; }
    showScreen(to); runAfterBack();
  }

  function leaveSingers() { goBack('library'); }

  function renderHint() {
    el.hint.textContent = state.max > 0 && state.players.length ? t().pick(state.max) : '';
    el.add.textContent = state.adding ? t().adding : t().add;
  }

  function loadPlayers() {
    hideState(el.singState); el.add.disabled = true;
    showState(el.singState, 'loading', t().loading);
    api('/api/players').then(function (data) {
      hideState(el.singState);
      state.players = data.players; state.max = data.maxSingers; state.maxName = data.maxName || 24;
      applyPendingSelect();
      if (state.max === 0) { showState(el.singState, 'empty', t().noMic, t().noMicHint); renderHint(); return; }
      if (state.players.length === 0) { showState(el.singState, 'empty', t().noPlayers, t().noPlayersHint); renderHint(); return; }
      renderPlayers(); renderHint(); updateAdd();
    }).catch(function (e) { showFailure(el.singState, e, loadPlayers); });
  }

  function renderPlayers() {
    el.players.textContent = '';
    state.players.forEach(function (p) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'px player'; b.setAttribute('aria-pressed', 'false'); b.dataset.id = String(p.id);
      var face = document.createElement('div'); face.className = 'face';
      var pic = document.createElement('span'); pic.className = 'pic'; pic.textContent = (p.name || '?').slice(0, 1).toUpperCase();
      face.appendChild(pic);
      var order = document.createElement('span'); order.className = 'order'; order.setAttribute('aria-hidden', 'true');
      var who = document.createElement('div'); who.className = 'who'; who.textContent = p.name;
      b.appendChild(face); b.appendChild(who);
      b.addEventListener('click', function () { toggle(p.id); });
      face.appendChild(order);
      el.players.appendChild(b);
      if (p.avatar) { setImage(pic, '/api/avatar/' + encodeURIComponent(p.avatar), p.name); }
    });
    paintSelection();
  }

  function toggle(id) {
    if (state.adding) { return; }
    var i = state.selected.indexOf(id);
    if (i >= 0) { state.selected.splice(i, 1); setNote(''); }
    else if (state.selected.length >= state.max) { setNote(t().pick(state.max), true); return; }
    else { state.selected.push(id); setNote(''); }
    state.requestId = newRequestId();   // otra elección es otro pedido
    paintSelection(); updateAdd();
  }

  function paintSelection() {
    Array.prototype.forEach.call(el.players.children, function (b) {
      var pos = state.selected.indexOf(Number(b.dataset.id));
      b.setAttribute('aria-pressed', pos >= 0 ? 'true' : 'false');
      b.style.setProperty('--slot', pos >= 0 ? SLOT_COLORS[pos % SLOT_COLORS.length] : '');
      var order = b.querySelector('.order');
      if (order) { order.textContent = pos >= 0 ? String(pos + 1) : ''; }
    });
  }

  // El jugador recién creado queda elegido; si ya estaba completo el cupo, ocupa el lugar del último elegido.
  function applyPendingSelect() {
    var id = state.pendingSelect; state.pendingSelect = null;
    if (id === null || state.max === 0 || !state.players.some(function (p) { return p.id === id; })) { return; }
    if (state.selected.indexOf(id) < 0) {
      if (state.selected.length >= state.max) { state.selected.pop(); }
      state.selected.push(id);
      state.requestId = newRequestId();
    }
  }

  function updateAdd() { el.add.disabled = state.adding || state.selected.length === 0; }

  function setNote(text, bad) { el.note.textContent = text; el.note.className = 'note' + (bad ? ' bad' : ''); }

  el.add.addEventListener('click', function () {
    if (state.adding || state.selected.length === 0 || !state.song) { return; }
    state.adding = true; updateAdd(); renderHint(); setNote('');
    var body = JSON.stringify({ songId: state.song.id, playerIds: state.selected, requestId: state.requestId });
    api('/api/queue', { method: 'POST', body: body }).then(function (res) {
      state.adding = false;
      toast(res.duplicate ? t().alreadyQueued : t().queued, t().position(res.position));
      leaveSingers();
    }).catch(function (e) {
      state.adding = false; updateAdd(); renderHint();
      if (state.closed) { showFailure(el.singState, e); el.add.disabled = true; return; }
      var known = e instanceof ApiError && t().errors[e.code];
      setNote(known ? t().errors[e.code] : (isOffline(e) ? t().offlineHint : t().generic), true);
      if (e instanceof ApiError && (e.code === 'PLAYER_NOT_AVAILABLE' || e.code === 'PLAYER_NOT_FOUND')) { state.selected = []; loadPlayers(); }
    });
  });

  el.back.addEventListener('click', function () { if (!state.adding) { leaveSingers(); } });

  // ---- Pantalla C: nuevo jugador ------------------------------------------------------------------------------

  function setCreateNote(text, bad) { el.createNote.textContent = text; el.createNote.className = 'note' + (bad ? ' bad' : ''); }

  function updateCreate() { el.create.disabled = state.creating || el.nameInput.value.trim().length === 0; }

  function openCreate() {
    if (state.adding) { return; }
    state.createId = newRequestId(); state.creating = false; state.avatar = null;
    el.nameInput.value = ''; el.nameInput.maxLength = state.maxName;
    setCreateNote(''); hideState(el.createState); updateCreate();
    try { history.pushState({ screen: 'create' }, ''); } catch (e) { /* sin historial: queda CANCELAR */ }
    showScreen('create');
    loadAvatars();
  }

  // La galería es la de fábrica de Kantor (la misma de la PC); las imágenes se piden por identificador.
  function loadAvatars() {
    if (state.avatarList) { renderAvatars(); return; }
    api('/api/avatars').then(function (d) { state.avatarList = d.items || []; renderAvatars(); })
      .catch(function (e) {
        if (state.closed) { showFailure(el.createState, e); return; }
        state.avatarList = []; renderAvatars();   // sin galería se crea con el personaje genérico
      });
  }

  function renderAvatars() {
    var list = state.avatarList || [];
    el.avatarBlock.hidden = list.length === 0;
    el.avatars.textContent = '';
    list.forEach(function (id, i) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'px avatar'; b.dataset.id = id;
      b.setAttribute('role', 'radio'); b.setAttribute('aria-checked', 'false'); b.setAttribute('aria-label', '#' + (i + 1));
      var pic = document.createElement('span'); pic.className = 'pic'; pic.setAttribute('aria-hidden', 'true');
      b.appendChild(pic);
      b.addEventListener('click', function () { chooseAvatar(id); });
      el.avatars.appendChild(b);
      setImage(pic, '/api/avatar/' + encodeURIComponent(id), '');
    });
    paintAvatars();
  }

  function paintAvatars() {
    Array.prototype.forEach.call(el.avatars.children, function (b) {
      b.setAttribute('aria-checked', b.dataset.id === state.avatar ? 'true' : 'false');
    });
  }

  function chooseAvatar(id) {
    if (state.creating) { return; }
    state.avatar = id; state.createId = newRequestId(); setCreateNote('');
    paintAvatars();
  }

  el.newPlayer.addEventListener('click', openCreate);
  el.createBack.addEventListener('click', function () { if (!state.creating) { goBack('singers'); } });
  el.nameInput.addEventListener('input', function () { state.createId = newRequestId(); setCreateNote(''); updateCreate(); });
  el.nameInput.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') { e.preventDefault(); el.nameInput.blur(); if (!el.create.disabled) { el.create.click(); } }
  });

  el.create.addEventListener('click', function () {
    var name = el.nameInput.value.trim();
    if (state.creating || !name) { return; }
    state.creating = true; updateCreate(); el.create.textContent = t().creating; setCreateNote('');
    var body = JSON.stringify({ name: name, avatar: state.avatar, requestId: state.createId });
    api('/api/players', { method: 'POST', body: body }).then(function (res) {
      state.creating = false; el.create.textContent = t().create;
      state.pendingSelect = res.player.id;
      toast(t().created, res.player.name);
      goBack('singers', loadPlayers);   // vuelve a "¿Quién canta?" con el jugador nuevo ya elegido
    }).catch(function (e) {
      state.creating = false; el.create.textContent = t().create; updateCreate();
      if (state.closed) { showFailure(el.createState, e); el.create.disabled = true; return; }
      var known = e instanceof ApiError && t().errors[e.code];
      setCreateNote(known ? t().errors[e.code] : (isOffline(e) ? t().offlineHint : t().generic), true);
    });
  });

  // ---- Aviso -----------------------------------------------------------------------------------------------

  var toastTimer = null;
  function toast(title, sub) {
    el.toastTitle.textContent = title; el.toastSub.textContent = sub || '';
    el.toast.hidden = false;
    el.toast.classList.remove('show'); void el.toast.offsetWidth; el.toast.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.toast.hidden = true; }, 2200);
  }

  // ---- Idioma (solo de esta visita: no se guarda) --------------------------------------------------------------

  el.lang.addEventListener('click', function () { state.lang = state.lang === 'es' ? 'en' : 'es'; applyTexts(); });

  // ---- Fondo: cielo en bandas, piso con perspectiva y estrellas (PixelBackdrop), dibujado una vez ----------------------

  function drawBackdrop() {
    var c = $('backdrop');
    var scale = 4;
    var w = Math.max(1, Math.ceil(window.innerWidth / scale));
    var h = Math.max(1, Math.ceil(window.innerHeight / scale));
    c.width = w; c.height = h;
    var g = c.getContext('2d');
    if (!g) { return; }
    var top = [0x07, 0x0A, 0x24], bottom = [0x24, 0x12, 0x52];
    var floor = Math.floor(h * 0.82), bands = 12, i;
    for (i = 0; i < bands; i++) {
      var f = i / (bands - 1);
      g.fillStyle = 'rgb(' + top.map(function (v, k) { return Math.round(v + (bottom[k] - v) * f); }).join(',') + ')';
      g.fillRect(0, Math.floor(floor * i / bands), w, Math.ceil(floor / bands) + 1);
    }
    g.fillStyle = '#0C0826'; g.fillRect(0, floor, w, h - floor);
    g.fillStyle = '#4A329A'; g.fillRect(0, floor, w, 1);
    g.fillStyle = '#24185A';
    for (i = -12; i <= 12; i++) {
      for (var y = floor + 1; y < h; y++) {
        var x = Math.round(w / 2 + (i * 5 + (i * 17) * (y - floor) / (h - floor)));
        g.fillRect(x, y, 1, 1);
      }
    }
    for (i = 1; i <= 3; i++) { g.fillRect(0, floor + Math.floor((h - floor) * Math.pow(i / 4, 1.6)), w, 1); }
    // reflector
    var cx = Math.floor(w / 2), spread = Math.floor(h * 0.3);
    g.fillStyle = 'rgba(25,211,255,0.19)';
    for (y = 0; y < floor; y++) {
      var half = 3 + Math.floor((spread - 3) * y / floor);
      g.fillRect(cx - half, y, half * 2, 1);
    }
    // estrellas fijas (misma idea que PixelBackdrop: pocas, 1 px, algunas doradas)
    var seed = 7;
    function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; }
    for (i = 0; i < 46; i++) {
      g.fillStyle = i % 5 === 0 ? '#FFC933' : '#F4F6FF';
      g.fillRect(Math.floor(rnd() * w), Math.floor(rnd() * floor * 0.7), 1, 1);
    }
  }

  var resizeTimer = null;
  window.addEventListener('resize', function () { clearTimeout(resizeTimer); resizeTimer = setTimeout(drawBackdrop, 200); });

  // ---- Arranque ----------------------------------------------------------------------------------------------

  try { if (history.state) { history.replaceState(null, ''); } } catch (e) { /* ignorar */ }
  initToken();
  drawBackdrop();
  applyTexts();
  if (!state.token) { showNoToken(); return; }
  api('/api/config').then(function (cfg) {
    state.lang = cfg.language === 'en' ? 'en' : 'es';   // el idioma de Kantor como valor inicial
    applyTexts();
    loadSongs(true);
  }).catch(function (e) {
    if (state.closed) { showNoToken(); } else { showFailure(el.libState, e, function () { location.reload(); }); }
  });
})();
