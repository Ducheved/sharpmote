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
    btnClose: document.getElementById('btnClose')
};

function fmt(ms) {
    if (!ms || ms <= 0) return '0:00';
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const r = s % 60;
    return `${m}:${r.toString().padStart(2, '0')}`;
}

function getApiKey() {
    return localStorage.getItem('sharpmote_api_key') || '';
}
function setApiKey(v) {
    localStorage.setItem('sharpmote_api_key', v);
}

async function fetchApi(path, method = 'GET', body) {
    const key = getApiKey();
    if (!key) throw new Error('API key not set');
    const res = await fetch(path, {
        method,
        headers: {
            'Content-Type': 'application/json',
            'X-Api-Key': key
        },
        body: body ? JSON.stringify(body) : undefined
    });
    if (!res.ok) throw new Error(await res.text());
    return res.json();
}

function connectSse() {
    const key = getApiKey();
    if (!key) return;
    const es = new EventSource(`/events?api_key=${encodeURIComponent(key)}`);
    es.addEventListener('state', e => {
        const data = JSON.parse(e.data);
        applyState(data);
    });
    es.addEventListener('volume', e => {
        const data = JSON.parse(e.data);
        applyVolume(data);
    });
    es.addEventListener('track', e => {
        const data = JSON.parse(e.data);
        applyTrack(data);
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
function applyTrack(t) {
    applyState(t);
}

async function refresh() {
    try {
        const s = await fetchApi('/api/v1/state');
        applyState(s);
        applyVolume(s);
    } catch (e) { }
}

function debounce(fn, ms) {
    let tid;
    return (...args) => {
        clearTimeout(tid);
        tid = setTimeout(() => fn(...args), ms);
    };
}

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

els.btnSettings.onclick = () => {
    els.apiKeyInput.value = getApiKey();
    els.settings.classList.remove('hidden');
};
els.btnSave.onclick = () => {
    setApiKey(els.apiKeyInput.value.trim());
    els.settings.classList.add('hidden');
    refresh();
    connectSse();
};
els.btnClose.onclick = () => els.settings.classList.add('hidden');

if (!getApiKey()) els.settings.classList.remove('hidden');
refresh();
connectSse();
