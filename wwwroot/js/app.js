const els = {
    title: document.getElementById('title'),
    artist: document.getElementById('artist'),
    app: document.getElementById('app'),
    pos: document.getElementById('pos'),
    dur: document.getElementById('dur'),
    bar: document.getElementById('barInner'),
    mute: document.getElementById('mute'),
    volume: document.getElementById('volume'),
    percent: document.getElementById('percent'),
    btnPrev: document.getElementById('btnPrev'),
    btnToggle: document.getElementById('btnToggle'),
    btnNext: document.getElementById('btnNext'),
    btnVolUp: document.getElementById('btnVolUp'),
    btnVolDown: document.getElementById('btnVolDown'),
    btnMute: document.getElementById('btnMute'),
    btnSettings: document.getElementById('btnSettings'),
    settings: document.getElementById('settings'),
    apiKeyInput: document.getElementById('apiKeyInput'),
    btnSave: document.getElementById('btnSave'),
    btnClose: document.getElementById('btnClose'),
    coverImg: document.getElementById('coverImg'),
    coverPh: document.getElementById('coverPh'),
    elapsed: document.getElementById('elapsed'),
    left: document.getElementById('left')
};

function fmt(ms) {
    if (!ms || ms <= 0) return '0:00';
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const r = s % 60;
    return `${m}:${r.toString().padStart(2, '0')}`;
}

function getApiKey() { return localStorage.getItem('sharpmote_api_key') || ''; }
function setApiKey(v) { localStorage.setItem('sharpmote_api_key', v); }

async function fetchApi(path, method = 'GET', body) {
    const key = getApiKey();
    if (!key) throw new Error('API key not set');
    const res = await fetch(path, {
        method,
        headers: { 'Content-Type': 'application/json', 'X-Api-Key': key },
        body: body ? JSON.stringify(body) : undefined
    });
    if (!res.ok) throw new Error(await res.text());
    return res;
}

function connectSse() {
    const key = getApiKey();
    if (!key) return;
    const es = new EventSource(`/events?api_key=${encodeURIComponent(key)}`);
    es.addEventListener('state', e => applyState(JSON.parse(e.data)));
    es.addEventListener('volume', e => applyVolume(JSON.parse(e.data)));
    es.addEventListener('track', async e => {
        const t = JSON.parse(e.data);
        applyState(t);
        await loadAlbumArt();
    });
    es.onerror = () => { };
}

function applyState(s) {
    if (!s) return;
    els.title.textContent = s.title || '–';
    els.artist.textContent = s.artist || '–';
    els.app.textContent = s.app || '–';
    els.pos.textContent = fmt(s.position_ms);
    els.dur.textContent = fmt(s.duration_ms);
    const pct = s.duration_ms > 0 ? Math.min(100, Math.max(0, Math.round(s.position_ms / s.duration_ms * 100))) : 0;
    els.bar.style.width = pct + '%';
    els.elapsed.textContent = 'Прошло ' + fmt(s.position_ms);
    const leftMs = Math.max(0, (s.duration_ms || 0) - (s.position_ms || 0));
    els.left.textContent = 'Осталось ' + fmt(leftMs);
}

function applyVolume(v) {
    if (typeof v.volume === 'number') {
        const p = Math.round(v.volume * 100);
        els.volume.value = p;
        els.percent.textContent = p + '%';
    }
    if (typeof v.mute === 'boolean') {
        els.mute.textContent = v.mute ? '🔇' : '🔊';
    }
}

async function loadAlbumArt() {
    try {
        const res = await fetchApi('/api/v1/albumart');
        if (res.status === 204) { showPlaceholder(); return; }
        const blob = await res.blob();
        if (!blob || blob.size === 0) { showPlaceholder(); return; }
        const url = URL.createObjectURL(blob);
        els.coverImg.src = url;
        els.coverImg.onload = () => URL.revokeObjectURL(url);
        els.coverImg.classList.remove('hidden');
        els.coverPh.classList.add('hidden');
    } catch { showPlaceholder(); }
}

function showPlaceholder() {
    els.coverImg.classList.add('hidden');
    els.coverPh.classList.remove('hidden');
}

async function refresh() {
    try {
        const s = await (await fetchApi('/api/v1/state')).json();
        applyState(s);
        applyVolume(s);
        await loadAlbumArt();
    } catch { }
}

function debounce(fn, ms) { let tid; return (...a) => { clearTimeout(tid); tid = setTimeout(() => fn(...a), ms); }; }

els.btnPrev.onclick = () => fetchApi('/api/v1/prev', 'POST');
els.btnToggle.onclick = () => fetchApi('/api/v1/toggle', 'POST');
els.btnNext.onclick = () => fetchApi('/api/v1/next', 'POST');
els.btnVolUp.onclick = () => fetchApi('/api/v1/volume/step', 'POST', { delta: 0.05 });
els.btnVolDown.onclick = () => fetchApi('/api/v1/volume/step', 'POST', { delta: -0.05 });
els.btnMute.onclick = () => fetchApi('/api/v1/volume/mute', 'POST');

els.volume.oninput = debounce(() => {
    const level = parseInt(els.volume.value, 10) / 100;
    els.percent.textContent = Math.round(level * 100) + '%';
    fetchApi('/api/v1/volume/set', 'POST', { level });
}, 80);

els.btnSettings.onclick = () => { els.apiKeyInput.value = getApiKey(); els.settings.classList.remove('hidden'); };
els.btnSave.onclick = () => { setApiKey(els.apiKeyInput.value.trim()); els.settings.classList.add('hidden'); refresh(); connectSse(); };
els.btnClose.onclick = () => els.settings.classList.add('hidden');

if (!getApiKey()) els.settings.classList.remove('hidden');
refresh();
connectSse();
