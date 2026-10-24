'use strict';

const STATUS_RU = {
    new: 'новая',
    in_progress: 'в работе',
    done: 'выполнена',
    cancelled: 'отменена',
    needs_review: 'на разборе',
};

const OUTCOME_RU = {
    created: 'задача создана',
    executed: 'команда исполнена',
    needs_review: 'на разборе',
    failed: 'ошибка',
};

const TAB_IN_RU = { tasks: 'задачи', review: 'разбор', intake: 'приём', compare: 'проверка', actions: 'действия' };

const state = {
    token: localStorage.getItem('kouki.token') || '',
    tab: 'tasks',
    status: '',
    taskId: null,
    timer: null,
};

const $ = (id) => document.getElementById(id);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
}[ch]));

class AuthError extends Error {}

async function api(path, options = {}) {
    const headers = Object.assign({}, options.headers);
    if (state.token) headers.Authorization = 'Bearer ' + state.token;
    if (options.body) headers['Content-Type'] = 'application/json; charset=utf-8';

    const response = await fetch(path, Object.assign({}, options, { headers }));
    if (response.status === 401) throw new AuthError('неверный или отсутствующий токен');
    if (!response.ok) {
        let detail = '';
        try {
            const data = await response.json();
            detail = data.error || data.title || JSON.stringify(data);
        } catch { detail = await response.text(); }
        throw new Error(`${response.status}: ${detail}`);
    }
    return response.status === 204 ? null : response.json();
}

function showBanner(message) {
    const banner = $('banner');
    banner.textContent = message;
    banner.hidden = !message;
}

function setServerStatus(text, kind) {
    const node = $('server-status');
    node.textContent = text;
    node.className = 'brand-sub' + (kind ? ' is-' + kind : '');
}

function setCount(id, value) {
    const node = $(id);
    if (node) node.textContent = Number.isFinite(value) ? String(value) : '—';
}

function formatTime(iso) {
    if (!iso) return '—';
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) return iso;
    const today = new Date().toDateString() === date.toDateString();
    return today
        ? date.toLocaleTimeString('ru-RU', { hour12: false })
        : date.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
}

/* ---------- сводка ---------- */

async function refreshTally() {
    const [tasks, review, actions] = await Promise.allSettled([
        api('/api/tasks?limit=200'),
        api('/api/classifications?unresolved=true&limit=100'),
        api('/api/actions?limit=100'),
    ]);

    setCount('count-tasks', tasks.status === 'fulfilled' ? tasks.value.length : NaN);
    setCount('count-review', review.status === 'fulfilled' ? review.value.length : NaN);
    setCount('count-actions', actions.status === 'fulfilled' ? actions.value.length : NaN);

    const auth = [tasks, review, actions].some((result) => result.status === 'rejected' && result.reason instanceof AuthError);
    if (auth) throw new AuthError('нет токена');
}

/* ---------- задачи ---------- */

async function loadTasks() {
    const query = state.status ? '?status=' + encodeURIComponent(state.status) : '';
    const tasks = await api('/api/tasks' + query + (query ? '&' : '?') + 'limit=200');
    const list = $('task-list');
    $('tasks-meta').textContent = tasks.length ? `${tasks.length} записей` : '';
    setCount('count-tasks', tasks.length);

    if (!tasks.length) {
        list.innerHTML = '<div class="empty">Пока пусто. Добавьте задачу через «Приём» или из Telegram.</div>';
        return;
    }

    list.innerHTML = tasks.map((task) => `
        <div class="card" data-task="${esc(task.id)}">
            <span class="status status-${esc(task.status)}">${esc(STATUS_RU[task.status] || task.status)}</span>
            <div class="card-main">
                <div class="card-title">${esc(task.title)}</div>
                <div class="card-sub">${esc(task.source)} · ${esc(formatTime(task.createdAt))}${
                    task.meta && task.meta.intent ? ' · ' + esc(task.meta.intent) : ''
                }</div>
            </div>
        </div>`).join('');
}

/* ---------- разбор ---------- */

async function loadReview() {
    const items = await api('/api/classifications?unresolved=true&limit=100');
    const list = $('review-list');
    $('review-meta').textContent = items.length ? `${items.length} фраз` : '';
    setCount('count-review', items.length);

    if (!items.length) {
        list.innerHTML = '<div class="empty">Всё разобрано — неуверенных фраз нет.</div>';
        return;
    }

    list.innerHTML = items.map((item) => `
        <div class="card" style="cursor:default">
            <div class="card-main">
                <div class="card-title">«${esc(item.inputText)}»</div>
                <div class="card-sub">
                    ${esc(item.source)} · ${esc(formatTime(item.createdAt))} ·
                    ${esc(item.intent)} / ${Number(item.score).toFixed(2)} · ${esc(item.model)}
                    ${item.error ? ' · <span style="color:var(--bad)">' + esc(item.error) + '</span>' : ''}
                </div>
                <div class="label-row">
                    ${['add_task', 'list_tasks', 'complete_task'].map((label) => `
                        <button class="btn small" data-label="${esc(label)}" data-id="${esc(item.id)}">${esc(label)}</button>`).join('')}
                    <input class="mono" data-custom="${esc(item.id)}" placeholder="своя метка" style="width:120px">
                    <button class="btn small" data-custom-save="${esc(item.id)}">пометить</button>
                </div>
            </div>
        </div>`).join('');
}

async function labelRecord(id, label) {
    if (!label) return;
    await api(`/api/classifications/${id}/label`, {
        method: 'POST',
        body: JSON.stringify({ label }),
    });
    await Promise.all([loadReview(), refreshTally()]);
}

/* ---------- действия ---------- */

async function loadActions() {
    const actions = await api('/api/actions?limit=100');
    const body = $('actions-table').querySelector('tbody');
    $('actions-meta').textContent = actions.length ? `${actions.length} записей` : '';
    setCount('count-actions', actions.length);

    if (!actions.length) {
        body.innerHTML = '<tr><td colspan="7" class="empty">Действий ещё не было.</td></tr>';
        return;
    }

    body.innerHTML = actions.map((action) => `
        <tr>
            <td class="mono">${esc(formatTime(action.createdAt))}</td>
            <td>${esc(action.source)}</td>
            <td class="outcome-${esc(action.outcome)}">${esc(OUTCOME_RU[action.outcome] || action.outcome)}</td>
            <td>${esc(action.intent || '')}</td>
            <td class="mono">${esc(action.command || '')}</td>
            <td class="mono">${action.durationMs ?? ''}</td>
            <td>${action.taskId
                ? `<a href="#" class="mono" data-task="${esc(action.taskId)}">${esc(String(action.taskId).slice(0, 8))}</a>`
                : '<span class="muted">—</span>'}</td>
        </tr>`).join('');
}

/* ---------- карточка задачи ---------- */

async function openTask(id) {
    state.taskId = id;
    $('detail').hidden = false;
    $('detail-title').textContent = 'Загрузка…';
    $('detail-body').innerHTML = '';

    const [task, actions] = await Promise.all([
        api(`/api/tasks/${id}`),
        api(`/api/actions?taskId=${id}&limit=100`),
    ]);

    $('detail-title').textContent = task.title;

    const buttons = ['in_progress', 'done', 'cancelled']
        .filter((status) => status !== task.status)
        .map((status) => `<button class="btn small" data-set-status="${esc(status)}">${esc(STATUS_RU[status])}</button>`)
        .join('');

    const timeline = actions.length
        ? actions.map((action) => `
            <div class="entry">
                <div class="entry-top">
                    <span class="outcome-${esc(action.outcome)}">${esc(OUTCOME_RU[action.outcome] || action.outcome)}</span>
                    <span class="muted mono">${esc(formatTime(action.createdAt))} · ${action.durationMs ?? '?'} мс</span>
                </div>
                <div class="entry-meta">
                    ${esc(action.source)}${action.command ? ' · команда ' + esc(action.command) : ''}
                    ${action.error ? ' · <span style="color:var(--bad)">' + esc(action.error) + '</span>' : ''}
                </div>
            </div>`).join('')
        : '<div class="empty">Действий по этой задаче нет.</div>';

    const meta = task.meta
        ? `<pre class="result" style="max-height:160px">${esc(JSON.stringify(task.meta, null, 2))}</pre>`
        : '<span class="muted">нет</span>';

    $('detail-body').innerHTML = `
        <dl class="kv">
            <dt>id</dt><dd class="mono">${esc(task.id)}</dd>
            <dt>статус</dt><dd><span class="status status-${esc(task.status)}">${esc(STATUS_RU[task.status] || task.status)}</span></dd>
            <dt>источник</dt><dd>${esc(task.source)}</dd>
            <dt>приоритет</dt><dd>${esc(task.priority)}</dd>
            <dt>создана</dt><dd>${esc(formatTime(task.createdAt))}</dd>
            <dt>изменена</dt><dd>${esc(formatTime(task.updatedAt))}</dd>
            <dt>фраза</dt><dd>«${esc(task.rawText)}»</dd>
        </dl>
        <div>
            <div class="section-title">Действия</div>
            <div class="timeline">${timeline}</div>
        </div>
        <div>
            <div class="section-title">Разбор (meta)</div>
            ${meta}
        </div>
        <div class="row-actions">${buttons}</div>`;
}

async function setTaskStatus(status) {
    if (!state.taskId) return;
    await api(`/api/tasks/${state.taskId}`, {
        method: 'PATCH',
        body: JSON.stringify({ status }),
    });
    await Promise.all([loadTasks(), openTask(state.taskId), refreshTally()]);
}

/* ---------- приём ---------- */

async function submitIntake(event) {
    event.preventDefault();
    const text = $('intake-text').value.trim();
    if (!text) return;

    const result = $('intake-result');
    result.hidden = false;
    result.textContent = 'Отправляю…';

    try {
        const response = await api('/api/intake', {
            method: 'POST',
            body: JSON.stringify({ text, source: $('intake-source').value }),
        });
        result.textContent = JSON.stringify(response, null, 2);
        $('intake-text').value = '';
        await Promise.all([refreshTally(), loadActions()]);
    } catch (error) {
        result.textContent = 'Ошибка: ' + error.message;
    }
}

/* ---------- проверка: сравнение классификаторов и распознавание файла ---------- */

const selectedClassifiers = new Set();

async function loadClassifiers() {
    const list = $('classifier-list');
    if (list.dataset.loaded === 'true') return;

    const response = await api('/api/classifiers');
    const names = response.classifiers || [];

    list.innerHTML = names.map((name) => {
        /* «+journal» пишет в журнал при каждом прогоне — по умолчанию выключен. */
        const checked = !name.includes('+journal');
        if (checked) selectedClassifiers.add(name);
        return `<button type="button" class="chip${checked ? ' is-active' : ''}" data-classifier="${esc(name)}">${esc(name)}</button>`;
    }).join('');

    list.dataset.loaded = 'true';
}

function renderCompare(response) {
    const body = $('compare-body');
    const results = response.results || [];

    if (results.length === 0) {
        body.innerHTML = '<tr><td colspan="6" class="meta">нет выбранных классификаторов</td></tr>';
        return;
    }

    body.innerHTML = results.map((item) => `<tr>
        <td>${esc(item.classifier)}</td>
        <td>${esc(item.intent)}</td>
        <td>${(item.score * 100).toFixed(0)} %</td>
        <td>${item.confident ? 'да' : 'нет'} (${(response.threshold * 100).toFixed(0)} %)</td>
        <td>${esc(item.model)}</td>
        <td class="err">${esc(item.error || '')}</td>
    </tr>`).join('');
}

async function compareText(event) {
    if (event) event.preventDefault();
    const text = $('compare-text').value.trim();
    const result = $('compare-result');
    if (!text) return;

    try {
        const body = { text, classifiers: Array.from(selectedClassifiers) };
        const response = await api('/api/classify', { method: 'POST', body: JSON.stringify(body) });
        renderCompare(response);
        result.hidden = true;
    } catch (error) {
        result.hidden = false;
        result.textContent = 'Ошибка: ' + error.message;
    }
}

async function transcribeFile() {
    const input = $('transcribe-file');
    const meta = $('transcribe-meta');
    const file = input.files && input.files[0];

    if (!file) {
        meta.textContent = 'выберите WAV-файл';
        return;
    }

    meta.textContent = 'распознаю…';

    try {
        const buffer = await file.arrayBuffer();
        const response = await fetch('/api/transcribe', {
            method: 'POST',
            headers: {
                'Content-Type': 'audio/wav',
                ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}),
            },
            body: buffer,
        });

        if (response.status === 401) throw new AuthError('401');

        const payload = await response.json().catch(() => ({}));

        if (!response.ok) {
            meta.textContent = payload.error || `код ${response.status}`;
            return;
        }

        meta.textContent = `услышано за ${payload.durationSec.toFixed(2)} с`;
        $('compare-text').value = payload.transcript || '';
        await compareText(null);
    } catch (error) {
        meta.textContent = error instanceof AuthError ? 'нужен токен' : 'ошибка: ' + error.message;
    }
}

/* ---------- переключение вкладок ---------- */

async function switchTab(tab) {
    state.tab = tab;
    document.querySelectorAll('.tile').forEach((node) => {
        node.classList.toggle('is-active', node.dataset.tab === tab);
    });
    $('panel-tasks').hidden = tab !== 'tasks';
    $('panel-review').hidden = tab !== 'review';
    $('panel-intake').hidden = tab !== 'intake';
    $('panel-compare').hidden = tab !== 'compare';
    $('panel-actions').hidden = tab !== 'actions';

    await reload();
}

async function reload() {
    try {
        showBanner('');
        await refreshTally();
        if (state.tab === 'tasks') await loadTasks();
        else if (state.tab === 'review') await loadReview();
        else if (state.tab === 'compare') await loadClassifiers();
        else if (state.tab === 'actions') await loadActions();
        if (state.taskId) await openTask(state.taskId);
    } catch (error) {
        if (error instanceof AuthError) {
            showBanner('Нужен bearer-токен: введите его справа и нажмите «сохранить».');
            setServerStatus('нет доступа', 'down');
        } else {
            showBanner('Ошибка: ' + error.message);
            setServerStatus('ошибка', 'down');
        }
    }
}

async function ping() {
    try {
        const health = await api('/api/health');
        setServerStatus('на связи · ' + formatTime(health.utc), 'up');
        showBanner('');
    } catch (error) {
        setServerStatus(error instanceof AuthError ? 'нет доступа' : 'нет связи', 'down');
        if (error instanceof AuthError) showBanner('Нужен bearer-токен: введите его справа и нажмите «сохранить».');
    }
}

/* ---------- события ---------- */

document.addEventListener('click', async (event) => {
    const target = event.target.closest('[data-task]');
    if (target) {
        event.preventDefault();
        await openTask(target.dataset.task);
        return;
    }

    const labelButton = event.target.closest('[data-label]');
    if (labelButton) {
        await labelRecord(labelButton.dataset.id, labelButton.dataset.label);
        return;
    }

    const customSave = event.target.closest('[data-custom-save]');
    if (customSave) {
        const id = customSave.dataset.customSave;
        const input = document.querySelector(`[data-custom="${id}"]`);
        await labelRecord(id, input.value.trim());
        return;
    }

    const statusButton = event.target.closest('[data-set-status]');
    if (statusButton) {
        await setTaskStatus(statusButton.dataset.setStatus);
    }
});

$('status-filter').addEventListener('click', async (event) => {
    const chip = event.target.closest('[data-status]');
    if (!chip) return;
    document.querySelectorAll('#status-filter .chip').forEach((node) => {
        node.classList.toggle('is-active', node === chip);
    });
    state.status = chip.dataset.status;
    await loadTasks();
});

document.querySelectorAll('.tile[data-tab]').forEach((node) => {
    node.addEventListener('click', () => switchTab(node.dataset.tab));
});

$('detail-close').addEventListener('click', () => {
    state.taskId = null;
    $('detail').hidden = true;
});

$('intake-form').addEventListener('submit', submitIntake);
$('compare-form').addEventListener('submit', compareText);
$('transcribe-btn').addEventListener('click', transcribeFile);

$('classifier-list').addEventListener('click', (event) => {
    const chip = event.target.closest('[data-classifier]');
    if (!chip) return;

    const name = chip.dataset.classifier;
    if (selectedClassifiers.has(name)) {
        selectedClassifiers.delete(name);
        chip.classList.remove('is-active');
    } else {
        selectedClassifiers.add(name);
        chip.classList.add('is-active');
    }
});

$('save-token').addEventListener('click', async () => {
    state.token = $('token').value.trim();
    localStorage.setItem('kouki.token', state.token);
    await ping();
    await reload();
});

$('refresh').addEventListener('click', reload);

document.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && state.tab === 'intake') {
        $('intake-form').requestSubmit();
    }
});

/* ---------- старт ---------- */

$('token').value = state.token;
switchTab('tasks').then(ping);
state.timer = setInterval(() => {
    if (document.hidden) return;
    if (state.tab === 'intake') return;
    reload();
}, 5000);

console.info('Kouki dashboard: вкладка', TAB_IN_RU[state.tab]);
